// Renders a task's Body as editable blocks. Rtf (WPF's rich-text format for a block) is
// desktop-only - there's no browser engine for it, so the web editor works purely off each
// block's plain-text mirror. Reading a desktop-authored block still shows its Text fine; editing
// it here just never repopulates Rtf, so the desktop app falls back to unformatted text for
// anything touched from the web side. No data loss, just no bold/italic from this side yet.
//
// NoteBlockType has no "Table" entry - the desktop app's tables are RTF content embedded inside
// a Text block's Rtf, not a distinct block type, so there's nothing structural here to build
// against. Left out entirely rather than half-supported.
import { NoteBlockType, newNoteBlock, newChecklistItem } from './model.js?v=25';
import { icon } from './icons.js?v=25';
import { downloadAttachmentBlob, uploadAttachmentBlob } from './drive.js?v=25';

const URL_RE = /^https?:\/\/\S+$/i;

// driveFetch() (in drive.js) throws the bare 'NOT_SIGNED_IN' string for a token Google rejected -
// same sentinel whether the token was missing or just got revoked mid-session. app.js's status
// bar special-cases it into "Signed out - click to reconnect"; the inline attachment failures
// below have no click-to-reconnect affordance of their own (they're captions inside a note, not
// the status bar), so this just keeps the raw sentinel from leaking to the user as-is and points
// them at the control that DOES know how to reconnect.
function friendlyDriveError(err) {
  return err.message === 'NOT_SIGNED_IN' ? 'signed out - use Sync Now to reconnect' : err.message;
}

export function renderEditableBody(container, task, onChange) {
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
    case NoteBlockType.File: {
      const p = document.createElement('p');
      p.className = 'block-file';
      p.textContent = `📎 ${block.FileName || 'file'} (editing files from the web isn't supported yet)`;
      return p;
    }
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

function renderPhotoByFileName(rawFileName) {
  const container = document.createElement('div');
  container.className = 'block-photo';
  const fileName = rawFileName || 'photo';

  if (photoUrlCache.has(fileName)) {
    container.appendChild(buildPhotoImg(photoUrlCache.get(fileName), fileName));
    return container;
  }

  container.textContent = `Loading ${fileName}…`;
  downloadAttachmentBlob(fileName)
    .then((blob) => {
      if (!blob) {
        container.textContent = `📷 ${fileName} (not found on Drive)`;
        return;
      }
      const url = URL.createObjectURL(blob);
      photoUrlCache.set(fileName, url);
      container.textContent = '';
      container.appendChild(buildPhotoImg(url, fileName));
    })
    .catch((err) => {
      container.textContent = `📷 ${fileName} (failed to load: ${friendlyDriveError(err)})`;
      console.error('Tasky: photo download failed', fileName, err);
    });

  return container;
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
  link.textContent = `📎 ${fileName}`;

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
          link.textContent = `📎 ${fileName} (not found on Drive)`;
          return;
        }
        const url = URL.createObjectURL(blob);
        fileUrlCache.set(fileName, url);
        link.textContent = `📎 ${fileName}`;
        link.href = url;
        link.download = fileName;
        link.click();
      })
      .catch((err) => {
        link.textContent = `📎 ${fileName} (failed to load: ${friendlyDriveError(err)})`;
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

  bar.append(addText, addChecklist, addLink, addPhoto, photoInput);
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
    // NOT_SIGNED_IN sites in app.js already get, instead of just showing the raw sentinel text.
    onChange({
      rerenderBody: true,
      error: `Photo upload failed: ${friendlyDriveError(err)}`,
      isAuthFailure: err.message === 'NOT_SIGNED_IN',
    });
  }
}
