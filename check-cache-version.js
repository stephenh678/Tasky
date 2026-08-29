#!/usr/bin/env node
// Run before every Tasky Web release: `node check-cache-version.js`.
//
// Tasky Web has no build step (deliberately - see reference_tasky_web memory), so two things are
// kept in sync by hand instead of by tooling: the "?v=NN" cache-bust suffix duplicated across
// every <script>/<link>/import in docs/, and docs/js/config.js's DESKTOP_VERSION, which must
// match TodoApp.csproj's <Version> (shown on the sign-in screen and About dialog). A missed
// cache-bust bump has already caused a real bug once (a "NOT_SIGNED_IN after login" failure from
// mismatched cache versions loaded at different times). This script doesn't remove either manual
// step - that's what sync-desktop-version.js is for, for the DESKTOP_VERSION half - it just
// catches a miss before it ships, by checking everything actually agrees.
const fs = require('fs');
const path = require('path');
const { walkDocsFiles, readCsprojVersion, readConfigDesktopVersion } = require('./version-utils');

let ok = true;

// --- Cache-bust consistency ---------------------------------------------------
const VERSION_RE = /\?v=(\d+)/g;
const found = []; // { file, line, version }
for (const file of walkDocsFiles()) {
  const lines = fs.readFileSync(file, 'utf8').split('\n');
  lines.forEach((lineText, i) => {
    for (const m of lineText.matchAll(VERSION_RE)) {
      found.push({ file: path.relative(__dirname, file), line: i + 1, version: m[1] });
    }
  });
}

if (found.length === 0) {
  console.error('No "?v=NN" occurrences found under docs/ - did the cache-bust scheme change? Check this script still matches reality.');
  ok = false;
} else {
  const versions = new Set(found.map((f) => f.version));
  if (versions.size === 1) {
    console.log(`OK: all ${found.length} occurrence(s) of "?v=" agree on v${[...versions][0]}.`);
  } else {
    console.error(`MISMATCH: found ${versions.size} different cache-bust versions across ${found.length} occurrence(s):`);
    for (const { file, line, version } of found) {
      console.error(`  v${version}  ${file}:${line}`);
    }
    ok = false;
  }
}

// --- Desktop version consistency ----------------------------------------------
const csprojVersion = readCsprojVersion();
const configVersion = readConfigDesktopVersion();
if (csprojVersion === configVersion) {
  console.log(`OK: docs/js/config.js's DESKTOP_VERSION (${configVersion}) matches TodoApp.csproj.`);
} else {
  console.error(`MISMATCH: docs/js/config.js's DESKTOP_VERSION is "${configVersion}" but TodoApp.csproj's <Version> is "${csprojVersion}". Run: node sync-desktop-version.js`);
  ok = false;
}

process.exit(ok ? 0 : 1);
