#!/usr/bin/env node
// Run any time after bumping TodoApp.csproj's <Version>, before pushing/deploying Tasky Web:
//   node sync-desktop-version.js
//
// Folds two previously-separate, easy-to-forget manual steps into one command:
//   1. Updates docs/js/config.js's DESKTOP_VERSION to match TodoApp.csproj's <Version> (shown on
//      the sign-in screen and About Tasky Web dialog - see reference_tasky_web memory).
//   2. Bumps the docs/ cache-bust "?v=NN" suffix, since editing config.js is itself a docs/ JS
//      change that needs one (see check-cache-version.js's comment for why that matters).
// Safe to run any time, not just right after a real bump - it's a no-op if DESKTOP_VERSION
// already matches the csproj.
const fs = require('fs');
const { CONFIG_PATH, readCsprojVersion, readConfigDesktopVersion, walkDocsFiles } = require('./version-utils');

const csprojVersion = readCsprojVersion();
const configVersion = readConfigDesktopVersion();

if (csprojVersion === configVersion) {
  console.log(`OK: docs/js/config.js's DESKTOP_VERSION already matches TodoApp.csproj (${csprojVersion}). Nothing to do.`);
  process.exit(0);
}

// The cache-bust version must already agree across every occurrence, or bumping "the" version
// from here is ambiguous - defer to check-cache-version.js's own check rather than guessing.
const files = walkDocsFiles();
const versions = new Set();
for (const file of files) {
  for (const m of fs.readFileSync(file, 'utf8').matchAll(/\?v=(\d+)/g)) versions.add(m[1]);
}
if (versions.size !== 1) {
  console.error(`Cache-bust versions aren't consistent yet (found: ${[...versions].join(', ')}) - run node check-cache-version.js first and fix that before syncing the desktop version.`);
  process.exit(1);
}
const oldCacheBust = Number([...versions][0]);
const newCacheBust = oldCacheBust + 1;

const configSrc = fs.readFileSync(CONFIG_PATH, 'utf8');
fs.writeFileSync(CONFIG_PATH, configSrc.replace(/DESKTOP_VERSION = '[^']+'/, `DESKTOP_VERSION = '${csprojVersion}'`), 'utf8');

const bumpRe = new RegExp(`\\?v=${oldCacheBust}\\b`, 'g');
for (const file of files) {
  const text = fs.readFileSync(file, 'utf8');
  const updated = text.replace(bumpRe, `?v=${newCacheBust}`);
  if (updated !== text) fs.writeFileSync(file, updated, 'utf8');
}

console.log(`Updated DESKTOP_VERSION: ${configVersion} -> ${csprojVersion}`);
console.log(`Bumped cache-bust version: v${oldCacheBust} -> v${newCacheBust}`);
