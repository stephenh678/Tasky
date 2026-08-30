#!/usr/bin/env node
// Run any time after editing docs/js/*.js, docs/index.html, or docs/css/styles.css - before
// pushing/deploying Tasky Web:
//   node bump-cache-version.js
//
// Tasky Web has no build step (deliberately - see reference_tasky_web memory), so the "?v=NN"
// cache-bust suffix duplicated across every <script>/<link>/import in docs/ has to be bumped by
// hand. Hand-editing all of them is exactly the kind of thing that gets missed in one spot - a
// missed bump has already caused a real bug once (a "NOT_SIGNED_IN after login" failure from
// mismatched cache versions loaded at different times). This script does the bump in one command
// instead; check-cache-version.js still exists as a belt-and-suspenders check before release.
//
// Safe to run any time the versions already agree - it errors out (instead of guessing) if they
// don't, since that means something's already wrong and needs check-cache-version.js's diagnosis
// first.
const { bumpCacheVersion } = require('./version-utils');

try {
  const { oldVersion, newVersion } = bumpCacheVersion();
  console.log(`Bumped cache-bust version: v${oldVersion} -> v${newVersion}`);
} catch (err) {
  console.error(err.message);
  process.exit(1);
}
