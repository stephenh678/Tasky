import * as auth from './auth.js?v=8';
import * as drive from './drive.js?v=8';
import {
  NoteBlockType,
  RecurrenceRule,
  parseDotNetDate,
  formatDotNetDate,
  nowDotNet,
  newTaskItem,
  newTaskSyncRecord,
  spawnNextOccurrence,
  blockHasInlineImage,
} from './model.js?v=8';
import { deduplicateTombstones, mergeRemoteState } from './sync.js?v=8';
import { renderEditableBody } from './editor.js?v=8';
import { icon } from './icons.js?v=8';
import { DEFAULT_DATA_FILE_NAME } from './config.js?v=8';

const el = (id) => document.getElementById(id);
const signinScreen = el('signin-screen');
const signinBtn = el('signin-btn');
const signinStatus = el('signin-status');
const appEl = el('app');
const appBody = document.querySelector('.app-body');
const sidebarList = el('sidebar-list');
const tagList = el('tag-list');
const taskListEl = el('task-list');
const listEmpty = el('list-empty');
const searchBox = el('search-box');
const newTaskBtn = el('new-task-btn');
const sidebarNewTaskBtn = el('sidebar-new-task-btn');
const sortSelect = el('sort-select');
const quickFilterSelect = el('quick-filter-select');
const editorEmpty = el('editor-empty');
const editorContent = el('editor-content');
const editorTitle = el('editor-title');
const editorDue = el('editor-due');
const editorRecurrence = el('editor-recurrence');
const editorTags = el('editor-tags');
const editorTagInput = el('editor-tag-input');
const editorBody = el('editor-body');
const editorDone = el('editor-done');
const editorPinBtn = el('editor-pin-btn');
const editorTrashBtn = el('editor-trash-btn');
const editorDeleteBtn = el('editor-delete-btn');
const saveStatus = el('save-status');
const syncNowBtn = el('sync-now-btn');
const accountBtn = el('account-btn');
const accountDropdown = el('account-dropdown');
const accountEmailEl = el('account-email');
const accountLastSyncedEl = el('account-last-synced');
const accountSignoutBtn = el('account-signout-btn');
const menuBtn = el('menu-btn');
const menuDropdown = el('menu-dropdown');
const themeSwitch = el('theme-switch');
const fontSizeSwitch = el('font-size-switch');
const aboutBtn = el('about-btn');
const aboutModal = el('about-modal');
const aboutCloseBtn = el('about-close-btn');
const sidebarDrawerBtn = el('sidebar-drawer-btn');
const sidebarCollapseBtn = el('sidebar-collapse-btn');
const navBack = el('nav-back');
const trashActionsRow = el('trash-actions-row');
const emptyTrashBtn = el('empty-trash-btn');
const mobileTabbar = el('mobile-tabbar');

const SECTION_ICONS = { all: 'list', recurring: 'repeat', done: 'check', trash: 'trash' };
navBack.innerHTML = icon('back');
sidebarDrawerBtn.innerHTML = icon('menu');
menuBtn.innerHTML = icon('menu');
syncNowBtn.innerHTML = icon('sync');
newTaskBtn.innerHTML = `${icon('plus')}<span class="sidebar-item-label">New Task</span>`;
sidebarNewTaskBtn.innerHTML = `${icon('plus')}<span class="sidebar-item-label">New Task</span>`;
editorPinBtn.innerHTML = icon('pin');
sidebarCollapseBtn.innerHTML = icon('chevronLeft');
aboutCloseBtn.innerHTML = icon('x');
document.querySelector('button[data-theme-choice="light"]').innerHTML = icon('sun');
document.querySelector('button[data-theme-choice="dark"]').innerHTML = icon('moon');
document.querySelector('button[data-theme-choice="system"]').innerHTML = icon('monitor');

let noRemoteFileYet = false;

let appState = { Tasks: [], DeletedTasks: [] };
let currentFileId = null;
let taskyFolderId = null;
let currentSection = { kind: 'all' }; // {kind:'all'|'recurring'|'done'|'trash'|'tag', tag?}
let selectedTaskId = null;
let searchQuery = '';
let sortKey = 'modified';
let quickFilter = '';

