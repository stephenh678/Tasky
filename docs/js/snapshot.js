// Local copy of the synced state - the web/mobile equivalent of desktop's Tasky.tasky on disk.
//
// appState used to live only in memory: a phone OS killing the PWA (which Android and iOS both do
// freely) took every unsaved edit with it, and launching with no connection showed a browser
// error page. This keeps one structured-clone snapshot of the whole AppState in IndexedDB - not
// localStorage, whose ~5MB string-only quota a photo-heavy task file can exceed - written on every
// local edit (debounced by app.js) and after every successful sync, and read back at boot in two
// situations: Drive is unreachable (offline, or down), so the app shows the local copy and syncs
// when it can; or the snapshot is marked dirty, meaning a previous session died with unsaved
// edits, which app.js reconciles against the fresh download with the same 3-way merge used for
// any other device's changes. Zero wire-format change: what's stored IS the .tasky JSON shape.
//
// Every failure here is swallowed and reported as "no snapshot" / "not saved" - IndexedDB being
// unavailable (storage blocked, some private modes) must degrade to the old in-memory-only
// behaviour, never break the app.

const DB_NAME = 'tasky-local';
const DB_VERSION = 1;
const STORE = 'snapshot';
const KEY = 'current';
// Guest (local test) mode keeps its own record. It used to share KEY, so opening ./?guest=1 adopted
// the signed-in account's tasks as guest data and then overwrote that account's dirty snapshot.
export const GUEST_SNAPSHOT_KEY = 'guest';

let dbPromise = null;

function openDb() {
  if (!dbPromise) {
    dbPromise = new Promise((resolve, reject) => {
      let req;
      try {
        req = indexedDB.open(DB_NAME, DB_VERSION);
      } catch (err) {
        reject(err);
        return;
      }
      req.onupgradeneeded = () => req.result.createObjectStore(STORE);
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
      req.onblocked = () => reject(new Error('IndexedDB open blocked'));
    }).catch((err) => {
      dbPromise = null; // let a later call try again rather than caching the failure forever
      throw err;
    });
  }
  return dbPromise;
}

/**
 * Persists { appState, currentFileId, currentFileName, taskyFolderId, noRemoteFileYet, dirty,
 * accountEmail } plus a savedAt timestamp. Resolves either way; returns true only if it landed.
 */
export async function writeSnapshot(data, key = KEY) {
  try {
    const db = await openDb();
    await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put({ ...data, savedAt: Date.now() }, key);
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
    return true;
  } catch (err) {
    console.warn('Tasky: local snapshot write failed', err);
    return false;
  }
}

/** The stored snapshot, or null if there is none / storage is unavailable. */
export async function readSnapshot(key = KEY) {
  try {
    const db = await openDb();
    return await new Promise((resolve, reject) => {
      const req = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
      req.onsuccess = () => resolve(req.result ?? null);
      req.onerror = () => reject(req.error);
    });
  } catch (err) {
    console.warn('Tasky: local snapshot read failed', err);
    return null;
  }
}

/** Sign-out: the next account to sign in on this device must never see the previous one's tasks. */
export async function clearSnapshot(key = KEY) {
  try {
    const db = await openDb();
    await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).delete(key);
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } catch (err) {
    console.warn('Tasky: local snapshot clear failed', err);
  }
}
