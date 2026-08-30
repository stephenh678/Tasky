// Shared helpers for check-cache-version.js and sync-desktop-version.js - kept in one place so
// the two scripts don't duplicate the same file-walking/parsing logic they're meant to be
// preventing duplication bugs in.
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;
const CSPROJ_PATH = path.join(ROOT, 'TodoApp.csproj');
const CONFIG_PATH = path.join(ROOT, 'docs', 'js', 'config.js');
const DOCS_DIR = path.join(ROOT, 'docs');
const SCAN_EXTENSIONS = new Set(['.html', '.js', '.css']);

function walkDocsFiles(dir = DOCS_DIR, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walkDocsFiles(full, out);
    else if (SCAN_EXTENSIONS.has(path.extname(entry.name))) out.push(full);
  }
  return out;
}

function readCsprojVersion() {
  const xml = fs.readFileSync(CSPROJ_PATH, 'utf8');
  const m = xml.match(/<Version>([^<]+)<\/Version>/);
  if (!m) throw new Error(`Could not find <Version> in ${CSPROJ_PATH}`);
  return m[1].trim();
}

function readConfigDesktopVersion() {
  const js = fs.readFileSync(CONFIG_PATH, 'utf8');
  const m = js.match(/DESKTOP_VERSION = '([^']+)'/);
  if (!m) throw new Error(`Could not find DESKTOP_VERSION in ${CONFIG_PATH}`);
  return m[1];
}

// Bumps every "?v=NN" occurrence under docs/ to the next integer. Requires them to already agree
// on a single version - if they don't, check-cache-version.js needs to be run and fixed first,
// since bumping "the" version is ambiguous otherwise. Returns { oldVersion, newVersion }, or throws
// if the versions aren't consistent yet.
function bumpCacheVersion() {
  const files = walkDocsFiles();
  const versions = new Set();
  for (const file of files) {
    for (const m of fs.readFileSync(file, 'utf8').matchAll(/\?v=(\d+)/g)) versions.add(m[1]);
  }
  if (versions.size !== 1) {
    throw new Error(`Cache-bust versions aren't consistent yet (found: ${[...versions].join(', ')}) - run node check-cache-version.js first and fix that before bumping.`);
  }
  const oldVersion = Number([...versions][0]);
  const newVersion = oldVersion + 1;
  const bumpRe = new RegExp(`\\?v=${oldVersion}\\b`, 'g');
  for (const file of files) {
    const text = fs.readFileSync(file, 'utf8');
    const updated = text.replace(bumpRe, `?v=${newVersion}`);
    if (updated !== text) fs.writeFileSync(file, updated, 'utf8');
  }
  return { oldVersion, newVersion };
}

module.exports = { ROOT, CSPROJ_PATH, CONFIG_PATH, DOCS_DIR, walkDocsFiles, readCsprojVersion, readConfigDesktopVersion, bumpCacheVersion };
