// Deliberately does nothing beyond existing: no caching, no offline fallback, no install-time
// precache. Registered solely because Chrome/Android's PWA installability check requires *some*
// service worker with a fetch handler present before it will ever fire beforeinstallprompt - see
// ROADMAP.md gating decision #4 (#6/#7, the real offline service worker + cache, are deliberately
// deferred; Tasky Web is not meant to work offline). Every request just passes straight through to
// the network exactly as if this file didn't exist.
//
// skipWaiting()/clients.claim() activate a new version immediately rather than waiting for every
// open tab to close first - safe here specifically because there's no cache to get out of sync;
// a version with real caching logic would need to think much harder about this.
self.addEventListener('install', () => {
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', (event) => {
  event.respondWith(fetch(event.request));
});
