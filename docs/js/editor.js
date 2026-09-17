// Renders a task's Body as editable blocks. Rtf (WPF's rich-text format for a block) is
// desktop-only - there's no browser engine for it, so the web editor works purely off each
// block's plain-text mirror. Reading a desktop-authored block still shows its Text fine; editing
// it here just never repopulates Rtf, so the desktop app falls back to unformatted text for
// anything touched from the web side. No data loss, just no bold/italic from this side yet.
//
// NoteBlockType has no "Table" entry - the desktop app's tables are RTF content embedded inside
// a Text block's Rtf, not a distinct block type, so there's nothing structural here to build
// against. Left out entirely rather than half-supported.
import {
  NoteBlockType,
  newNoteBlock,
  newChecklistItem,
  extractInlineImageFileNames,
  extractInlineFileNames,
  xamlToHtml,
  htmlToXaml,
} from './model.js?v=29';
import { icon } from './icons.js?v=29';
import { downloadAttachmentBlob, uploadAttachmentBlob, deleteAttachmentBlob } from './drive.js?v=29';
import { storage } from './storage.js?v=29';
import { openDialog, trapFocus } from './dialog.js?v=29';

// Touch devices get the Web Share sheet for files (an <a download> is unreliable inside an iOS
// standalone PWA) and a "Take Photo" entry; mouse-and-keyboard browsers keep plain downloads.
const isTouchDevice = window.matchMedia('(pointer: coarse)').matches;

const URL_RE = /^https?:\/\/\S+$/i;

// Finds URLs sitting inside a run of otherwise-plain text (as opposed to URL_RE above, which only
// matches when the ENTIRE string is nothing but a URL). Mirrors desktop's
// RichTextBoxBehavior.ParsePlainTextUrlSegments - same regex shape, same trailing-punctuation
// trim so a link doesn't swallow a sentence's closing "." or ")" into its target.
const EMBEDDED_URL_RE = /(https?:\/\/[^\s<>"']+|www\.[^\s<>"']+)/gi;
const URL_TRAILING_PUNCT_RE = /[.,;:!?)\]}"']+$/;

function splitTextIntoUrlSegments(text) {
  const segments = [];
  let lastIndex = 0;
  for (const match of text.matchAll(EMBEDDED_URL_RE)) {
    const raw = match[0].replace(URL_TRAILING_PUNCT_RE, '');
    if (!raw) continue;
    const absolute = raw.toLowerCase().startsWith('www.') ? `https://${raw}` : raw;
    if (!URL_RE.test(absolute)) continue;

    if (match.index > lastIndex) segments.push({ text: text.slice(lastIndex, match.index) });
    segments.push({ url: absolute, label: raw });
    lastIndex = match.index + raw.length;
  }
  if (lastIndex < text.length) segments.push({ text: text.slice(lastIndex) });
  return segments;
}

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
  // auth.js: the silent token refresh failed for a transient reason (rate-limited, Cloud Function
  // briefly down) but the session is fine - not a sign-out, just a temporary outage.
  if (err.message === 'AUTH_UNAVAILABLE') return "couldn't reach Tasky's sign-in service - try again shortly";
  return err.message;
}

// readOnly mirrors desktop's TaskDetailViewModel.IsEditable (false once a task is Done or in
// Trash): text blocks lose contentEditable, checklist rows lose their inputs' edit-ability, and
// the per-block remove buttons and the insert toolbar aren't rendered at all. Photos/files/links
// still render and still open, since viewing isn't editing.
export function renderEditableBody(container, task, onChange, { readOnly = false } = {}) {
  releaseMediaCacheIfTaskChanged(task.Id);
  const focus = captureBodyFocus(container);
  container.innerHTML = '';
  container.classList.toggle('read-only', readOnly);

  task.Body.forEach((block, index) => {
    const wrap = document.createElement('div');
    wrap.className = 'block-wrap';
    wrap.appendChild(renderBlock(block, task, index, onChange, readOnly));

    if (!readOnly) {
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
    }

    container.appendChild(wrap);
  });

  if (!readOnly) container.appendChild(renderInsertToolbar(task, onChange));
  restoreBodyFocus(container, focus);
}

