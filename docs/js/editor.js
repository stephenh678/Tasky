// Renders a task's Body as editable blocks. Rtf (WPF's rich-text format for a block) is
// desktop-only - there's no browser engine for it, so the web editor works purely off each
// block's plain-text mirror. Reading a desktop-authored block still shows its Text fine; editing
// it here just never repopulates Rtf, so the desktop app falls back to unformatted text for
// anything touched from the web side. No data loss, just no bold/italic from this side yet.
//
// NoteBlockType has no "Table" entry - the desktop app's tables are RTF content embedded inside
// a Text block's Rtf, not a distinct block type, so there's nothing structural here to build
// against. Left out entirely rather than half-supported.
import { NoteBlockType, newNoteBlock, newChecklistItem } from './model.js?v=17';
import { icon } from './icons.js?v=17';
import { downloadAttachmentBlob, uploadAttachmentBlob, deleteAttachmentBlob } from './drive.js?v=17';

const URL_RE = /^https?:\/\/\S+$/i;

// Sets an element's content to an icon glyph followed by plain text, without needing an
// escapeHtml import - the text always goes in via a text node, never through innerHTML.
function setIconText(el, iconName, text) {
  el.innerHTML = icon(iconName, 'inline-icon');
  el.appendChild(document.createTextNode(` ${text}`));
}

// driveFetch() (in drive.js) throws the bare 'NOT_SIGNED_IN' string for a token Google rejected -
// same sentinel whether the token was missing or just got revoked mid-session - and
// 'DRIVE_SCOPE_MISSING' when sign-in succeeded but the user didn't check the Drive permission box
// on Google's consent screen (it's a separate opt-in checkbox, unchecked by default). app.js's
// status bar special-cases both into a clickable reconnect prompt; the inline attachment failures
// below have no click-to-reconnect affordance of their own (they're captions inside a note, not
// the status bar), so this just keeps the raw sentinel from leaking to the user as-is and points
// them at the control that DOES know how to reconnect.
function friendlyDriveError(err) {
  // Worded as a connection state, not a user action - this fires just as readily for a token
  // Google silently rejected mid-session (auth.js's refreshAccessToken hit a network hiccup, or
  // Google itself invalidated it early) as for an actual manual sign-out, and telling someone
  // "signed out" when they never touched Sign Out reads as the app being wrong about their own
  // account state.
  if (err.message === 'NOT_SIGNED_IN') return 'connection to Google expired - use Sync Now to reconnect';
  if (err.message === 'DRIVE_SCOPE_MISSING') return "Drive access wasn't granted - use Sync Now to fix";
  return err.message;
}

export function renderEditableBody(container, task, onChange) {
  releaseMediaCacheIfTaskChanged(task.Id);
  container.innerHTML = '';

  task.Body.forEach((block, index) => {
    const wrap = document.createElement('div');
    wrap.className = 'block-wrap';
    wrap.appendChild(renderBlock(block, task, index, onChange));

    const removeBtn = document.createElement('button');
    removeBtn.className = 'block-remove';
    removeBtn.innerHTML = icon('x');
    removeBtn.title = 'Remove block';
    removeBtn.addEventListener('click', () => {
      releaseBlockMedia(block);
      deleteRemoteAttachmentIfAny(block);
      task.Body.splice(index, 1);
      onChange({ rerenderBody: true });
    });
    wrap.appendChild(removeBtn);

    container.appendChild(wrap);
  });

  container.appendChild(renderInsertToolbar(task, onChange));
}

function renderBlock(block, task, index, onChange) {
  switch (block.Type) {
    case NoteBlockType.Text:
      return renderTextBlock(block, task, index, onChange);
    case NoteBlockType.Checklist:
      return renderChecklistBlock(block, onChange);
    case NoteBlockType.Link:
      return renderLinkBlock(block);
    case NoteBlockType.Photo:
      return renderPhotoByFileName(block.FileName);
    case NoteBlockType.File:
      return renderFileByFileName(block.FileName);
    default:
      return document.createElement('div');
  }
}