let dirty = false;
let saving = false;
let saveTimer = null;
const SAVE_DEBOUNCE_MS = 10000;

const SECTIONS = [
  { kind: 'all', label: 'All Tasks' },
  { kind: 'recurring', label: 'Recurring' },
  { kind: 'done', label: 'Completed' },
  { kind: 'trash', label: 'Trash' },
];

// --- Boot: first check whether this load is Google redirecting back from sign-in, then fall
// back to the localStorage cache. Neither path ever risks a surprise redirect on page load -
// handleRedirectReturn() only acts on ?code=/?error= params that Google itself put there.
function waitForGoogleIdentity() {
  return new Promise((resolve) => {
    if (window.google?.accounts?.oauth2) return resolve();
    const t = setInterval(() => {
      if (window.google?.accounts?.oauth2) {
        clearInterval(t);
        resolve();
      }
    }, 50);
  });
}

async function boot() {
  await waitForGoogleIdentity();
  signinBtn.disabled = false;
  signinBtn.textContent = 'Sign in with Google';

  if (await auth.handleRedirectReturn()) {
    await onSignedIn();
    return;
  }
  if (auth.restoreFromCache()) {
    await onSignedIn();
  }
}

signinBtn.addEventListener('click', () => {
  signinStatus.textContent = '';
  try {
    auth.signIn(); // navigates the tab to Google's consent screen - nothing to await here
  } catch (err) {
    signinStatus.textContent = `Sign-in failed: ${err.message}`;
  }
});

accountBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  closeDropdowns({ except: accountDropdown });
  accountDropdown.classList.toggle('hidden');
});
accountSignoutBtn.addEventListener('click', () => {
  auth.signOut();
  location.reload();
});

// --- Menu dropdown: theme + about --------------------------------------------
menuBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  closeDropdowns({ except: menuDropdown });
  menuDropdown.classList.toggle('hidden');
});
document.addEventListener('click', () => closeDropdowns({}));
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    closeDropdowns({});
    closeAbout();
  }
});
function closeDropdowns({ except }) {
  for (const d of [menuDropdown, accountDropdown]) {
    if (d !== except) d.classList.add('hidden');
  }
}

const THEME_KEY = 'tasky-theme';
function applyTheme(choice) {
  if (choice === 'system') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = choice;
  localStorage.setItem(THEME_KEY, choice);
  for (const btn of themeSwitch.querySelectorAll('button')) {
    btn.classList.toggle('active', btn.dataset.themeChoice === choice);
  }
}
themeSwitch.addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-theme-choice]');
  if (btn) applyTheme(btn.dataset.themeChoice);
});
applyTheme(localStorage.getItem(THEME_KEY) ?? 'system');

const FONT_SIZE_KEY = 'tasky-font-size';
function applyFontSize(choice) {
  if (choice === 'medium') delete document.documentElement.dataset.fontSize;
  else document.documentElement.dataset.fontSize = choice;
  localStorage.setItem(FONT_SIZE_KEY, choice);
  for (const btn of fontSizeSwitch.querySelectorAll('button')) {
    btn.classList.toggle('active', btn.dataset.fontSize === choice);
  }
}
fontSizeSwitch.addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-font-size]');
  if (btn) applyFontSize(btn.dataset.fontSize);
});
applyFontSize(localStorage.getItem(FONT_SIZE_KEY) ?? 'medium');

// --- Last synced indicator (mirrors the desktop app's "Last Synced" display) ------------------
const LAST_SYNCED_KEY = 'tasky-last-synced';
function formatLastSynced(date) {
  if (!date) return 'Last synced: never';
  return `Last synced: ${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} at ${date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`;
}
function setLastSynced(date) {
  localStorage.setItem(LAST_SYNCED_KEY, date.toISOString());
  accountLastSyncedEl.textContent = formatLastSynced(date);
}
{
  const stored = localStorage.getItem(LAST_SYNCED_KEY);
  accountLastSyncedEl.textContent = formatLastSynced(stored ? new Date(stored) : null);
}

aboutBtn.addEventListener('click', () => {
  closeDropdowns({});
  aboutModal.classList.remove('hidden');
});
aboutCloseBtn.addEventListener('click', closeAbout);
aboutModal.addEventListener('click', (e) => {
  if (e.target === aboutModal) closeAbout();
});
function closeAbout() {
  aboutModal.classList.add('hidden');
}