// A body re-render (rerenderBody: true - adding a checklist item, removing a block) rebuilds every
// element, which drops keyboard focus on the floor; on a phone that also closes the soft keyboard,
// so adding three checklist items meant tapping "+ Add item" three times. Inputs that can
// sensibly keep focus across a rebuild carry a data-focus-key naming what they are (the block's
// Id plus a role), so the freshly-rendered equivalent can take focus and the caret back.
function captureBodyFocus(container) {
  const active = document.activeElement;
  if (!active || !container.contains(active) || !active.dataset.focusKey) return null;
  return {
    key: active.dataset.focusKey,
    selectionStart: active.selectionStart ?? null,
    selectionEnd: active.selectionEnd ?? null,
  };
}

function restoreBodyFocus(container, focus) {
  if (!focus) return;
  const target = container.querySelector(`[data-focus-key="${CSS.escape(focus.key)}"]`);
  if (!target) return;
  target.focus();
  if (focus.selectionStart !== null && typeof target.setSelectionRange === 'function') {
    try {
      const end = Math.min(focus.selectionEnd ?? focus.selectionStart, target.value.length);
      target.setSelectionRange(Math.min(focus.selectionStart, end), end);
    } catch {
      // Not a text-selectable input type - focus alone is enough.
    }
  }
}