function renderTextBlock(block, task, index, onChange) {
  const wrap = document.createElement('div');

  const div = document.createElement('div');
  div.className = 'block-text';
  div.contentEditable = 'plaintext-only';
  if (!('plaintext-only' in div.style)) {
    // Safari doesn't support plaintext-only contentEditable yet - fall back to normal
    // contenteditable; paste handling below still strips this down to plain text/link logic.
    div.contentEditable = 'true';
  }
  div.textContent = block.Text;
  div.dataset.placeholder = 'Type…';
  div.addEventListener('input', () => {
    block.Text = div.innerText;
    // Desktop's loader checks Rtf first and, if present, displays THAT instead of Text - Rtf is
    // the actual rendered content there, Text is only a search/word-count mirror. Leaving a
    // desktop-authored block's old Rtf in place while only updating Text would make this edit
    // silently disappear the next time the task is opened on desktop (it'd keep showing the
    // stale pre-edit Rtf). Clearing Rtf makes desktop fall back to rendering Text directly -
    // confirmed safe against RichTextBoxBehavior.LoadContent, which handles an empty Rtf by
    // rendering Text as a plain paragraph. Net effect: any rich formatting on this specific
    // paragraph is dropped once edited from the web, which is already a disclosed limitation -
    // but the actual words are never lost or hidden.
    block.Rtf = '';
    onChange({ rerenderBody: false });
  });

  // Mirrors the desktop app's paste-URL-to-link behavior: pasting a bare URL turns into a real
  // Link block (like pasting an image turns into a Photo block) rather than landing as plain text.
  div.addEventListener('paste', (e) => {
    const text = (e.clipboardData || window.clipboardData)?.getData('text/plain') ?? '';
    const trimmed = text.trim();
    if (!URL_RE.test(trimmed)) return; // let normal paste happen
    e.preventDefault();
    task.Body.splice(index + 1, 0, newNoteBlock(NoteBlockType.Link, { url: trimmed, linkLabel: trimmed }));
    onChange({ rerenderBody: true });
  });

  wrap.appendChild(div);

  // Pasting an image straight into desktop's rich-text editor embeds it as an inline
  // <Image UriSource="..."> inside the block's Rtf rather than creating a separate Photo block -
  // there's no RTF engine here to render the rest of that markup (see file header), but the image
  // itself is just another file in this task's InlineImages folder, which downloadAttachmentBlob
  // already knows how to search. Pull out its filename and show it below the text.
  for (const fileName of extractInlineImageFileNames(block.Rtf)) {
    wrap.appendChild(renderPhotoByFileName(fileName));
  }

  // Same idea for a non-image file attached via desktop's Insert File toolbar button
  // (RichTextBoxBehavior.InsertInlineFileChip): it's embedded as a custom "file card" Grid
  // widget rather than a separate File block, tagged with the local path it was inserted from.
  // The bytes are just another file in this task's Attachments folder, which
  // downloadAttachmentBlob already knows how to search.
  for (const fileName of extractInlineFileNames(block.Rtf)) {
    wrap.appendChild(renderFileByFileName(fileName));
  }

  return wrap;
}

function extractInlineImageFileNames(rtf) {
  if (!rtf) return [];
  const names = [];
  const re = /UriSource="([^"]+)"/g;
  let m;
  while ((m = re.exec(rtf))) {
    const parts = m[1].split(/[\\/]/);
    const name = parts[parts.length - 1];
    if (name) names.push(name);
  }
  return names;
}

// The file card's outer Grid carries the attachment's local path as its Tag; the image
// container uses the same Tag attribute for a fixed "ImageContainer" marker instead of a path,
// and nested elements within a file card (the card body, the delete button) also set Tag to
// their own fixed markers - filtering those out leaves only genuine file-card paths.
const NON_FILE_TAG_MARKERS = new Set(['ImageContainer', 'CardBody', 'DeleteAttachmentBtn']);

function extractInlineFileNames(rtf) {
  if (!rtf) return [];
  const names = [];
  const re = /<Grid[^>]*\sTag="([^"]+)"/g;
  let m;
  while ((m = re.exec(rtf))) {
    if (NON_FILE_TAG_MARKERS.has(m[1])) continue;
    const parts = m[1].split(/[\\/]/);
    const name = parts[parts.length - 1];
    if (name) names.push(name);
  }
  return names;
}

