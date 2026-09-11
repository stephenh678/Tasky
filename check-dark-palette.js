#!/usr/bin/env node
// Run before every Tasky Web release, alongside check-cache-version.js:
//   node check-dark-palette.js        # fails if the two dark-palette blocks in styles.css differ
//   node check-dark-palette.js --fix  # regenerates the second block from the first
//
// docs/css/styles.css defines the dark palette twice: once under @media (prefers-color-scheme:
// dark) for "match system", and once under :root[data-theme="dark"] for "force dark". A media
// query can't be combined with an attribute selector in one rule, and Tasky Web deliberately has
// no build step or preprocessor to define the palette once, so the second block is a verbatim
// copy of the first - which is exactly the kind of by-hand duplication that drifts. This script
// treats the media-query block as the source of truth and checks (or rewrites) the forced block
// against it, custom property by custom property. `color-scheme: dark;` is the one line the forced
// block has that the media block doesn't, and it's preserved.
const fs = require('fs');
const path = require('path');

const CSS_PATH = path.join(__dirname, 'docs', 'css', 'styles.css');
const MEDIA_RE = /@media \(prefers-color-scheme: dark\) \{\r?\n\s*:root:not\(\[data-theme="light"\]\) \{\r?\n([\s\S]*?)\r?\n\s*\}\r?\n\}/;
const FORCED_RE = /(:root\[data-theme="dark"\] \{\r?\n)([\s\S]*?)(\r?\n\})/;

function parseDeclarations(block) {
  const decls = new Map();
  for (const line of block.split(/\r?\n/)) {
    const m = line.trim().match(/^(--[\w-]+):\s*(.+?);$/);
    if (m) decls.set(m[1], m[2]);
  }
  return decls;
}

const css = fs.readFileSync(CSS_PATH, 'utf8');
const eol = css.includes('\r\n') ? '\r\n' : '\n';
const mediaMatch = css.match(MEDIA_RE);
const forcedMatch = css.match(FORCED_RE);
if (!mediaMatch || !forcedMatch) {
  console.error('Could not find both dark-palette blocks in docs/css/styles.css - did the selectors change? Update this script to match.');
  process.exit(1);
}

const source = parseDeclarations(mediaMatch[1]);
const forced = parseDeclarations(forcedMatch[2]);
if (source.size === 0) {
  console.error('The @media (prefers-color-scheme: dark) block has no custom properties - check this script still matches styles.css.');
  process.exit(1);
}

const problems = [];
for (const [name, value] of source) {
  if (!forced.has(name)) problems.push(`  ${name} is missing from :root[data-theme="dark"]`);
  else if (forced.get(name) !== value) problems.push(`  ${name}: media block has "${value}", forced block has "${forced.get(name)}"`);
}
for (const name of forced.keys()) {
  if (!source.has(name)) problems.push(`  ${name} is only in :root[data-theme="dark"] (not in the media block)`);
}

if (problems.length === 0) {
  console.log(`OK: both dark-palette blocks in styles.css agree on all ${source.size} custom properties.`);
  process.exit(0);
}

if (!process.argv.includes('--fix')) {
  console.error(`MISMATCH: the two dark-palette blocks in docs/css/styles.css differ:`);
  for (const p of problems) console.error(p);
  console.error('Run: node check-dark-palette.js --fix   (regenerates the forced block from the media block)');
  process.exit(1);
}

const regenerated = ['  color-scheme: dark;', ...[...source].map(([name, value]) => `  ${name}: ${value};`)].join(eol);
const updated = css.replace(FORCED_RE, (_, open, _body, close) => `${open}${regenerated}${close}`);
fs.writeFileSync(CSS_PATH, updated, 'utf8');
console.log(`Rewrote :root[data-theme="dark"] from the media block (${source.size} custom properties). Fixed:`);
for (const p of problems) console.log(p);
