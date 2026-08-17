// Thin Google Drive REST v3 layer, called directly via fetch (no client library) - mirrors what
// Tasky/Services/GoogleDriveService.cs does for the desktop app, scoped to what the web app needs.
import { getAccessToken } from './auth.js?v=8';
import { TASKY_FOLDER_NAME } from './config.js?v=8';

const API = 'https://www.googleapis.com/drive/v3';
const UPLOAD_API = 'https://www.googleapis.com/upload/drive/v3';
const FOLDER_MIME = 'application/vnd.google-apps.folder';

async function driveFetch(url, options = {}) {
  const token = await getAccessToken();
  const res = await fetch(url, {
    ...options,
    headers: { ...(options.headers ?? {}), Authorization: `Bearer ${token}` },
  });
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`Drive API ${res.status} for ${url}: ${body}`);
  }
  return res;
}

/**
 * Finds the "Tasky" folder, creating it if missing. Self-healing like the desktop app: if the
 * folder was trashed/deleted out from under us, this just makes a new one rather than uploading
 * into a folder nobody can see.
 */
export async function ensureTaskyFolder() {
  const q = encodeURIComponent(
    `mimeType='${FOLDER_MIME}' and name='${TASKY_FOLDER_NAME}' and trashed=false`
  );
  const res = await driveFetch(`${API}/files?q=${q}&fields=files(id,name)&spaces=drive`);
  const { files } = await res.json();
  if (files && files.length > 0) return files[0].id;

  const createRes = await driveFetch(`${API}/files?fields=id`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: TASKY_FOLDER_NAME, mimeType: FOLDER_MIME }),
  });
  const created = await createRes.json();
  return created.id;
}

/** Lists .tasky files inside the given folder, newest-modified first. */
export async function listTaskyFiles(folderId) {
  const q = encodeURIComponent(
    `'${folderId}' in parents and name contains '.tasky' and trashed=false`
  );
  const res = await driveFetch(
    `${API}/files?q=${q}&fields=files(id,name,modifiedTime,size)&orderBy=modifiedTime desc&spaces=drive`
  );
  const { files } = await res.json();
  return files ?? [];
}

/** Returns the file ID for an exact name match inside the folder, or null if none exists. */
export async function findFileByName(name, folderId) {
  const q = encodeURIComponent(
    `'${folderId}' in parents and name='${name.replace(/'/g, "\\'")}' and trashed=false`
  );
  const res = await driveFetch(`${API}/files?q=${q}&fields=files(id,name)&spaces=drive`);
  const { files } = await res.json();
  return files && files.length > 0 ? files[0].id : null;
}

/** Same as findFileByName but restricted to folders, matching GetOrCreateFolderAsync's query. */
async function findFolderByName(name, folderId) {
  const q = encodeURIComponent(
    `mimeType='${FOLDER_MIME}' and '${folderId}' in parents and name='${name.replace(/'/g, "\\'")}' and trashed=false`
  );
  const res = await driveFetch(`${API}/files?q=${q}&fields=files(id,name)&spaces=drive`);
  const { files } = await res.json();
  return files && files.length > 0 ? files[0].id : null;
}

export async function downloadFileText(fileId) {
  const res = await driveFetch(`${API}/files/${fileId}?alt=media`);
  return res.text();
}

export async function downloadFileBlob(fileId) {
  const res = await driveFetch(`${API}/files/${fileId}?alt=media`);
  return res.blob();
}

// Desktop stores attachments under one of two layouts, per ResolveMediaContainerFolderIdAsync in
// GoogleDriveService.cs: whichever file was already syncing before per-file containers existed
// keeps its Attachments/InlineImages folders flat under the Tasky root; every other file gets its
// own "<name-without-extension> Attachments" container folder holding those same two subfolders.
// The web app has no access to the desktop's local Settings.json (that's what records which mode
// a given file is in), so there's no way to know which layout this account uses - resolve every
// candidate subfolder that actually exists and check each one when looking up a specific file.
let syncContext = { taskyFolderId: null, dataFileName: null };
let candidateFolderIdsPromise = null;

export function setSyncContext(taskyFolderId, dataFileName) {
  syncContext = { taskyFolderId, dataFileName };
  candidateFolderIdsPromise = null;
  uploadFolderIdPromise = null;
}

function fileStem(name) {
  const i = name.lastIndexOf('.');
  return i > 0 ? name.slice(0, i) : name;
}

async function ensureCandidateFolderIds() {
  const { taskyFolderId, dataFileName } = syncContext;
  if (!taskyFolderId || !dataFileName) return [];
  if (!candidateFolderIdsPromise) {
    candidateFolderIdsPromise = (async () => {
      // ResolveMediaContainerFolderIdAsync derives the container name from
      // Path.GetFileName(path).ToLowerInvariant() - the container for the default "Tasky.tasky"
      // is literally "tasky Attachments" (lowercase), and Drive's name= query is case-sensitive,
      // so this lowercase step isn't optional.
      const containerId = await findFolderByName(`${fileStem(dataFileName.toLowerCase())} Attachments`, taskyFolderId);
      const roots = [taskyFolderId, containerId].filter(Boolean);
      const ids = [];
      for (const root of roots) {
        for (const dirName of ['Attachments', 'InlineImages']) {
          const id = await findFolderByName(dirName, root);
          if (id) ids.push(id);
        }
      }
      console.info('Tasky: attachment search folders resolved', { taskyFolderId, dataFileName, containerId, candidateFolderIds: ids });
      return ids;
    })();
  }
  return candidateFolderIdsPromise;
}