function renderChecklistBlock(block, onChange) {
  const div = document.createElement('div');
  div.className = 'block-checklist';

  block.ChecklistItems.forEach((item, i) => {
    const row = document.createElement('div');
    row.className = 'block-checklist-item editable';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = item.IsChecked;
    checkbox.addEventListener('change', () => {
      item.IsChecked = checkbox.checked;
      onChange({ rerenderBody: false });
    });
    // See the matching comment in app.js's task-list checkbox: a <label> wrapper is the
    // reliable cross-browser way to grow a native checkbox's tap target without growing its
    // visible size (padding on the checkbox itself isn't consistently respected for hit-testing).
    const checkboxWrap = document.createElement('label');
    checkboxWrap.className = 'checkbox-tap-target';
    checkboxWrap.appendChild(checkbox);

    const text = document.createElement('input');
    text.type = 'text';
    text.value = item.Text;
    text.placeholder = 'Checklist item';
    text.addEventListener('input', () => {
      item.Text = text.value;
      onChange({ rerenderBody: false });
    });

    const del = document.createElement('button');
    del.className = 'block-remove small';
    del.innerHTML = icon('x');
    del.addEventListener('click', () => {
      block.ChecklistItems.splice(i, 1);
      onChange({ rerenderBody: true });
    });

    row.append(checkboxWrap, text, del);
    div.appendChild(row);
  });

  const addRow = document.createElement('input');
  addRow.type = 'text';
  addRow.placeholder = '+ Add item and press Enter';
  addRow.className = 'checklist-add';
  addRow.enterKeyHint = 'done';
  function commitChecklistRow() {
    if (!addRow.value.trim()) return;
    block.ChecklistItems.push(newChecklistItem({ text: addRow.value }));
    addRow.value = '';
    onChange({ rerenderBody: true });
  }
  addRow.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') commitChecklistRow();
  });
  // Many Android keyboards (Gboard included, with word prediction active) never fire a real
  // keydown for Enter/Done - only this input event, carrying inputType "insertLineBreak". Reuses
  // commitChecklistRow()'s own empty-value guard so a keyboard that fires both isn't double-handled.
  addRow.addEventListener('input', (e) => {
    if (e.inputType === 'insertLineBreak') commitChecklistRow();
  });
  div.appendChild(addRow);

  return div;
}

// Caches object URLs by filename so repeated re-renders (rerenderBody fires on nearly every body
// edit) don't re-download the same photo from Drive each time.
const photoUrlCache = new Map();

// object URLs pin their underlying blob's bytes in memory for as long as they stay unrevoked -
// photoUrlCache/fileUrlCache below never used to release any of them outside one narrow
// upload-failure rollback (handlePhotoPick), so viewing many photo/file-heavy tasks over one long
// session accumulated unbounded memory that was never freed until a full page reload. Fixed two
// ways: releaseBlockMedia revokes a single block's entry the moment it's removed (called from
// renderEditableBody's remove-button handler), and releaseMediaCacheIfTaskChanged revokes and
// clears everything left over the moment a *different* task is opened - within one task, the
// cache still persists across body re-renders exactly as intended (that's the whole point of it).
let cachedTaskId = null;
function releaseMediaCacheIfTaskChanged(taskId) {
  if (taskId === cachedTaskId) return;
  cachedTaskId = taskId;
  for (const url of photoUrlCache.values()) URL.revokeObjectURL(url);
  photoUrlCache.clear();
  for (const url of fileUrlCache.values()) URL.revokeObjectURL(url);
  fileUrlCache.clear();
}

// ROADMAP.md #139: releaseBlockMedia above only ever revoked the local object URL - the uploaded
// Drive file itself was never deleted, so web-only users accumulated orphaned attachments in Drive
// forever (desktop's 3-way diff only prunes one if a desktop client later syncs this exact file).
// Fire-and-forget: block removal is meant to feel instant, and this is best-effort cleanup that
// shouldn't block or fail the removal itself if Drive is slow or unreachable right now.
function deleteRemoteAttachmentIfAny(block) {
  if (block.Type !== NoteBlockType.Photo && block.Type !== NoteBlockType.File) return;
  if (!block.FileName) return;
  if (block.Type === NoteBlockType.Photo) deleteCachedThumbnail(block.FileName).catch(() => {});
  deleteAttachmentBlob(block.FileName).catch((err) => {
    console.warn(`Tasky: failed to delete remote attachment "${block.FileName}"`, err);
  });
}

