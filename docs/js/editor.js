// Renders a task's Body as editable blocks. Rtf (WPF's rich-text format for a block) is
// desktop-only - there's no browser engine for it, so the web editor works purely off each
// block's plain-text mirror. Reading a desktop-authored block still shows its Text fine; editing
// it here just never repopulates Rtf, so the desktop app falls back to unformatted text for
// anything touched from the web side. No data loss, just no bold/italic from this side yet.
//
// NoteBlockType has no "Table" entry - the desktop app's tables are RTF content embedded inside
// a Text block's Rtf, not a distinct block type, so there's nothing structural here to build
// against. Left out entirely rather than half-supported.
import { NoteBlockType, newNoteBlock, newChecklistItem } from './model.js?v=1';
import { icon } from './icons.js?v=1';

const URL_RE = /^https?:\/\/\S+$/i;

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
    case NoteBlockType.Photo: {
      const p = document.createElement('p');
      p.className = 'block-photo';
      p.textContent = `📷 ${block.FileName || 'photo'} (editing photos from the web isn't supported yet)`;
      return p;
    }
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

  return div;
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

    row.append(checkbox, text, del);
    div.appendChild(row);
  });

  const addRow = document.createElement('input');
  addRow.type = 'text';
  addRow.placeholder = '+ Add item and press Enter';
  addRow.className = 'checklist-add';
  addRow.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || !addRow.value.trim()) return;
    block.ChecklistItems.push(newChecklistItem({ text: addRow.value }));
    addRow.value = '';
    onChange({ rerenderBody: true });
  });
  div.appendChild(addRow);

  return div;
}

function renderLinkBlock(block) {
  const p = document.createElement('p');
  const a = document.createElement('a');
  a.href = block.Url;
  a.textContent = block.LinkLabel || block.Url;
  a.target = '_blank';
  a.rel = 'noopener noreferrer';
  p.appendChild(a);
  return p;
}

function renderInsertToolbar(task, onChange) {
  const bar = document.createElement('div');
  bar.className = 'insert-toolbar';

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
  addLink.addEventListener('click', () => {
    const url = prompt('URL:');
    if (!url) return;
    const label = prompt('Label (optional):', url) ?? url;
    task.Body.push(newNoteBlock(NoteBlockType.Link, { url, linkLabel: label }));
    onChange({ rerenderBody: true });
  });

  bar.append(addText, addChecklist, addLink);
  return bar;
}