/** Downloads an existing attachment (photo/file) by its filename, or null if not found on Drive. */
export async function downloadAttachmentBlob(fileName) {
  const folderIds = await ensureCandidateFolderIds();
  if (folderIds.length === 0) {
    console.warn(`Tasky: no attachment folders found on Drive for "${fileName}"`, syncContext);
    return null;
  }
  for (const folderId of folderIds) {
    const fileId = await findFileByName(fileName, folderId);
    if (fileId) return downloadFileBlob(fileId);
  }
  console.warn(`Tasky: "${fileName}" not found in any of ${folderIds.length} candidate attachment folder(s)`, folderIds);
  return null;
}

async function getOrCreateFolder(name, parentId) {
  const existing = await findFolderByName(name, parentId);
  if (existing) return existing;
  const createRes = await driveFetch(`${API}/files?fields=id`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, mimeType: FOLDER_MIME, parents: [parentId] }),
  });
  const created = await createRes.json();
  return created.id;
}

let uploadFolderIdPromise = null;

async function ensureAttachmentsWriteFolderId() {
  const { taskyFolderId, dataFileName } = syncContext;
  if (!taskyFolderId || !dataFileName) throw new Error('Not signed in yet.');
  if (!uploadFolderIdPromise) {
    uploadFolderIdPromise = (async () => {
      // Prefer an existing legacy flat "Attachments" folder if this account already has one, so
      // a web-added photo lands wherever desktop already expects this file's attachments (see
      // ensureCandidateFolderIds above - there's no way to know which layout an account uses
      // without desktop's local Settings.json). Only create the newer per-file container scheme
      // when neither layout exists yet, matching what a fresh desktop install would also do.
      const legacy = await findFolderByName('Attachments', taskyFolderId);
      if (legacy) return legacy;
      const containerId = await getOrCreateFolder(`${fileStem(dataFileName.toLowerCase())} Attachments`, taskyFolderId);
      return getOrCreateFolder('Attachments', containerId);
    })();
  }
  return uploadFolderIdPromise;
}

/** Uploads a new attachment (e.g. a photo picked on web) under a caller-chosen unique filename. */
export async function uploadAttachmentBlob(fileName, file) {
  const folderId = await ensureAttachmentsWriteFolderId();
  const metadata = { name: fileName, parents: [folderId] };
  const boundary = `tasky-${crypto.randomUUID()}`;
  const body = new Blob([
    `--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${JSON.stringify(metadata)}\r\n`,
    `--${boundary}\r\nContent-Type: ${file.type || 'application/octet-stream'}\r\n\r\n`,
    file,
    `\r\n--${boundary}--`,
  ]);
  const res = await driveFetch(`${UPLOAD_API}/files?uploadType=multipart&fields=id`, {
    method: 'POST',
    headers: { 'Content-Type': `multipart/related; boundary=${boundary}` },
    body,
  });
  const result = await res.json();
  return result.id;
}

/**
 * Creates or updates a Drive file's content. Pass fileId to update in place, or null to create
 * a new file in folderId. Returns the resulting file ID.
 */
export async function uploadFileText(fileId, name, folderId, text) {
  const metadata = fileId ? { name } : { name, parents: [folderId] };
  const boundary = `tasky-${crypto.randomUUID()}`;
  const body =
    `--${boundary}\r\n` +
    `Content-Type: application/json; charset=UTF-8\r\n\r\n` +
    `${JSON.stringify(metadata)}\r\n` +
    `--${boundary}\r\n` +
    `Content-Type: application/json; charset=UTF-8\r\n\r\n` +
    `${text}\r\n` +
    `--${boundary}--`;

  const url = fileId
    ? `${UPLOAD_API}/files/${fileId}?uploadType=multipart&fields=id`
    : `${UPLOAD_API}/files?uploadType=multipart&fields=id`;

  const res = await driveFetch(url, {
    method: fileId ? 'PATCH' : 'POST',
    headers: { 'Content-Type': `multipart/related; boundary=${boundary}` },
    body,
  });
  const result = await res.json();
  return result.id;
}

/** Revision history Drive already keeps on every save - used for restore-from-backup instead of
 *  reimplementing the desktop app's separate Backups folder. */
export async function listRevisions(fileId) {
  const res = await driveFetch(
    `${API}/files/${fileId}/revisions?fields=revisions(id,modifiedTime,size)`
  );
  const { revisions } = await res.json();
  return (revisions ?? []).sort((a, b) => new Date(b.modifiedTime) - new Date(a.modifiedTime));
}

export async function downloadRevisionText(fileId, revisionId) {
  const res = await driveFetch(`${API}/files/${fileId}/revisions/${revisionId}?alt=media`);
  return res.text();
}