// --- Sync now -----------------------------------------------------------------
syncNowBtn.addEventListener('click', async () => {
  if (!auth.isSignedIn()) {
    auth.signIn(); // redirects away and back; the resumed session syncs normally on return
    return;
  }
  clearTimeout(saveTimer);
  syncNowBtn.classList.add('spinning');
  saveStatus.textContent = 'Syncing…';
  try {
    dirty = false;
    await saveToDrive();
    saveStatus.textContent = 'Saved';
    setLastSynced(new Date());
  } catch (err) {
    saveStatus.textContent = `Sync failed: ${err.message}`;
    console.error(err);
  } finally {
    syncNowBtn.classList.remove('spinning');
  }
});

// --- Sidebar collapse / drawer -------------------------------------------------
const SIDEBAR_COLLAPSED_KEY = 'tasky-sidebar-collapsed';
function setSidebarCollapsed(collapsed) {
  appBody.classList.toggle('sidebar-collapsed', collapsed);
  localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(collapsed));
}
sidebarCollapseBtn.addEventListener('click', () => {
  setSidebarCollapsed(!appBody.classList.contains('sidebar-collapsed'));
});
setSidebarCollapsed(localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === 'true');

async function onSignedIn() {
  signinScreen.classList.add('hidden');
  appEl.classList.remove('hidden');
  saveStatus.textContent = 'Loading…';

  const email = auth.getAccountEmail();
  if (email) {
    accountBtn.textContent = email[0].toUpperCase();
    accountBtn.title = `Signed in as ${email}`;
    accountEmailEl.textContent = `Signed in as ${email}`;
  }

  try {
    taskyFolderId = await drive.ensureTaskyFolder();
    const files = await drive.listTaskyFiles(taskyFolderId);
    const match =
      files.find((f) => f.name.toLowerCase() === DEFAULT_DATA_FILE_NAME.toLowerCase()) ?? files[0];

    if (match) {
      currentFileId = match.id;
      drive.setSyncContext(taskyFolderId, match.name);
      const text = await drive.downloadFileText(match.id);
      appState = JSON.parse(text);
      appState.Tasks ??= [];
      appState.DeletedTasks = deduplicateTombstones(appState.DeletedTasks ?? []);
      saveStatus.textContent = `Loaded ${appState.Tasks.length} task(s)`;
    } else {
      drive.setSyncContext(taskyFolderId, DEFAULT_DATA_FILE_NAME);
      saveStatus.textContent = '';
      noRemoteFileYet = true;
    }

    renderSidebar();
    renderList();
  } catch (err) {
    saveStatus.textContent = `Load failed: ${err.message}`;
    console.error(err);
  }
}

// --- Filtering / sorting ----------------------------------------------------
function tasksForSection(section) {
  switch (section.kind) {
    case 'all':
      return appState.Tasks.filter((t) => !t.IsClosed && !t.IsDone);
    case 'recurring':
      return appState.Tasks.filter((t) => !t.IsClosed && !t.IsDone && t.Recurrence !== RecurrenceRule.None);
    case 'done':
      return appState.Tasks.filter((t) => !t.IsClosed && t.IsDone);
    case 'trash':
      return appState.Tasks.filter((t) => t.IsClosed);
    case 'tag':
      return appState.Tasks.filter((t) => !t.IsClosed && t.Tags.includes(section.tag));
    default:
      return [];
  }
}

function isSameDate(a, b) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

function applyQuickFilter(tasks) {
  if (!quickFilter) return tasks;
  const today = new Date();
  return tasks.filter((t) => {
    const due = t.DueDate ? parseDotNetDate(t.DueDate) : null;
    switch (quickFilter) {
      case 'overdue':
        return due && due < today && !isSameDate(due, today) && !t.IsDone;
      case 'dueToday':
        return due && isSameDate(due, today);
      case 'noDueDate':
        return !due;
      case 'recurring':
        return t.Recurrence !== RecurrenceRule.None;
      case 'hasLink':
        return t.Body.some((b) => b.Type === NoteBlockType.Link);
      case 'hasAttachment':
        return t.Body.some((b) => b.Type === NoteBlockType.Photo || b.Type === NoteBlockType.File || blockHasInlineImage(b));
      default:
        return true;
    }
  });
}