// --- Persistent photo thumbnail cache (ROADMAP.md #70) -----------------------
// IndexedDB rather than localStorage - the cached payloads are binary blobs, potentially many of
// them, which localStorage (string-only, ~5-10MB total) can't hold. Keyed by filename alone: every
// attachment filename this app writes is a fresh crypto.randomUUID() (see handlePhotoPick), never
// reused for different content, so a cache entry never needs invalidating - it's either still the
// right image forever, or (after deleteCachedThumbnail, wired into deleteRemoteAttachmentIfAny
// above) gone.
const THUMBNAIL_DB_NAME = 'tasky-thumbnails';
const THUMBNAIL_STORE = 'thumbnails';
const THUMBNAIL_MAX_DIMENSION = 1024;
// A simple entry-count cap, not a byte budget or true LRU - cheap to enforce (see
// pruneThumbnailCache) and, at a max ~1024px-per-side JPEG each, 200 entries is at most a few
// hundred MB, well within what a PWA's storage quota tolerates without asking the user for
// persistent-storage permission.
const THUMBNAIL_MAX_ENTRIES = 200;

let thumbnailDbPromise = null;
function openThumbnailDb() {
  if (!thumbnailDbPromise) {
    thumbnailDbPromise = new Promise((resolve, reject) => {
      if (typeof indexedDB === 'undefined') {
        reject(new Error('IndexedDB unavailable'));
        return;
      }
      const req = indexedDB.open(THUMBNAIL_DB_NAME, 1);
      req.onupgradeneeded = () => {
        req.result.createObjectStore(THUMBNAIL_STORE, { keyPath: 'fileName' });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }
  return thumbnailDbPromise;
}

// Every call site below treats a cache miss/failure identically to "not cached yet" - IndexedDB
// being unavailable (very old browser, private-browsing lockdown in some engines) or a transaction
// failing just means loadPhotoBlob falls through to a fresh Drive download, same as a true miss.
async function getCachedThumbnail(fileName) {
  try {
    const db = await openThumbnailDb();
    return await new Promise((resolve, reject) => {
      const req = db.transaction(THUMBNAIL_STORE, 'readonly').objectStore(THUMBNAIL_STORE).get(fileName);
      req.onsuccess = () => resolve(req.result?.blob ?? null);
      req.onerror = () => reject(req.error);
    });
  } catch (err) {
    console.warn('Tasky: thumbnail cache read failed', fileName, err);
    return null;
  }
}

async function putCachedThumbnail(fileName, blob) {
  try {
    const db = await openThumbnailDb();
    await new Promise((resolve, reject) => {
      const tx = db.transaction(THUMBNAIL_STORE, 'readwrite');
      tx.objectStore(THUMBNAIL_STORE).put({ fileName, blob, cachedAt: Date.now() });
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
    });
    pruneThumbnailCache().catch((err) => console.warn('Tasky: thumbnail cache prune failed', err));
  } catch (err) {
    console.warn('Tasky: thumbnail cache write failed', fileName, err);
  }
}

async function deleteCachedThumbnail(fileName) {
  const db = await openThumbnailDb();
  db.transaction(THUMBNAIL_STORE, 'readwrite').objectStore(THUMBNAIL_STORE).delete(fileName);
}

// Keeps the cache bounded over a long-lived session that views many different photos - same
// bounded-not-unbounded spirit as the sync tombstone retention window (TaskSyncMerge.cs's
// DeduplicateTombstones), just capped by entry count instead of age since there's no natural
// "expiry" for an immutable, randomly-named attachment. A plain cursor walk to find the oldest
// entries is fine at this (a couple hundred rows) scale - not worth a second index on cachedAt
// just to avoid it.
async function pruneThumbnailCache() {
  const db = await openThumbnailDb();
  const store = db.transaction(THUMBNAIL_STORE, 'readwrite').objectStore(THUMBNAIL_STORE);
  const count = await new Promise((resolve, reject) => {
    const req = store.count();
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
  const overflow = count - THUMBNAIL_MAX_ENTRIES;
  if (overflow <= 0) return;

  const entries = await new Promise((resolve, reject) => {
    const found = [];
    const req = store.openCursor();
    req.onsuccess = () => {
      const cursor = req.result;
      if (!cursor) {
        resolve(found);
        return;
      }
      found.push({ key: cursor.primaryKey, cachedAt: cursor.value.cachedAt ?? 0 });
      cursor.continue();
    };
    req.onerror = () => reject(req.error);
  });
  entries.sort((a, b) => a.cachedAt - b.cachedAt);
  for (const entry of entries.slice(0, overflow)) store.delete(entry.key);
}

// Downscales a full-resolution blob to at most THUMBNAIL_MAX_DIMENSION on its longest side via an
// offscreen canvas before it's cached - createImageBitmap decodes off the main thread where
// supported. Falls back to caching the original blob untouched if decoding fails (a format canvas
// can't handle, or a browser too old for createImageBitmap) rather than losing the photo entirely -
// worse cache efficiency, not a broken photo.
async function downscaleToThumbnail(blob) {
  if (typeof createImageBitmap !== 'function') return blob;
  try {
    const bitmap = await createImageBitmap(blob);
    const scale = Math.min(1, THUMBNAIL_MAX_DIMENSION / Math.max(bitmap.width, bitmap.height));
    if (scale >= 1) {
      bitmap.close?.();
      return blob; // already small enough - re-encoding it would only lose quality for no size win
    }
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);
    canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    const thumbnail = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.85));
    return thumbnail ?? blob;
  } catch (err) {
    console.warn('Tasky: thumbnail downscale failed, caching original size', err);
    return blob;
  }
}

function releaseBlockMedia(block) {
  if (block.Type === NoteBlockType.Photo && photoUrlCache.has(block.FileName)) {
    URL.revokeObjectURL(photoUrlCache.get(block.FileName));
    photoUrlCache.delete(block.FileName);
  } else if (block.Type === NoteBlockType.File && fileUrlCache.has(block.FileName)) {
    URL.revokeObjectURL(fileUrlCache.get(block.FileName));
    fileUrlCache.delete(block.FileName);
  }
}

function renderPhotoByFileName(rawFileName) {
  const container = document.createElement('div');
  container.className = 'block-photo';
  const fileName = rawFileName || 'photo';

  if (photoUrlCache.has(fileName)) {
    container.appendChild(buildPhotoImg(photoUrlCache.get(fileName), fileName));
    return container;
  }

  loadPhotoInto(container, fileName);
  return container;
}

// A failure here is very often the same transient/reconnectable kind refreshAccessToken now
// retries once on its own (see auth.js) - but that retry is already over by the time this catch
// runs, and simply reopening the task to force a fresh render was the only way to try again
// before. Making the failure state itself clickable retries the one thing that actually failed,
// in place, without a full reload/re-navigation round trip.
function loadPhotoInto(container, fileName) {
  container.classList.remove('block-photo-error');
  container.onclick = null;
  container.textContent = `Loading ${fileName}…`;
  loadPhotoBlob(fileName)
    .then((blob) => {
      if (!blob) {
        setIconText(container, 'image', `${fileName} (not found on Drive)`);
        return;
      }
      const url = URL.createObjectURL(blob);
      photoUrlCache.set(fileName, url);
      container.textContent = '';
      container.appendChild(buildPhotoImg(url, fileName));
    })
    .catch((err) => {
      setIconText(container, 'image', `${fileName} (failed to load: ${friendlyDriveError(err)} - tap to retry)`);
      container.classList.add('block-photo-error');
      container.onclick = () => loadPhotoInto(container, fileName);
      console.error('Tasky: photo download failed', fileName, err);
    });
}

// ROADMAP.md #70: photoUrlCache (above) only ever lived for the current task-viewing session -
// releaseMediaCacheIfTaskChanged wipes it the moment a *different* task opens, so reopening any
// task with photos re-downloaded every one of them from Drive again, full-resolution, every single
// time. getCachedThumbnail/putCachedThumbnail persist a downscaled copy in IndexedDB instead, keyed
// by filename, so a photo already viewed once loads instantly from local storage on every later
// visit - across task switches and page reloads alike - without ever hitting the network again.
async function loadPhotoBlob(fileName) {
  const cached = await getCachedThumbnail(fileName);
  if (cached) return cached;
  const blob = await downloadAttachmentBlob(fileName);
  if (!blob) return null;
  const thumbnail = await downscaleToThumbnail(blob);
  putCachedThumbnail(fileName, thumbnail); // fire-and-forget - a failed cache write just costs a future re-download, not correctness
  return thumbnail;
}

function buildPhotoImg(url, fileName) {
  const img = document.createElement('img');
  img.className = 'block-photo-img';
  img.src = url;
  img.alt = fileName;
  return img;
}

// Same object-URL caching as photoUrlCache, kept separate since these are files, not images.
const fileUrlCache = new Map();

function renderFileByFileName(rawFileName) {
  const fileName = rawFileName || 'file';
  const link = document.createElement('a');
  link.className = 'block-file-link';
  link.href = '#';
  setIconText(link, 'paperclip', fileName);

  if (fileUrlCache.has(fileName)) {
    link.href = fileUrlCache.get(fileName);
    link.download = fileName;
    return link;
  }

  link.addEventListener('click', (e) => {
    e.preventDefault();
    if (fileUrlCache.has(fileName)) {
      link.href = fileUrlCache.get(fileName);
      link.download = fileName;
      link.click();
      return;
    }
    link.textContent = `Loading ${fileName}…`;
    downloadAttachmentBlob(fileName)
      .then((blob) => {
        if (!blob) {
          setIconText(link, 'paperclip', `${fileName} (not found on Drive)`);
          return;
        }
        const url = URL.createObjectURL(blob);
        fileUrlCache.set(fileName, url);
        setIconText(link, 'paperclip', fileName);
        link.href = url;
        link.download = fileName;
        link.click();
      })
      .catch((err) => {
        setIconText(link, 'paperclip', `${fileName} (failed to load: ${friendlyDriveError(err)})`);
        console.error('Tasky: file download failed', fileName, err);
      });
  });

  return link;
}

function renderLinkBlock(block) {
  const p = document.createElement('p');
  // Only http(s) is ever offered by this app's own "+ Link" flow (see promptForLink), but a
  // block can also arrive from a hand-edited file, an old backup, or the desktop app's own Link
  // prompt (which doesn't scheme-check) - fail closed rather than rendering a clickable
  // javascript:/data: href from data this page doesn't fully control.
  if (URL_RE.test(block.Url || '')) {
    const a = document.createElement('a');
    a.href = block.Url;
    a.textContent = block.LinkLabel || block.Url;
    a.target = '_blank';
    a.rel = 'noopener noreferrer';
    p.appendChild(a);
  } else {
    p.textContent = `${block.LinkLabel || block.Url || '(invalid link)'} (not a valid http(s) link)`;
  }
  return p;
}

/**
 * Replaces the old sequential prompt()s with a small modal matching the app's own UI (the
 * .modal-overlay/.modal-card pattern already used for the About dialog), and enforces the same
 * http(s)-only rule renderLinkBlock renders against - so a link created here can never fail that
 * check later. Resolves to { url, label } or null if cancelled.
 */
function promptForLink() {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';

    const card = document.createElement('div');
    card.className = 'modal-card link-modal-card';

    const heading = document.createElement('h2');
    heading.textContent = 'Add Link';

    const urlLabel = document.createElement('label');
    urlLabel.className = 'link-modal-field';
    urlLabel.textContent = 'URL';
    const urlInput = document.createElement('input');
    urlInput.type = 'url';
    urlInput.placeholder = 'https://…';
    urlLabel.appendChild(urlInput);

    const errorMsg = document.createElement('p');
    errorMsg.className = 'link-modal-error hidden';
    errorMsg.textContent = 'Enter a valid http:// or https:// URL.';

    const labelLabel = document.createElement('label');
    labelLabel.className = 'link-modal-field';
    labelLabel.textContent = 'Label (optional)';
    const labelInput = document.createElement('input');
    labelInput.type = 'text';
    labelLabel.appendChild(labelInput);

    const actions = document.createElement('div');
    actions.className = 'link-modal-actions';
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-ghost';
    cancelBtn.textContent = 'Cancel';
    const addBtn = document.createElement('button');
    addBtn.type = 'button';
    addBtn.className = 'btn btn-primary';
    addBtn.textContent = 'Add';
    actions.append(cancelBtn, addBtn);

    card.append(heading, urlLabel, errorMsg, labelLabel, actions);
    overlay.appendChild(card);
    document.body.appendChild(overlay);
    urlInput.focus();

    function close(result) {
      document.removeEventListener('keydown', onKeydown);
      overlay.remove();
      resolve(result);
    }
    function submit() {
      const url = urlInput.value.trim();
      if (!URL_RE.test(url)) {
        errorMsg.classList.remove('hidden');
        urlInput.focus();
        return;
      }
      close({ url, label: labelInput.value.trim() || url });
    }
    function onKeydown(e) {
      if (e.key === 'Escape') close(null);
      else if (e.key === 'Enter') {
        e.preventDefault();
        submit();
      }
    }
    document.addEventListener('keydown', onKeydown);
    overlay.addEventListener('click', (e) => {
      if (e.target === overlay) close(null);
    });
    cancelBtn.addEventListener('click', () => close(null));
    addBtn.addEventListener('click', submit);
  });
}

