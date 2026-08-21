#!/usr/bin/env node
// Run before every Tasky Web release: `node check-cache-version.js`.
//
// Tasky Web has no build step (deliberately - see reference_tasky_web memory), so its cache-bust
// "?v=NN" suffix is duplicated by hand across every <script>/<link>/import in docs/ instead of
// coming from one source of truth. A missed bump on any single occurrence serves a stale cached
// module after a release and has already caused a real bug once (a "NOT_SIGNED_IN after login"
// failure from mismatched cache versions loaded at different times). This script doesn't remove
// the manual step - that would need real build tooling, which this project deliberately doesn't
// have - it just catches a miss before it ships, by checking every occurrence agrees.
const fs = require('fs');
const path = require('path');

const DOCS_DIR = path.join(__dirname, 'docs');
const SCAN_EXTENSIONS = new Set(['.html', '.js', '.css']);
const VERSION_RE = /\?v=(\d+)/g;

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (SCAN_EXTENSIONS.has(path.extname(entry.name))) out.push(full);
  }
  return out;
}

const found = []; // { file, line, version }
for (const file of walk(DOCS_DIR)) {
  const lines = fs.readFileSync(file, 'utf8').split('\n');
  lines.forEach((lineText, i) => {
    for (const m of lineText.matchAll(VERSION_RE)) {
      found.push({ file: path.relative(__dirname, file), line: i + 1, version: m[1] });
    }
  });
}

if (found.length === 0) {
  console.error('No "?v=NN" occurrences found under docs/ - did the cache-bust scheme change? Check this script still matches reality.');
  process.exit(1);
}

const versions = new Set(found.map((f) => f.version));
if (versions.size === 1) {
  console.log(`OK: all ${found.length} occurrence(s) of "?v=" agree on v${[...versions][0]}.`);
  process.exit(0);
}

console.error(`MISMATCH: found ${versions.size} different cache-bust versions across ${found.length} occurrence(s):`);
for (const { file, line, version } of found) {
  console.error(`  v${version}  ${file}:${line}`);
}
process.exit(1);
