// Tasky Web service worker: precaches the app shell so an installed PWA launches with no
// connection (ROADMAP.md #6/#7 - previously a deliberate pass-through that existed only to satisfy
// Chrome's installability check). Together with docs/js/snapshot.js (the local copy of the task
// data) this gives the same launch-offline, sync-when-you-can model the desktop app has.
//
// Versioning: SHELL_VERSION is the same "?v=NN" cache-bust suffix every <script>/<link>/import in
// docs/ carries - bump-cache-version.js / check-cache-version.js treat this literal like any
// other occurrence, so it can't drift from index.html. Every version gets its own cache; activate
// deletes the rest.
//
// Strategy:
//  - Versioned assets (anything with "?v=" in the URL): cache-first. A given version's bytes never
//    change, and a new deploy references new versions from a fresh index.html.
//  - Navigations and unversioned same-origin files (index.html, manifest, icons): network-first,
//    falling back to the cache - so a normal online launch always gets the latest shell, and an
//    offline one still gets *a* shell.
//  - Anything cross-origin (Drive/userinfo APIs, the sign-in Cloud Functions, account pictures):
//    not intercepted at all. respondWith() is never called, so the browser handles them exactly
//    as if this worker didn't exist.
//
// skipWaiting()/clients.claim() still activate a new version immediately. That's safe because an
// already-open page only ever asks for its own version's assets: those are served cache-first from
// whichever cache still has them, or from the network - never silently swapped for newer bytes.

const SHELL_VERSION = '?v=31';
const CACHE_NAME = `tasky-shell-${SHELL_VERSION.replace('?v=', '')}`;

const SHELL_FILES = [
  './',
  './index.html',
  './manifest.json',
  './icons/icon-180.png',
  './icons/icon-192.png',
  './icons/icon-512.png',
  `./css/styles.css${SHELL_VERSION}`,
  `./js/boot-guard.js${SHELL_VERSION}`,
  `./js/app.js${SHELL_VERSION}`,
  `./js/auth.js${SHELL_VERSION}`,
  `./js/config.js${SHELL_VERSION}`,
  `./js/dialog.js${SHELL_VERSION}`,
  `./js/drive.js${SHELL_VERSION}`,
  `./js/editor.js${SHELL_VERSION}`,
  `./js/icons.js${SHELL_VERSION}`,
  `./js/model.js${SHELL_VERSION}`,
  `./js/snapshot.js${SHELL_VERSION}`,
  `./js/storage.js${SHELL_VERSION}`,
  `./js/sync.js${SHELL_VERSION}`,
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then((cache) => cache.addAll(SHELL_FILES))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (request.mode === 'navigate') {
    event.respondWith(networkFirst(request, './index.html'));
    return;
  }
  if (url.search.includes('v=')) {
    event.respondWith(cacheFirst(request));
    return;
  }
  event.respondWith(networkFirst(request));
});

async function cacheFirst(request) {
  const cached = await caches.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  if (response.ok) {
    const cache = await caches.open(CACHE_NAME);
    cache.put(request, response.clone());
  }
  return response;
}

async function networkFirst(request, fallbackKey) {
  try {
    const response = await fetch(request);
    if (response.ok) {
      const cache = await caches.open(CACHE_NAME);
      cache.put(request, response.clone());
    }
    return response;
  } catch (err) {
    const cached = (await caches.match(request)) ?? (fallbackKey ? await caches.match(fallbackKey) : null);
    if (cached) return cached;
    throw err;
  }
}
