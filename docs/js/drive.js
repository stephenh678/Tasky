// Thin Google Drive REST v3 layer, called directly via fetch (no client library) - mirrors what
// Tasky/Services/GoogleDriveService.cs does for the desktop app, scoped to what the web app needs.
import { getAccessToken } from './auth.js';
import { TASKY_FOLDER_NAME } from './config.js';

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

export async function downloadFileText(fileId) {
  const res = await driveFetch(`${API}/files/${fileId}?alt=media`);
  return res.text();
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
