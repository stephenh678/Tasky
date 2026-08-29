#!/usr/bin/env node
// Regenerates the live, filterable roadmap page from ROADMAP.md's master table.
//
// Run after any edit to ROADMAP.md's Status column (or the table generally):
//   node roadmap-artifact/build.js
// then publish roadmap-artifact/out.html via Claude's Artifact tool with
// url: https://claude.ai/code/artifact/d1cad8b5-5463-485b-9a6c-9e8f8f3f3584
// (same URL every time - that's what keeps the link stable instead of spawning a new artifact).
//
// This script only produces the HTML; it can't publish it itself - there's no public API for
// updating a claude.ai artifact from outside a Claude session, only the Artifact tool a session
// calls directly. So the actual publish step is still manual (a session doing it, not a person),
// same as the rest of Tasky Web's deliberately build-step-free design (see check-cache-version.js).
//
// template.html carries the page's design (tokens, layout, filter/search/expand behavior) with a
// single __ROADMAP_DATA__ placeholder; this script's only job is turning the markdown table into
// that JSON and splicing it in - it does not know or care about styling.
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const ROADMAP_PATH = path.join(ROOT, 'ROADMAP.md');
const TEMPLATE_PATH = path.join(__dirname, 'template.html');
const OUT_PATH = path.join(__dirname, 'out.html');

function parseRoadmapTable(markdown) {
  const lines = markdown.split('\n');
  let inTable = false;
  const rows = [];
  for (const line of lines) {
    if (line.startsWith('| # |')) {
      inTable = true;
      continue;
    }
    if (inTable && line.startsWith('|---')) continue;
    if (!inTable) continue;
    if (!line.trim().startsWith('|')) break; // table ended

    const parts = line.trim().replace(/^\||\|$/g, '').split('|').map((s) => s.trim());
    if (parts.length < 9) continue;
    const [rank, rec, platform, flag, pri, effort, category, status, ...rest] = parts;
    rows.push({
      rank: Number(rank),
      rec,
      platform,
      flag: flag.trim() === '⚡',
      pri,
      effort,
      category,
      status: status.replace(/\*\*/g, '').trim(),
      // A context cell can itself contain literal "|" only inside inline code/links, which this
      // table has never used - rejoining any split-on-those pieces is a safe, simple rebuild.
      context: rest.join('|'),
    });
  }
  return rows;
}

const markdown = fs.readFileSync(ROADMAP_PATH, 'utf-8');
const rows = parseRoadmapTable(markdown);

if (rows.length === 0) {
  console.error('Parsed 0 rows from ROADMAP.md - did the table header/format change? Check this script still matches "| # | Recommendation | Platform | ⚡ | Pri | Effort | Category | Status | Context |".');
  process.exit(1);
}

const template = fs.readFileSync(TEMPLATE_PATH, 'utf-8');
const html = template.replace('__ROADMAP_DATA__', JSON.stringify(rows));
fs.writeFileSync(OUT_PATH, html);

const byStatus = {};
for (const r of rows) byStatus[r.status] = (byStatus[r.status] || 0) + 1;
console.log(`Parsed ${rows.length} rows: ${Object.entries(byStatus).map(([k, v]) => `${v} ${k}`).join(', ')}.`);
console.log(`Wrote ${OUT_PATH} (${html.length.toLocaleString()} bytes).`);
console.log('Next: publish it via the Artifact tool with url: https://claude.ai/code/artifact/d1cad8b5-5463-485b-9a6c-9e8f8f3f3584');