function applySearch(tasks) {
  const q = searchQuery.trim().toLowerCase();
  if (!q) return tasks;
  if (q.startsWith('tag:')) {
    const tag = q.slice(4).trim();
    return tasks.filter((t) => t.Tags.some((tg) => tg.toLowerCase().includes(tag)));
  }
  return tasks.filter((t) => {
    if (t.Text.toLowerCase().includes(q)) return true;
    if (t.Tags.some((tg) => tg.toLowerCase().includes(q))) return true;
    return t.Body.some((b) => b.Type === NoteBlockType.Text && b.Text.toLowerCase().includes(q));
  });
}

function sortTasks(tasks) {
  return [...tasks].sort((a, b) => {
    if (a.IsPinned !== b.IsPinned) return a.IsPinned ? -1 : 1;
    switch (sortKey) {
      case 'name':
        return a.Text.localeCompare(b.Text);
      case 'due': {
        const ad = a.DueDate ? parseDotNetDate(a.DueDate).getTime() : Infinity;
        const bd = b.DueDate ? parseDotNetDate(b.DueDate).getTime() : Infinity;
        return ad - bd;
      }
      case 'created':
        return parseDotNetDate(b.CreatedAt) - parseDotNetDate(a.CreatedAt);
      case 'modified':
      default:
        return parseDotNetDate(b.ModifiedAt) - parseDotNetDate(a.ModifiedAt);
    }
  });
}

function allTags() {
  const s = new Set();
  for (const t of appState.Tasks) {
    if (t.IsClosed) continue;
    for (const tag of t.Tags) s.add(tag);
  }
  return [...s].sort();
}

function currentTasks() {
  return sortTasks(applySearch(applyQuickFilter(tasksForSection(currentSection))));
}

function findTask(id) {
  return appState.Tasks.find((t) => t.Id === id) ?? null;
}

// --- Mutations (all funnel through markDirty) --------------------------------
function touch(task) {
  task.ModifiedAt = nowDotNet();
}

function markDirty() {
  dirty = true;
  saveStatus.textContent = 'Unsaved changes…';
  clearTimeout(saveTimer);
  saveTimer = setTimeout(triggerSave, SAVE_DEBOUNCE_MS);
}

async function triggerSave() {
  if (saving) {
    saveTimer = setTimeout(triggerSave, 2000);
    return;
  }
  if (!dirty) return;
  saving = true;
  dirty = false;
  saveStatus.textContent = 'Saving…';
  try {
    await saveToDrive();
    saveStatus.textContent = 'Saved';
    saveStatus.classList.remove('save-status-action');
    setLastSynced(new Date());
  } catch (err) {
    dirty = true;
    // getAccessToken() deliberately throws instead of attempting a background reauth (that's
    // the same unwanted-popup problem this debounce timer isn't allowed to trigger) - surface
    // it as a click target instead, since a click IS allowed to reauth.
    if (err.message === 'NOT_SIGNED_IN') {
      saveStatus.textContent = 'Signed out — click to reconnect';
      saveStatus.classList.add('save-status-action');
    } else {
      saveStatus.textContent = `Save failed: ${err.message}`;
      console.error(err);
    }
  } finally {
    saving = false;
  }
}

saveStatus.addEventListener('click', () => {
  if (!saveStatus.classList.contains('save-status-action')) return;
  // Redirects away and back - any edit still unsaved at this exact moment is lost, since AppState
  // only ever lives in memory (see the "online-only" decision). Nothing left to retry here after
  // the redirect - the resumed session picks back up normally via boot()'s handleRedirectReturn().
  auth.signIn();
});