function renderBlock(block, task, index, onChange, readOnly) {
  switch (block.Type) {
    case NoteBlockType.Text:
      return renderTextBlock(block, task, index, onChange, readOnly);
    case NoteBlockType.Checklist:
      return renderChecklistBlock(block, onChange, readOnly);
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

function renderTextBlock(block, task, index, onChange, readOnly) {
  const wrap = document.createElement('div');

  const div = document.createElement('div');
  div.className = 'block-text';
  const formattedHtml = xamlToHtml(block.Rtf);
  if (formattedHtml) {
    div.innerHTML = formattedHtml;
  } else {
    div.textContent = block.Text;
  }
  if (readOnly) {
    // A locked (Done/Trashed) task: formatted/plain text plus whatever inline attachments the Rtf still
    // references, none of the edit wiring below.
    wrap.appendChild(div);
    for (const fileName of extractInlineImageFileNames(block.Rtf)) wrap.appendChild(renderPhotoByFileName(fileName));
    for (const fileName of extractInlineFileNames(block.Rtf)) wrap.appendChild(renderFileByFileName(fileName));
    return wrap;
  }
  // When formatted HTML is present, keep full contentEditable to support rich styles; otherwise
  // plaintext-only keeps pasted content and Enter from producing unexpected nested HTML.
  if (formattedHtml) {
    div.contentEditable = 'true';
  } else {
    let plainTextOnly = false;
    try {
      div.contentEditable = 'plaintext-only';
      plainTextOnly = div.contentEditable === 'plaintext-only';
    } catch {
      // SyntaxError from an engine that rejects the value outright.
    }
    if (!plainTextOnly) div.contentEditable = 'true';
  }
  div.dataset.placeholder = 'Type…';
  div.addEventListener('input', () => {
    block.Text = div.innerText;
    // Hoist any inline attachment files (pasted images or file chips) into real Photo/File blocks
    // before updating formatting, ensuring attachment references are preserved across sync.
    if (block.Rtf) {
      hoistInlineAttachments(task, block);
    }
    // Convert edited HTML content into clean FlowDocument XAML so desktop continues to display
    // formatting, bold/italics, lists and paragraphs cleanly rather than losing styling.
    block.Rtf = htmlToXaml(div.innerHTML, block.Text);
    onChange({ rerenderBody: false });
  });

  // Mirrors the desktop app's paste-URL-to-link behavior: pasting a bare URL turns into a real
  // Link block (like pasting an image turns into a Photo block) rather than landing as plain text.
  div.addEventListener('paste', (e) => {
    const text = (e.clipboardData || window.clipboardData)?.getData('text/plain') ?? '';
    const trimmed = text.trim();
    if (URL_RE.test(trimmed)) {
      e.preventDefault();
      task.Body.splice(index + 1, 0, newNoteBlock(NoteBlockType.Link, { url: trimmed, linkLabel: trimmed }));
      onChange({ rerenderBody: true });
      return;
    }

    // A chunk of text with one or more URLs mixed into other words - a sentence copied from
    // Notes, a plain-text email/SMS view - rather than the clipboard being nothing but a URL
    // (handled above). This app's block model has no concept of an inline link inside a Text
    // block's plain-text content, so left alone the URL would land as dead characters; splitting
    // it into alternating Text/Link blocks (same shape the bare-URL case above already produces)
    // keeps it clickable.
    const segments = splitTextIntoUrlSegments(text);
    if (!segments.some((s) => s.url)) {
      // No embedded URL. Where plaintext-only isn't available the browser would paste the
      // clipboard's HTML flavour (fonts, colours, tables from Gmail/Docs) into what is meant to be
      // a plain-text block - insert the text/plain flavour ourselves instead. execCommand is the
      // one way to do that which keeps native undo and fires the input event above.
      if (!plainTextOnly && text) {
        e.preventDefault();
        document.execCommand('insertText', false, text);
      }
      return;
    }
    e.preventDefault();
    const newBlocks = segments
      .filter((s) => s.url || s.text.trim())
      .map((s) => (s.url
        ? newNoteBlock(NoteBlockType.Link, { url: s.url, linkLabel: s.label })
        : newNoteBlock(NoteBlockType.Text, { text: s.text.trim() })));
    task.Body.splice(index + 1, 0, ...newBlocks);
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

// Converts every attachment referenced inside a Text block's Rtf into a standalone Photo/File
// block placed right after it - called once, just before the Rtf is cleared by a web-side edit
// (see the input handler above). Visually nothing moves: renderTextBlock already draws these
// same attachments directly under the text, and the hoisted blocks land in that exact spot on the
// next re-render. Skips a filename that some other block in the task already references so the
// same attachment never appears twice. Returns how many blocks were added.
function hoistInlineAttachments(task, block) {
  const referenced = new Set(task.Body.map((b) => b.FileName).filter(Boolean));
  const hoisted = [];
  const hoist = (type, fileName) => {
    if (referenced.has(fileName)) return;
    referenced.add(fileName);
    hoisted.push(newNoteBlock(type, { photoPath: fileName }));
  };
  for (const name of extractInlineImageFileNames(block.Rtf)) hoist(NoteBlockType.Photo, name);
  for (const name of extractInlineFileNames(block.Rtf)) hoist(NoteBlockType.File, name);
  if (hoisted.length === 0) return 0;
  const at = task.Body.indexOf(block);
  task.Body.splice(at === -1 ? task.Body.length : at + 1, 0, ...hoisted);
  return hoisted.length;
}

// extractInlineImageFileNames / extractInlineFileNames live in model.js (shared with
// collectTaskFileNames, which app.js uses for attachment cleanup on permanent delete).

// Checklist editing keys, the way every notes app does them: Enter splits the item at the caret
// into a new one below (so Enter at the end is simply "next item"), Backspace on an empty item
// removes it and lands at the end of the one above, Alt+Up/Down reorder. Each one rebuilds the
// body (onChange rerenderBody) and then puts the caret where the gesture implies, which is not
// where captureBodyFocus would leave it - that restores the *same* key, and here the interesting
// input is a different row. The rerender is synchronous (app.js onBodyChange), so the new inputs
// exist by the time focusChecklistItem looks for them.
function focusChecklistItem(block, index, caret) {
  const target =
    document.querySelector(`[data-focus-key="${CSS.escape(`${block.Id}:item:${index}`)}"]`) ??
    document.querySelector(`[data-focus-key="${CSS.escape(`${block.Id}:add`)}"]`);
  if (!target) return;
  target.focus();
  const pos = caret === 'end' ? target.value.length : 0;
  try {
    target.setSelectionRange(pos, pos);
  } catch {
    // Not a text input - nothing to place.
  }
}
function splitChecklistItem(block, index, input, onChange) {
  const at = input.selectionStart ?? input.value.length;
  block.ChecklistItems[index].Text = input.value.slice(0, at);
  block.ChecklistItems.splice(index + 1, 0, newChecklistItem({ text: input.value.slice(at) }));
  onChange({ rerenderBody: true });
  focusChecklistItem(block, index + 1, 'start');
}
function removeChecklistItem(block, index, onChange) {
  block.ChecklistItems.splice(index, 1);
  onChange({ rerenderBody: true });
  focusChecklistItem(block, Math.max(0, index - 1), 'end');
}
function moveChecklistItem(block, index, delta, onChange) {
  const target = index + delta;
  if (target < 0 || target >= block.ChecklistItems.length) return;
  const [item] = block.ChecklistItems.splice(index, 1);
  block.ChecklistItems.splice(target, 0, item);
  onChange({ rerenderBody: true });
  focusChecklistItem(block, target, 'end');
}

function renderChecklistBlock(block, onChange, readOnly) {
  const div = document.createElement('div');
  div.className = 'block-checklist';

  block.ChecklistItems.forEach((item, i) => {
    const row = document.createElement('div');
    row.className = 'block-checklist-item editable';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = item.IsChecked;
    checkbox.disabled = readOnly;
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
    text.readOnly = readOnly;
    text.dataset.focusKey = `${block.Id}:item:${i}`; // see captureBodyFocus
    text.addEventListener('input', (e) => {
      // Gboard and friends send Enter as this input event rather than a keydown (see addRow below).
      if (e.inputType === 'insertLineBreak') {
        splitChecklistItem(block, i, text, onChange);
        return;
      }
      item.Text = text.value;
      onChange({ rerenderBody: false });
    });
    text.addEventListener('keydown', (e) => {
      if (readOnly) return;
      if (e.key === 'Enter') {
        e.preventDefault();
        splitChecklistItem(block, i, text, onChange);
      } else if (e.key === 'Backspace' && text.value === '') {
        e.preventDefault();
        removeChecklistItem(block, i, onChange);
      } else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
        e.preventDefault();
        moveChecklistItem(block, i, e.key === 'ArrowUp' ? -1 : 1, onChange);
      }
    });

    row.append(checkboxWrap, text);
    if (!readOnly) {
      const del = document.createElement('button');
      del.className = 'block-remove small';
      del.innerHTML = icon('x');
      del.addEventListener('click', () => {
        block.ChecklistItems.splice(i, 1);
        onChange({ rerenderBody: true });
      });
      row.appendChild(del);
    }
    div.appendChild(row);
  });

  if (readOnly) return div;

  const addRow = document.createElement('input');
  addRow.type = 'text';
  addRow.placeholder = '+ Add item and press Enter';
  addRow.className = 'checklist-add';
  addRow.enterKeyHint = 'done';
  addRow.dataset.focusKey = `${block.Id}:add`; // keeps focus (and the mobile keyboard) across the re-render each commit triggers
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
  fileBlobCache.clear();
}

// ROADMAP.md #139: releaseBlockMedia above only ever revoked the local object URL - the uploaded
// Drive file itself was never deleted, so web-only users accumulated orphaned attachments in Drive
// forever (desktop's 3-way diff only prunes one if a desktop client later syncs this exact file).
// Fire-and-forget: block removal is meant to feel instant, and this is best-effort cleanup that
// shouldn't block or fail the removal itself if Drive is slow or unreachable right now.
function deleteRemoteAttachmentIfAny(block) {
  if (block.Type !== NoteBlockType.Photo && block.Type !== NoteBlockType.File) return;
  if (!block.FileName) return;
  deleteAttachmentFiles([block.FileName]);
}

// Permanently removes each named attachment from Drive and from the local thumbnail cache -
// the whole-task counterpart to deleteRemoteAttachmentIfAny above, for a task that's being
// permanently deleted (single delete, Empty Trash, bulk delete, auto-empty). Desktop's
// CleanupTaskAttachments does the same on its side; without this, a web-only user's permanent
// deletes left every photo/file orphaned in Drive forever. The caller (app.js) is responsible for
// the "still referenced by another task" check, since only it can see the whole task list. Same
// fire-and-forget contract as the single-block path: the deletion itself is already committed,
// this is best-effort cleanup that must never block or fail it.
export function deleteAttachmentFiles(fileNames) {
  for (const fileName of fileNames) {
    if (!fileName) continue;
    // The thumbnail cache only ever holds images, but delete() on an absent key is a harmless
    // no-op, so there's no need to guess by extension here.
    deleteCachedThumbnail(fileName).catch(() => {});
    deleteAttachmentBlob(fileName).catch((err) => {
      console.warn(`Tasky: failed to delete remote attachment "${fileName}"`, err);
    });
  }
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
  return (await downscaleImage(blob, THUMBNAIL_MAX_DIMENSION)).blob;
}

// Shared by the thumbnail cache (1024 px) and the optional pre-upload shrink (2048 px, below).
// Returns { blob, reencoded }: reencoded is false when the image was already small enough (or
// couldn't be decoded), in which case `blob` is the untouched original.
async function downscaleImage(blob, maxDimension, quality = 0.85) {
  if (typeof createImageBitmap !== 'function') return { blob, reencoded: false };
  try {
    const bitmap = await createImageBitmap(blob);
    const scale = Math.min(1, maxDimension / Math.max(bitmap.width, bitmap.height));
    if (scale >= 1) {
      bitmap.close?.();
      return { blob, reencoded: false }; // already small enough - re-encoding would only lose quality for no size win
    }
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);
    canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    const scaled = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality));
    return scaled ? { blob: scaled, reencoded: true } : { blob, reencoded: false };
  } catch (err) {
    console.warn('Tasky: image downscale failed, keeping original size', err);
    return { blob, reencoded: false };
  }
}

// Settings > Photos > "Shrink photos before upload" (app.js owns the toggle; same key, same
// default: on for touch devices, off otherwise so a desktop browser matches desktop Tasky's
// full-resolution uploads unless the user opts in). 2048 px on the long side at q0.85 turns a
// 3-6 MB phone JPEG into a few hundred KB with no visible loss at any size this app shows.
const PHOTO_DOWNSCALE_KEY = 'tasky-photo-downscale';
const UPLOAD_MAX_DIMENSION = 2048;
function photoDownscaleEnabled() {
  return (storage.get(PHOTO_DOWNSCALE_KEY) ?? String(isTouchDevice)) === 'true';
}
async function prepareUploadImage(file) {
  if (!photoDownscaleEnabled()) return { blob: file, reencoded: false };
  return downscaleImage(file, UPLOAD_MAX_DIMENSION);
}

function releaseBlockMedia(block) {
  if (block.Type === NoteBlockType.Photo && photoUrlCache.has(block.FileName)) {
    URL.revokeObjectURL(photoUrlCache.get(block.FileName));
    photoUrlCache.delete(block.FileName);
  } else if (block.Type === NoteBlockType.File && fileUrlCache.has(block.FileName)) {
    URL.revokeObjectURL(fileUrlCache.get(block.FileName));
    fileUrlCache.delete(block.FileName);
    fileBlobCache.delete(block.FileName);
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
  img.title = 'Tap to view full size';
  img.addEventListener('click', () => openPhotoLightbox(fileName, url));
  return img;
}

// Tap-to-open full-size viewer - the web counterpart of desktop's PhotoViewerWindow. What the
// note shows is the 1024 px cached thumbnail; this opens on that immediately and swaps in the
// original from Drive once it arrives (kept for the rest of the session so reopening is instant).
const originalUrlCache = new Map();
function openPhotoLightbox(fileName, previewUrl) {
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay photo-lightbox';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');
  overlay.setAttribute('aria-label', fileName);

  const img = document.createElement('img');
  img.src = originalUrlCache.get(fileName) ?? previewUrl;
  img.alt = fileName;

  const caption = document.createElement('div');
  caption.className = 'photo-lightbox-caption';
  caption.textContent = originalUrlCache.has(fileName) ? fileName : `${fileName} - loading full size…`;

  const closeBtn = document.createElement('button');
  closeBtn.type = 'button';
  closeBtn.className = 'icon-btn photo-lightbox-close';
  closeBtn.setAttribute('aria-label', 'Close');
  closeBtn.innerHTML = icon('x');

  let releaseFocus = null;
  const close = () => {
    overlay.remove();
    document.removeEventListener('keydown', onKeydown);
    releaseFocus?.();
  };
  const onKeydown = (e) => {
    if (e.key === 'Escape') close();
  };
  overlay.addEventListener('click', close);
  document.addEventListener('keydown', onKeydown);

  overlay.append(img, caption, closeBtn);
  document.body.appendChild(overlay);
  releaseFocus = trapFocus(overlay, { initialFocus: closeBtn });

  if (!originalUrlCache.has(fileName)) {
    downloadAttachmentBlob(fileName)
      .then((blob) => {
        if (!blob) {
          caption.textContent = `${fileName} (original not found on Drive - showing the cached copy)`;
          return;
        }
        const url = URL.createObjectURL(blob);
        originalUrlCache.set(fileName, url);
        if (overlay.isConnected) {
          img.src = url;
          caption.textContent = fileName;
        }
      })
      .catch((err) => {
        caption.textContent = `${fileName} (full size failed to load: ${friendlyDriveError(err)})`;
      });
  }
}

// Same object-URL caching as photoUrlCache, kept separate since these are files, not images.
const fileUrlCache = new Map();
// The blobs behind those URLs, so a second tap can hand the same bytes to the share sheet
// without re-downloading (a File for navigator.share needs the blob, not an object URL).
const fileBlobCache = new Map();

function renderFileByFileName(rawFileName) {
  const fileName = rawFileName || 'file';
  const link = document.createElement('a');
  link.className = 'block-file-link';
  link.href = '#';
  setIconText(link, 'paperclip', fileName);

  // On a touch device the bytes go to the Web Share sheet (Files, Mail, another app) rather than
  // an <a download>, which iOS's standalone PWA mode doesn't reliably honour. Desktop keeps the
  // download. Both paths go through here so the cached and freshly-downloaded cases behave alike.
  const deliver = (blob) => {
    if (isTouchDevice && navigator.share && navigator.canShare) {
      const file = new File([blob], fileName, { type: blob.type || 'application/octet-stream' });
      if (navigator.canShare({ files: [file] })) {
        navigator.share({ files: [file], title: fileName }).catch((err) => {
          if (err?.name !== 'AbortError') console.warn('Tasky: file share failed', err);
        });
        return;
      }
    }
    const a = document.createElement('a');
    a.href = fileUrlCache.get(fileName);
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
  };
  const cachedBlobs = fileBlobCache;

  link.addEventListener('click', (e) => {
    e.preventDefault();
    if (cachedBlobs.has(fileName)) {
      deliver(cachedBlobs.get(fileName));
      return;
    }
    link.textContent = `Loading ${fileName}…`;
    downloadAttachmentBlob(fileName)
      .then((blob) => {
        if (!blob) {
          setIconText(link, 'paperclip', `${fileName} (not found on Drive)`);
          return;
        }
        fileUrlCache.set(fileName, URL.createObjectURL(blob));
        cachedBlobs.set(fileName, blob);
        setIconText(link, 'paperclip', fileName);
        deliver(blob);
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
  return openDialog({
    title: 'Add Link',
    fields: [
      { key: 'url', label: 'URL', type: 'url', placeholder: 'https://…', error: 'Enter a valid http:// or https:// URL.' },
      { key: 'label', label: 'Label (optional)' },
    ],
    actions: [
      { label: 'Cancel', value: null },
      {
        label: 'Add',
        primary: true,
        submit: true,
        validate: (v) => (URL_RE.test(v.url) ? null : 'url'),
        value: (v) => ({ url: v.url, label: v.label || v.url }),
      },
    ],
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
  // "Take Photo" - a second file input with capture="environment", which phones open straight on
  // the rear camera instead of the gallery picker. Only offered on touch devices; a laptop's
  // webcam prompt for the same attribute is more confusing than useful.
  const addCamera = document.createElement('button');
  addCamera.className = 'btn btn-ghost';
  addCamera.textContent = '+ Take Photo';
  const cameraInput = document.createElement('input');
  cameraInput.type = 'file';
  cameraInput.accept = 'image/*';
  cameraInput.setAttribute('capture', 'environment');
  cameraInput.className = 'hidden';
  addCamera.addEventListener('click', () => cameraInput.click());
  cameraInput.addEventListener('change', () => {
    const file = cameraInput.files?.[0];
    cameraInput.value = '';
    if (file) handlePhotoPick(task, file, onChange);
  });

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

  bar.append(addText, addChecklist, addLink, addPhoto, photoInput);
  if (isTouchDevice) bar.append(addCamera, cameraInput);
  bar.append(addFile, fileInput);
  wrap.append(toggleBtn, bar);
  activeInsertBar = bar;
  activeInsertToggle = toggleBtn;
  return wrap;
}

// Every attachment upload still in flight, including its rollback-on-failure. The block for a
// picked photo/file is pushed into task.Body (and the task marked dirty) the moment it's chosen,
// so the .tasky JSON references the filename before the bytes exist on Drive - and app.js's 4s
// autosave debounce routinely beats a slow mobile upload. If the page died in that gap the
// synced file pointed at an attachment no device could ever find. app.js's saveToDrive awaits
// waitForPendingUploads() before it serializes, so a save only ever goes out once every picked
// attachment has either landed on Drive or been rolled back out of the body.
const pendingUploads = new Set();

export function waitForPendingUploads() {
  if (pendingUploads.size === 0) return Promise.resolve();
  // Each tracked promise already swallows its own failure (see trackUpload) so one failed upload
  // never blocks the save - the failed block has been rolled back by then, which is exactly the
  // state the save should capture.
  return Promise.all([...pendingUploads]).then(() => undefined);
}

function trackUpload(work) {
  const tracked = work.catch(() => {});
  pendingUploads.add(tracked);
  tracked.finally(() => pendingUploads.delete(tracked));
  return work;
}

async function handlePhotoPick(task, pickedFile, onChange) {
  // Optionally shrunk before anything else happens (see prepareUploadImage) so the preview, the
  // thumbnail cache and the upload all agree on one set of bytes. A re-encoded image is JPEG
  // whatever it started as, so its name says so.
  const prepared = await prepareUploadImage(pickedFile);
  const file = prepared.blob;
  const dot = pickedFile.name.lastIndexOf('.');
  const ext = prepared.reencoded ? '.jpg' : (dot > -1 ? pickedFile.name.slice(dot) : '.jpg');
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
  // Seed the thumbnail cache from the local file right away - the same downscaled copy
  // loadPhotoBlob would otherwise build only after re-downloading the full-size upload from
  // Drive. Without this, switching away and back within Drive's ~2s search-index lag made
  // findFileByName miss the just-uploaded file and the photo showed as "not found on Drive".
  downscaleToThumbnail(file).then((thumb) => putCachedThumbnail(fileName, thumb));

  // The whole upload-or-roll-back sequence is what's tracked (not just the upload call) so a
  // save waiting on it can't observe the half-state between "upload rejected" and "block removed".
  await trackUpload((async () => {
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
      deleteCachedThumbnail(fileName).catch(() => {});
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
  })());
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
  fileBlobCache.set(fileName, file); // shareable right away, no round-trip through Drive
  onChange({ rerenderBody: true });

  await trackUpload((async () => {
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
  })());
}