// Rebuilt fully on every render (renderEditableBody clears and re-renders the whole body on most
// edits), so the outside-click-closes listener below is registered once at module load rather
// than once per render - it just checks whichever bar/toggle are current at click time instead of
// accumulating a fresh document-level listener (and matching leaked closure) on every edit.
let activeInsertBar = null;
let activeInsertToggle = null;
document.addEventListener('click', (e) => {
  if (!activeInsertBar || activeInsertBar.classList.contains('hidden')) return;
  if (activeInsertBar.contains(e.target) || e.target === activeInsertToggle) return;
  activeInsertBar.classList.add('hidden');
});

function renderInsertToolbar(task, onChange) {
  const wrap = document.createElement('div');
  wrap.className = 'insert-toolbar-wrap';

  // Desktop has room for all four buttons in a row (unchanged, always visible - see the
  // min-width:768px override that forces .hidden off regardless of this class). On mobile they
  // don't fit, so this doubles as a menu trigger there: standard mobile pattern for 3+ actions
  // that don't fit a toolbar is one trigger with an overflow menu rather than letting them spill
  // off-screen.
  const toggleBtn = document.createElement('button');
  toggleBtn.type = 'button';
  toggleBtn.className = 'icon-btn insert-toggle-btn';
  toggleBtn.setAttribute('aria-label', 'Add content');
  toggleBtn.title = 'Add content';
  toggleBtn.innerHTML = icon('plus');
  toggleBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    bar.classList.toggle('hidden');
  });

  const bar = document.createElement('div');
  bar.className = 'insert-toolbar hidden';

  const addText = document.createElement('button');
  addText.className = 'btn btn-ghost';
  addText.textContent = '+ Text';
  addText.addEventListener('click', () => {
    task.Body.push(newNoteBlock(NoteBlockType.Text, {}));
    onChange({ rerenderBody: true });
  });

  const addChecklist = document.createElement('button');
  addChecklist.className = 'btn btn-ghost';
  addChecklist.textContent = '+ Checklist';
  addChecklist.addEventListener('click', () => {
    task.Body.push(newNoteBlock(NoteBlockType.Checklist, {}));
    onChange({ rerenderBody: true });
  });

  const addLink = document.createElement('button');
  addLink.className = 'btn btn-ghost';
  addLink.textContent = '+ Link';
  addLink.addEventListener('click', async () => {
    const result = await promptForLink();
    if (!result) return;
    task.Body.push(newNoteBlock(NoteBlockType.Link, { url: result.url, linkLabel: result.label }));
    onChange({ rerenderBody: true });
  });

  const addPhoto = document.createElement('button');
  addPhoto.className = 'btn btn-ghost';
  addPhoto.textContent = '+ Photo';
  const photoInput = document.createElement('input');
  photoInput.type = 'file';
  photoInput.accept = 'image/*';
  photoInput.className = 'hidden';
  addPhoto.addEventListener('click', () => photoInput.click());
  photoInput.addEventListener('change', () => {
    const file = photoInput.files?.[0];
    photoInput.value = ''; // lets the same file be picked again later
    if (file) handlePhotoPick(task, file, onChange);
  });

  const addFile = document.createElement('button');
  addFile.className = 'btn btn-ghost';
  addFile.textContent = '+ File';
  const fileInput = document.createElement('input');
  fileInput.type = 'file';
  fileInput.className = 'hidden';
  addFile.addEventListener('click', () => fileInput.click());
  fileInput.addEventListener('change', () => {
    const file = fileInput.files?.[0];
    fileInput.value = ''; // lets the same file be picked again later
    if (file) handleFilePick(task, file, onChange);
  });

  bar.append(addText, addChecklist, addLink, addPhoto, photoInput, addFile, fileInput);
  wrap.append(toggleBtn, bar);
  activeInsertBar = bar;
  activeInsertToggle = toggleBtn;
  return wrap;
}