async function saveToDrive() {
  if (currentFileId) {
    try {
      const text = await drive.downloadFileText(currentFileId);
      const remoteState = JSON.parse(text);
      remoteState.Tasks ??= [];
      remoteState.DeletedTasks = deduplicateTombstones(remoteState.DeletedTasks ?? []);
      mergeRemoteState(appState, remoteState);
      renderSidebar();
      renderList();
      // A merge can update the task currently open in the editor pane (e.g. a body edit made on
      // another device) - renderList() alone won't reflect that there, since the editor only
      // normally re-renders when you click into a task. Skip it while the editor itself has
      // focus so an in-progress edit's cursor position isn't disrupted mid-typing.
      const selectedTask = selectedTaskId ? findTask(selectedTaskId) : null;
      if (selectedTask && !editorContent.contains(document.activeElement)) {
        renderEditor(selectedTask);
      }
    } catch (err) {
      // Remote unreadable (empty/corrupt/interrupted upload) - fall through and upload local
      // state as-is, same fallback the desktop app uses.
      console.warn('Could not read remote file for merge, uploading local state as-is.', err);
    }
  }

  const json = JSON.stringify(appState, null, 2);
  const newId = await drive.uploadFileText(currentFileId, DEFAULT_DATA_FILE_NAME, taskyFolderId, json);
  currentFileId = newId;
  noRemoteFileYet = false;
}

function createTask() {
  const task = newTaskItem({ text: '' });
  appState.Tasks.push(task);
  currentSection = { kind: 'all' };
  selectedTaskId = task.Id;
  markDirty();
  renderSidebar();
  renderList();
  renderEditor(task);
  showMobileView('editor');
  editorTitle.focus();
}

function toggleDone(task) {
  const wasDone = task.IsDone;
  task.IsDone = !wasDone;
  touch(task);

  if (task.IsDone && task.Recurrence !== RecurrenceRule.None) {
    const next = spawnNextOccurrence(task);
    appState.Tasks.push(next);
  }
  markDirty();
  renderSidebar();
  renderList();
}

function togglePin(task) {
  task.IsPinned = !task.IsPinned;
  touch(task);
  markDirty();
  renderSidebar();
  renderList();
  renderEditor(task);
}

function toggleTrash(task) {
  task.IsClosed = !task.IsClosed;
  touch(task);
  markDirty();
  renderSidebar();
  renderList();
  renderEditor(task);
}

function recordTombstone(taskId) {
  const existing = appState.DeletedTasks.find((r) => r.TaskId === taskId);
  if (existing) existing.Timestamp = nowDotNet();
  else appState.DeletedTasks.push(newTaskSyncRecord(taskId));
}

function deleteForever(task) {
  if (!confirm(`Delete "${task.Text || '(untitled)'}" permanently? This cannot be undone.`)) return;
  appState.Tasks = appState.Tasks.filter((t) => t.Id !== task.Id);
  recordTombstone(task.Id);

  if (selectedTaskId === task.Id) {
    selectedTaskId = null;
    showEmptyEditor();
  }
  markDirty();
  renderSidebar();
  renderList();
  showMobileView('list');
}

function emptyTrash() {
  const trashed = appState.Tasks.filter((t) => t.IsClosed);
  if (trashed.length === 0) return;
  if (!confirm(`Permanently delete ${trashed.length} task(s) in Trash? This cannot be undone.`)) return;

  const trashedIds = new Set(trashed.map((t) => t.Id));
  appState.Tasks = appState.Tasks.filter((t) => !trashedIds.has(t.Id));
  for (const id of trashedIds) recordTombstone(id);

  if (selectedTaskId && trashedIds.has(selectedTaskId)) {
    selectedTaskId = null;
    showEmptyEditor();
  }
  markDirty();
  renderSidebar();
  renderList();
}

function addTag(task, rawTag) {
  const tag = rawTag.trim().toLowerCase();
  if (!tag || task.Tags.includes(tag)) return;
  task.Tags.push(tag);
  touch(task);
  markDirty();
  renderSidebar();
  renderEditor(task);
}

function removeTag(task, tag) {
  task.Tags = task.Tags.filter((t) => t !== tag);
  touch(task);
  markDirty();
  renderSidebar();
  renderEditor(task);
}

// --- Rendering --------------------------------------------------------------
// Shared by the sidebar list, the tag list, and the mobile tab bar - all three are just
// different views onto the same "which section is current" state.
function selectSection(section) {
  currentSection = section;
  searchBox.value = '';
  searchQuery = '';
  renderSidebar();
  renderList();
  showMobileView('list');
}

