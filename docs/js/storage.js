// Guarded localStorage/sessionStorage access. Both can throw synchronously - not just on a write
// that runs out of quota, but on the very first read: Chrome's "Block all cookies" setting, some
// private-browsing modes, and a page embedded where storage is partitioned off all make
// `localStorage.getItem` itself raise a SecurityError. app.js used to touch localStorage at
// module scope (applyTheme() runs at import time), so in any of those browsers the whole app
// failed to boot before drawing a single thing. Every module now goes through this instead:
// storage that's unavailable degrades to "no saved preference" rather than "no app".
//
// Values are strings, exactly as the underlying Storage API returns them - callers keep doing
// their own String()/Number()/JSON.parse. Missing keys and unavailable storage both read as null.

function guard(store) {
  return {
    get(key) {
      try {
        return store().getItem(key);
      } catch {
        return null;
      }
    },
    set(key, value) {
      try {
        store().setItem(key, value);
      } catch {
        // Best-effort: a preference that can't be saved just resets on the next load.
      }
    },
    remove(key) {
      try {
        store().removeItem(key);
      } catch {
        // Nothing to clean up if the store is unreachable.
      }
    },
  };
}

// Deferred lookups (store() rather than a captured reference) because merely evaluating
// `window.localStorage` is what throws in the blocked-storage cases described above.
export const storage = guard(() => window.localStorage);
export const sessionStore = guard(() => window.sessionStorage);