async function handlePhotoPick(task, file, onChange) {
  const dot = file.name.lastIndexOf('.');
  const ext = dot > -1 ? file.name.slice(dot) : '.jpg';
  // A fresh random name per upload (mirroring desktop's own {Guid}.png convention for pasted
  // images) sidesteps any chance of colliding with another task's identically-named photo in the
  // same shared Drive Attachments folder - there's no per-task subfolder on either side.
  const fileName = `${crypto.randomUUID()}${ext}`;

  const block = newNoteBlock(NoteBlockType.Photo, { photoPath: fileName });
  task.Body.push(block);
  // Show it immediately from the local file rather than waiting on the upload + a Drive
  // round-trip to fetch back what was just picked.
  photoUrlCache.set(fileName, URL.createObjectURL(file));
  onChange({ rerenderBody: true });

  try {
    await uploadAttachmentBlob(fileName, file);
  } catch (err) {
    console.error('Tasky: photo upload failed', fileName, err);
    // Roll back rather than leave a block whose FileName never actually made it to Drive - the
    // synced .tasky JSON would otherwise reference a photo that doesn't exist there.
    const idx = task.Body.indexOf(block);
    if (idx !== -1) task.Body.splice(idx, 1);
    URL.revokeObjectURL(photoUrlCache.get(fileName));
    photoUrlCache.delete(fileName);
    // Surfaced via the app's own status line (see onBodyChange's `error` handling) instead of a
    // blocking native alert(), matching how every other failure in the app is reported.
    // isAuthFailure lets onBodyChange add the same "click to reconnect" affordance the other
    // NOT_SIGNED_IN/DRIVE_SCOPE_MISSING sites in app.js already get, instead of just showing the
    // raw sentinel text - both need the same fix (sign in again), so one flag covers both here.
    onChange({
      rerenderBody: true,
      error: `Photo upload failed: ${friendlyDriveError(err)}`,
      isAuthFailure: err.message === 'NOT_SIGNED_IN' || err.message === 'DRIVE_SCOPE_MISSING',
    });
  }
}