function renderSidebar() {
  sidebarList.innerHTML = '';
  for (const section of SECTIONS) {
    const li = document.createElement('li');
    const count = tasksForSection({ kind: section.kind }).length;
    li.title = section.label;
    li.innerHTML = `${icon(SECTION_ICONS[section.kind])}<span class="sidebar-item-label">${section.label}</span><span class="count">${count}</span>`;
    if (currentSection.kind === section.kind) li.classList.add('active');
    li.addEventListener('click', () => selectSection({ kind: section.kind }));
    sidebarList.appendChild(li);
  }

  tagList.innerHTML = '';
  for (const tag of allTags()) {
    const li = document.createElement('li');
    li.innerHTML = `<span>#${tag}</span>`;
    if (currentSection.kind === 'tag' && currentSection.tag === tag) li.classList.add('active');
    li.addEventListener('click', () => selectSection({ kind: 'tag', tag }));
    tagList.appendChild(li);
  }

  renderMobileTabbar();
}

// Mobile-only bottom tab bar: the 4 fixed sections get one tap instead of open-drawer-then-pick,
// with a "More" tab standing in for Tags (a variable-length list that can't be fixed tabs) and
// for the sidebar screen generally. Hidden by CSS whenever the editor view is open.
function renderMobileTabbar() {
  mobileTabbar.innerHTML = '';
  const onSidebarView = appEl.dataset.view === 'sidebar';

  for (const section of SECTIONS) {
    const btn = document.createElement('button');
    btn.className = 'mobile-tab';
    btn.type = 'button';
    btn.innerHTML = `${icon(SECTION_ICONS[section.kind])}<span>${section.label}</span>`;
    if (!onSidebarView && currentSection.kind === section.kind) btn.classList.add('active');
    btn.addEventListener('click', () => selectSection({ kind: section.kind }));
    mobileTabbar.appendChild(btn);
  }

  const moreBtn = document.createElement('button');
  moreBtn.className = 'mobile-tab';
  moreBtn.type = 'button';
  moreBtn.innerHTML = `${icon('menu')}<span>More</span>`;
  if (onSidebarView || currentSection.kind === 'tag') moreBtn.classList.add('active');
  moreBtn.addEventListener('click', () => showMobileView('sidebar'));
  mobileTabbar.appendChild(moreBtn);
}

function renderList() {
  const tasks = currentTasks();
  trashActionsRow.classList.toggle('hidden', currentSection.kind !== 'trash' || tasksForSection({ kind: 'trash' }).length === 0);
  taskListEl.innerHTML = '';
  listEmpty.classList.toggle('hidden', tasks.length > 0);
  listEmpty.textContent =
    noRemoteFileYet && appState.Tasks.length === 0
      ? 'No Tasky file on Drive yet — create your first task and one will be set up automatically.'
      : 'No tasks here.';

  for (const task of tasks) {
    const li = document.createElement('li');
    if (task.Id === selectedTaskId) li.classList.add('selected');

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = task.IsDone;
    checkbox.title = 'Mark done';
    checkbox.addEventListener('click', (e) => {
      e.stopPropagation();
      toggleDone(task);
    });

    const info = document.createElement('div');
    info.className = 'task-row-info';
    const due = task.DueDate ? formatDate(parseDotNetDate(task.DueDate)) : '';
    const indicators = [];
    if (task.Recurrence !== RecurrenceRule.None) indicators.push(icon('repeat'));
    if (task.Body.some((b) => b.Type === NoteBlockType.Link)) indicators.push(icon('link'));
    if (task.Body.some((b) => b.Type === NoteBlockType.Photo || blockHasInlineImage(b))) indicators.push(icon('image'));
    if (task.Body.some((b) => b.Type === NoteBlockType.File)) indicators.push(icon('paperclip'));
    if (task.Body.some((b) => b.Type === NoteBlockType.Checklist)) indicators.push(icon('checklist'));
    info.innerHTML = `
      <div class="task-title ${task.IsDone ? 'done' : ''}">${task.IsPinned ? icon('pin', 'pin-inline') : ''}${escapeHtml(task.Text || '(untitled)')}</div>
      <div class="task-sub">${due ? `<span>${due}</span>` : ''}${indicators.length ? `<span class="task-indicators">${indicators.join('')}</span>` : ''}</div>
    `;

    li.append(checkbox, info);
    li.addEventListener('click', () => {
      selectedTaskId = task.Id;
      renderList();
      renderEditor(task);
      showMobileView('editor');
    });
    taskListEl.appendChild(li);
  }
}

