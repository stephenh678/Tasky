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

const SHELL_VERSION = '?v=34';
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
      // Not SHARE_CACHE: photos shared in just before an update would vanish before the page reads them.
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE_NAME && k !== SHARE_CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

// Android share target (manifest.json share_target): the OS POSTs what was shared here. Photos are
// parked in the "tasky-share" cache (the page can't receive a POST body itself), and the page is
// opened with the text fields and a file count in the query string - app.js handleIncomingShare
// turns that into a task and clears the cache.
const SHARE_CACHE = 'tasky-share';

async function receiveShare(request) {
  const form = await request.formData();
  const files = form.getAll('media').filter((f) => f && typeof f === 'object' && f.size > 0);
  const cache = await caches.open(SHARE_CACHE);
  for (const old of await cache.keys()) await cache.delete(old);
  let index = 0;
  for (const file of files) {
    const name = encodeURIComponent(file.name || `shared-${index}`);
    await cache.put(new Request(`./shared/${index++}-${name}`), new Response(file, { headers: { 'Content-Type': file.type || 'application/octet-stream' } }));
  }
  const params = new URLSearchParams();
  for (const key of ['title', 'text', 'url']) {
    const value = form.get(key);
    if (value) params.set(`share-${key}`, String(value));
  }
  if (files.length) params.set('share-files', String(files.length));
  if ([...params.keys()].length === 0) params.set('share-text', '');
  return Response.redirect(`./?${params}`, 303);
}

// A tapped reminder (app.js showReminder) focuses Tasky and opens that task - in the open tab if
// there is one, otherwise a fresh window deep-linked with #task=.
self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const taskId = event.notification.data?.taskId ?? null;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const client = windows.find((c) => new URL(c.url).origin === self.location.origin);
    if (client) {
      await client.focus();
      if (taskId) client.postMessage({ type: 'open-task', taskId });
      return;
    }
    await self.clients.openWindow(taskId ? `./#task=${encodeURIComponent(taskId)}` : './');
  })());
});

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method === 'POST' && new URL(request.url).pathname.endsWith('/share-target')) {
    event.respondWith(receiveShare(request));
    return;
  }
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
    // A redirected response can't be replayed for a later navigation (the browser rejects it), so
    // it must never replace the fallback entry.
    if (response.ok && !(fallbackKey && response.redirected)) {
      const cache = await caches.open(CACHE_NAME);
      // A navigation is always the same shell whatever its query string, so it refreshes the one
      // fallback entry - keyed by request, every ?share-text=... / ?section=... launch left its own
      // permanent copy behind (with the shared text sitting in the cache key).
      cache.put(fallbackKey ?? request, response.clone());
    }
    return response;
  } catch (err) {
    const cached = (await caches.match(request)) ?? (fallbackKey ? await caches.match(fallbackKey) : null);
    if (cached) return cached;
    throw err;
  }
}