// Mirrors handlePhotoPick exactly (see its comments), just for NoteBlockType.File instead of
// Photo - both store their upload under the same random-{Guid}+extension filename (desktop's own
// convention, see fileName above) in the same Drive Attachments folder (uploadAttachmentBlob is
// already content-agnostic), and PhotoPath doubles as the generic "attachment reference" field for
// every block type that has one, File included (see NoteBlock.cs - FileName is just PhotoPath's
// basename, regardless of Type).
async function handleFilePick(task, file, onChange) {
  const dot = file.name.lastIndexOf('.');
  const ext = dot > -1 ? file.name.slice(dot) : '';
  const fileName = `${crypto.randomUUID()}${ext}`;

  const block = newNoteBlock(NoteBlockType.File, { photoPath: fileName });
  task.Body.push(block);
  fileUrlCache.set(fileName, URL.createObjectURL(file));
  onChange({ rerenderBody: true });

  try {
    await uploadAttachmentBlob(fileName, file);
  } catch (err) {
    console.error('Tasky: file upload failed', fileName, err);
    const idx = task.Body.indexOf(block);
    if (idx !== -1) task.Body.splice(idx, 1);
    URL.revokeObjectURL(fileUrlCache.get(fileName));
    fileUrlCache.delete(fileName);
    onChange({
      rerenderBody: true,
      error: `File upload failed: ${friendlyDriveError(err)}`,
      isAuthFailure: err.message === 'NOT_SIGNED_IN' || err.message === 'DRIVE_SCOPE_MISSING',
    });
  }
}