function showEmptyEditor() {
  editorEmpty.classList.remove('hidden');
  editorContent.classList.add('hidden');
}

function renderEditor(task) {
  editorEmpty.classList.add('hidden');
  editorContent.classList.remove('hidden');

  editorDone.checked = task.IsDone;
  editorPinBtn.classList.toggle('active', task.IsPinned);
  editorTrashBtn.textContent = task.IsClosed ? 'Restore from Trash' : 'Move to Trash';
  editorDeleteBtn.classList.toggle('hidden', !task.IsClosed);

  editorTitle.value = task.Text;
  editorDue.value = task.DueDate ? toDateInputValue(parseDotNetDate(task.DueDate)) : '';
  editorRecurrence.value = String(task.Recurrence);

  editorTags.innerHTML = '';
  for (const tag of task.Tags) {
    const chip = document.createElement('span');
    chip.className = 'tag-chip';
    chip.innerHTML = `#${escapeHtml(tag)} <button aria-label="Remove tag">${icon('x')}</button>`;
    chip.querySelector('button').addEventListener('click', () => removeTag(task, tag));
    editorTags.appendChild(chip);
  }

  const onBodyChange = ({ rerenderBody }) => {
    touch(task);
    markDirty();
    renderList();
    if (rerenderBody) renderEditableBody(editorBody, task, onBodyChange);
  };
  renderEditableBody(editorBody, task, onBodyChange);
}

// --- Editor field listeners ---------------------------------------------------
editorTitle.addEventListener('input', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  task.Text = editorTitle.value;
  touch(task);
  markDirty();
  renderList();
});

editorDue.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  task.DueDate = editorDue.value ? formatDotNetDate(new Date(`${editorDue.value}T00:00:00`)) : null;
  touch(task);
  markDirty();
  renderList();
});

editorRecurrence.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  task.Recurrence = Number(editorRecurrence.value);
  touch(task);
  markDirty();
  renderSidebar();
});

editorTagInput.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || !editorTagInput.value.trim()) return;
  const task = findTask(selectedTaskId);
  if (!task) return;
  addTag(task, editorTagInput.value);
  editorTagInput.value = '';
});

editorDone.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (task) toggleDone(task);
});

editorPinBtn.addEventListener('click', () => {
  const task = findTask(selectedTaskId);
  if (task) togglePin(task);
});

editorTrashBtn.addEventListener('click', () => {
  const task = findTask(selectedTaskId);
  if (task) toggleTrash(task);
});

editorDeleteBtn.addEventListener('click', () => {
  const task = findTask(selectedTaskId);
  if (task) deleteForever(task);
});

newTaskBtn.addEventListener('click', createTask);
sidebarNewTaskBtn.addEventListener('click', createTask);
emptyTrashBtn.addEventListener('click', emptyTrash);
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') {
    e.preventDefault();
    createTask();
  }
});

searchBox.addEventListener('input', () => {
  searchQuery = searchBox.value;
  renderList();
});
sortSelect.addEventListener('change', () => {
  sortKey = sortSelect.value;
  renderList();
});
quickFilterSelect.addEventListener('change', () => {
  quickFilter = quickFilterSelect.value;
  renderList();
});

// --- Mobile / tablet navigation ---------------------------------------------
function showMobileView(view) {
  appEl.dataset.view = view;
  renderMobileTabbar();
}
navBack.addEventListener('click', () => {
  showMobileView(appEl.dataset.view === 'editor' ? 'list' : 'sidebar');
});
sidebarDrawerBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  const open = appEl.dataset.sidebarOpen === 'true';
  appEl.dataset.sidebarOpen = String(!open);
});
appEl.dataset.view = 'sidebar';

// --- Helpers ------------------------------------------------------------
function formatDate(date) {
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}
function toDateInputValue(date) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
function escapeHtml(str) {
  const d = document.createElement('div');
  d.textContent = str;
  return d.innerHTML;
}

boot();
