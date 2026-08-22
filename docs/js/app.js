import * as auth from './auth.js?v=27';
import * as drive from './drive.js?v=27';
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
  blockHasInlineFile,
} from './model.js?v=27';
import { deduplicateTombstones, mergeRemoteState } from './sync.js?v=27';
import { renderEditableBody } from './editor.js?v=27';
import { icon } from './icons.js?v=27';
import { DEFAULT_DATA_FILE_NAME, DESKTOP_VERSION } from './config.js?v=27';

const el = (id) => document.getElementById(id);
const signinScreen = el('signin-screen');
const signinBtn = el('signin-btn');
const signinStatus = el('signin-status');
const signinVersionEl = el('signin-version');
const aboutVersionEl = el('about-version');
// DESKTOP_VERSION is the one hand-maintained piece (see its comment in config.js) - the build
// number after it is derived from this very module's own URL, so it's always accurate even
// between desktop releases without anyone having to remember to bump a second thing.
{
  const build = new URL(import.meta.url).searchParams.get('v');
  const versionText = build ? `v${DESKTOP_VERSION} (build ${build})` : `v${DESKTOP_VERSION}`;
  signinVersionEl.textContent = versionText;
  aboutVersionEl.textContent = versionText;
}
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
const listFilterRow = el('list-filter-row');
const filterToggleBtn = el('filter-toggle-btn');
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
const accountNameEl = el('account-name');
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
const doneActionsRow = el('done-actions-row');
const moveAllTrashBtn = el('move-all-trash-btn');
const mobileTabbar = el('mobile-tabbar');
const paneResizer = el('pane-resizer');
const offlineBanner = el('offline-banner');

// Lightweight offline awareness only (no service worker / offline launch support - the app still
// needs network to load at all). Keeps the user from staring at a silent failure or a raw fetch
// error when they're simply out of signal, which is the common case on mobile.
//
// The banner is position:fixed (it has to be - it's not inside .app or .signin-screen, both of
// which exist as separate, mutually-hidden trees), so showing it doesn't push anything down on
// its own; it would otherwise sit on top of and obscure whatever's at the top of the viewport
// (the header's Sync/Account buttons, or the sign-in card). --offline-banner-height is measured
// from the real element (not hardcoded) since its text can wrap to two lines on a narrow phone,
// and CSS uses it to pad .app/.signin-screen down by exactly that much only while it's shown.
function updateOfflineBanner() {
  const offline = !navigator.onLine;
  offlineBanner.classList.toggle('hidden', !offline);
  document.body.classList.toggle('offline-banner-visible', offline);
  if (offline) {
    document.documentElement.style.setProperty('--offline-banner-height', `${offlineBanner.offsetHeight}px`);
  }
}
window.addEventListener('online', updateOfflineBanner);
window.addEventListener('offline', updateOfflineBanner);
// Re-measure if the banner's wrapped line count changes (e.g. rotating the phone) while it's
// already shown - only matters while offline, so this is a cheap no-op the rest of the time.
window.addEventListener('resize', () => {
  if (!navigator.onLine) updateOfflineBanner();
});
updateOfflineBanner();

function friendlyErrorMessage(prefix, err) {
  return navigator.onLine ? `${prefix}: ${err.message}` : `${prefix}: no internet connection`;
}

const SECTION_ICONS = { all: 'list', recurring: 'repeat', done: 'check', trash: 'trash' };
navBack.innerHTML = icon('back');
sidebarDrawerBtn.innerHTML = icon('menu');
menuBtn.innerHTML = icon('menu');
syncNowBtn.innerHTML = icon('sync');
newTaskBtn.innerHTML = `${icon('plus')}<span class="sidebar-item-label">New Task</span>`;
sidebarNewTaskBtn.innerHTML = `${icon('plus')}<span class="sidebar-item-label">New Task</span>`;
filterToggleBtn.innerHTML = icon('filter');
editorPinBtn.innerHTML = icon('pin');
sidebarCollapseBtn.innerHTML = icon('chevronLeft');
aboutCloseBtn.innerHTML = icon('x');
document.querySelector('button[data-theme-choice="light"]').innerHTML = icon('sun');
document.querySelector('button[data-theme-choice="dark"]').innerHTML = icon('moon');
document.querySelector('button[data-theme-choice="system"]').innerHTML = icon('monitor');

let noRemoteFileYet = false;
let loadError = null;
let loadErrorIsAuthFailure = false;

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

const STATUS_AUTOHIDE_MS = 3000;
let statusHideTimer = null;
// Transient confirmations ("Saved", "Loaded N task(s)") auto-clear so the status text isn't
// permanently occupying space; anything the user might need to act on or that signals an
// in-progress/error state (Saving…, Sync failed, Signed out — click to reconnect) stays put.
function setStatus(text, { autoHide = false } = {}) {
  clearTimeout(statusHideTimer);
  saveStatus.textContent = text;
  // Desktop truncates long messages (a Drive API error includes the full request URL and
  // reason) with an ellipsis to avoid disrupting the header layout - the title attribute
  // still exposes the complete text on hover.
  saveStatus.title = text;
  if (autoHide) {
    statusHideTimer = setTimeout(() => {
      if (saveStatus.textContent === text) saveStatus.textContent = '';
    }, STATUS_AUTOHIDE_MS);
  }
}

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

  const redirectResult = await auth.handleRedirectReturn();
  if (redirectResult.status === 'success') {
    await onSignedIn();
    armHistoryTrap();
    return;
  }
  if (redirectResult.status === 'error') {
    signinStatus.textContent = redirectResult.message;
  }
  if (auth.restoreFromCache()) {
    await onSignedIn();
  }
}

// The redirect sign-in flow (see auth.js) is a real full-page navigation to Google's consent
// screen and back, which leaves Google's own page as a genuine entry in the tab's history -
// there's no API to delete a specific past entry, only to replace the current one or push new
// ones. Left alone, pressing Android's back button (or any back navigation) from the freshly
// signed-in app lands straight back on that stale, already-consumed OAuth page. Only worth
// arming when a redirect round-trip actually just happened this session - a returning visit
// restored from the cached token never touches Google at all, so there's nothing poisoning
// history to guard against there.
function armHistoryTrap() {
  const plantAnchor = () => history.pushState({ tasky: true }, '', location.pathname + location.search);
  plantAnchor();
  window.addEventListener('popstate', plantAnchor);
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
document.addEventListener('click', (e) => {
  closeDropdowns({});
  // Mobile-only popup (Sort/Filter collapsed behind one icon - see filterToggleBtn below);
  // harmless no-op on desktop/tablet where CSS keeps it permanently visible regardless of this
  // class. Guarded by containment, unlike closeDropdowns, because it holds interactive <select>
  // elements - the plain "close on every click" the other dropdowns use would hide it out from
  // under a click that was just trying to open one of those selects.
  if (!listFilterRow.contains(e.target)) listFilterRow.classList.add('hidden');
});
filterToggleBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  closeDropdowns({});
  const wasHidden = listFilterRow.classList.contains('hidden');
  if (wasHidden) {
    const rect = filterToggleBtn.getBoundingClientRect();
    listFilterRow.style.top = `${rect.bottom + 6}px`;
  }
  listFilterRow.classList.toggle('hidden', !wasHidden);
});
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

// Reconnecting is a full-page redirect to Google and back - AppState only ever lives in memory
// (see the "online-only" decision), so anything still unsaved at that exact moment is lost with
// no way to recover it afterward. Warn before navigating away if there's actually something to lose.
function confirmSignInIfDirty() {
  if (dirty && !confirm('You have unsaved changes that will be lost when you reconnect. Continue anyway?')) {
    return false;
  }
  auth.signIn();
  return true;
}

// --- Sync now -----------------------------------------------------------------
syncNowBtn.addEventListener('click', async () => {
  if (!auth.isSignedIn()) {
    confirmSignInIfDirty(); // redirects away and back; the resumed session syncs normally on return
    return;
  }
  clearTimeout(saveTimer);
  syncNowBtn.classList.add('spinning');
  await performSave({ force: true, statusVerb: 'Sync' });
  syncNowBtn.classList.remove('spinning');
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

// --- List pane resize (desktop only - see .pane-resizer, hidden below 1024px) -------------
const LIST_PANE_WIDTH_KEY = 'tasky-list-pane-width';
const LIST_PANE_MIN_WIDTH = 240;
const LIST_PANE_MAX_WIDTH = 640;

function setListPaneWidth(px) {
  const clamped = Math.min(LIST_PANE_MAX_WIDTH, Math.max(LIST_PANE_MIN_WIDTH, px));
  appBody.style.setProperty('--list-pane-width', `${clamped}px`);
  // Chrome can leave a transitioned grid-template-columns frozen at its pre-change value when
  // only the var() it depends on changes, unless something forces a layout read - the .resizing
  // class disables the transition during a drag, but without this the very first frame (or a
  // drag that ends on a rapid pointerup) can paint stale until some unrelated reflow happens to
  // bail it out.
  void appBody.offsetHeight;
  return clamped;
}

const savedListPaneWidth = Number(localStorage.getItem(LIST_PANE_WIDTH_KEY));
if (savedListPaneWidth) setListPaneWidth(savedListPaneWidth);

paneResizer.addEventListener('pointerdown', (e) => {
  try {
    paneResizer.setPointerCapture(e.pointerId);
  } catch {
    // Capture keeps the drag alive even if the cursor outruns the 6px handle mid-move; without
    // it the drag still mostly works since pointermove/up stay bound below, just less reliably
    // on very fast movements - not worth aborting the whole gesture over.
  }
  paneResizer.classList.add('dragging');
  appBody.classList.add('resizing');
  const sidebarWidth = appBody.querySelector('.sidebar').getBoundingClientRect().width;
  const bodyLeft = appBody.getBoundingClientRect().left;
  let lastWidth = LIST_PANE_MIN_WIDTH;

  function onMove(moveEvent) {
    lastWidth = setListPaneWidth(moveEvent.clientX - bodyLeft - sidebarWidth);
  }
  function onUp() {
    paneResizer.classList.remove('dragging');
    appBody.classList.remove('resizing');
    localStorage.setItem(LIST_PANE_WIDTH_KEY, String(lastWidth));
    paneResizer.removeEventListener('pointermove', onMove);
    paneResizer.removeEventListener('pointerup', onUp);
  }
  paneResizer.addEventListener('pointermove', onMove);
  paneResizer.addEventListener('pointerup', onUp);
});

async function onSignedIn() {
  signinScreen.classList.add('hidden');
  appEl.classList.remove('hidden');
  setStatus('Loading…');

  const email = auth.getAccountEmail();
  const name = auth.getAccountName();
  const picture = auth.getAccountPicture();
  if (picture) {
    accountBtn.innerHTML = `<img src="${picture}" alt="" referrerpolicy="no-referrer" />`;
  } else if (email) {
    accountBtn.textContent = email[0].toUpperCase();
  }
  if (email) {
    accountBtn.title = name ? `Signed in as ${name} (${email})` : `Signed in as ${email}`;
    accountNameEl.textContent = name ?? '';
    accountEmailEl.textContent = email;
  }

  await loadFromDriveWithRetry();
}

async function loadFromDriveWithRetry() {
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
      setStatus(`Loaded ${appState.Tasks.length} task(s)`, { autoHide: true });
    } else {
      drive.setSyncContext(taskyFolderId, DEFAULT_DATA_FILE_NAME);
      setStatus('');
      noRemoteFileYet = true;
    }

    loadError = null;
    loadErrorIsAuthFailure = false;
    renderSidebar();
    renderList();
  } catch (err) {
    console.error(err);
    // Leaving the list pane blank here would look identical to "nothing to show," with only the
    // easy-to-miss header status line explaining why - render an explicit error + retry instead,
    // reusing the same empty-state slot renderList() already owns.
    loadErrorIsAuthFailure = err.message === 'NOT_SIGNED_IN';
    if (loadErrorIsAuthFailure) {
      setStatus('Signed out — click to reconnect');
      saveStatus.classList.add('save-status-action');
      loadError = 'signed out';
    } else {
      setStatus(friendlyErrorMessage('Load failed', err));
      loadError = navigator.onLine ? err.message : 'no internet connection';
    }
    renderSidebar();
    renderList();
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
        return t.Body.some((b) => b.Type === NoteBlockType.Photo || b.Type === NoteBlockType.File || blockHasInlineImage(b) || blockHasInlineFile(b));
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
  setStatus('Unsaved changes…');
  clearTimeout(saveTimer);
  saveTimer = setTimeout(triggerSave, SAVE_DEBOUNCE_MS);
}

async function triggerSave() {
  if (saving) {
    saveTimer = setTimeout(triggerSave, 2000);
    return;
  }
  await performSave({ force: false, statusVerb: 'Save' });
}

// Shared by the autosave debounce (triggerSave) and the manual Sync Now button - they used to
// be two independent copies of this same guard/save/error-handling logic and had already drifted
// (Sync Now cleared `dirty` unconditionally instead of checking it first, and its success path
// never cleared the `save-status-action` reconnect-click-trap class the way triggerSave's did) -
// see the code review that caught this. `force` is what lets Sync Now still pull+push even when
// nothing is locally dirty; `statusVerb` only changes the wording ("Saving…"/"Save failed" vs.
// "Syncing…"/"Sync failed") since autosave and a manual sync are the same operation underneath.
async function performSave({ force, statusVerb }) {
  if (saving) {
    if (!force) saveTimer = setTimeout(triggerSave, 2000);
    return;
  }
  if (!force && !dirty) return;
  const hadLocalEdits = dirty;
  saving = true;
  dirty = false;
  setStatus(`${statusVerb === 'Save' ? 'Saving' : 'Syncing'}…`);
  try {
    await saveToDrive();
    setStatus('Saved', { autoHide: true });
    saveStatus.classList.remove('save-status-action');
    setLastSynced(new Date());
  } catch (err) {
    dirty = hadLocalEdits; // don't invent an unsaved edit that was never there (e.g. a pull-only Sync Now)
    // getAccessToken() deliberately throws instead of attempting a background reauth (that's
    // the same unwanted-popup problem this debounce timer isn't allowed to trigger) - surface
    // it as a click target instead, since a click IS allowed to reauth. driveFetch() throws this
    // same sentinel for a token Google rejected mid-sync too, not just a missing one.
    if (err.message === 'NOT_SIGNED_IN') {
      setStatus('Signed out — click to reconnect');
      saveStatus.classList.add('save-status-action');
    } else {
      setStatus(friendlyErrorMessage(`${statusVerb} failed`, err));
      console.error(err);
    }
  } finally {
    saving = false;
  }
}

// Android backgrounds tabs aggressively (switching apps, the home/back gesture, the OS reclaiming
// memory) and throttles or fully suspends JS timers once hidden - the 10s autosave debounce may
// simply never get to fire before the page is gone, silently losing whatever was just typed.
// visibilitychange is the standard mobile-safe signal for this (unlike beforeunload, which mobile
// browsers don't reliably fire just for backgrounding rather than closing): flush immediately the
// moment the page goes hidden instead of waiting out the rest of the debounce window.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden' && dirty) {
    clearTimeout(saveTimer);
    triggerSave();
  }
});

saveStatus.addEventListener('click', () => {
  if (!saveStatus.classList.contains('save-status-action')) return;
  // Nothing left to retry here after the redirect either way - the resumed session picks back up
  // normally via boot()'s handleRedirectReturn(). confirmSignInIfDirty() is what stops this from
  // silently discarding an in-flight edit (see its own comment).
  confirmSignInIfDirty();
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

function moveAllDoneToTrash() {
  const done = appState.Tasks.filter((t) => !t.IsClosed && t.IsDone);
  if (done.length === 0) return;
  if (!confirm(`Move ${done.length} completed task(s) to Trash?`)) return;

  for (const task of done) {
    task.IsClosed = true;
    touch(task);
  }
  if (selectedTaskId && done.some((t) => t.Id === selectedTaskId)) {
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
    li.innerHTML = `<span>#${escapeHtml(tag)}</span>`;
    if (currentSection.kind === 'tag' && currentSection.tag === tag) li.classList.add('active');
    li.addEventListener('click', () => selectSection({ kind: 'tag', tag }));
    tagList.appendChild(li);
  }

  renderMobileTabbar();
}

// Mobile-only bottom tab bar: the 4 fixed sections get one tap instead of open-drawer-then-pick,
// with a "More" tab standing in for Tags (a variable-length list that can't be fixed tabs) and
// for the sidebar screen generally. Stays visible on the editor view too, so navigation is always
// one tap away instead of back-then-tap.
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
  // Creating a task always drops it into "All Tasks" (see createTask) - offering the button while
  // looking at Completed or Trash would just be a confusing way to leave the page you're on.
  newTaskBtn.classList.toggle('hidden', currentSection.kind === 'done' || currentSection.kind === 'trash');
  trashActionsRow.classList.toggle('hidden', currentSection.kind !== 'trash' || tasksForSection({ kind: 'trash' }).length === 0);
  doneActionsRow.classList.toggle('hidden', currentSection.kind !== 'done' || tasksForSection({ kind: 'done' }).length === 0);
  taskListEl.innerHTML = '';
  listEmpty.classList.toggle('hidden', tasks.length > 0 && !loadError);
  if (loadError) {
    listEmpty.innerHTML = '';
    const msg = document.createElement('p');
    msg.textContent = `Couldn't load your tasks: ${loadError}`;
    const retryBtn = document.createElement('button');
    retryBtn.type = 'button';
    retryBtn.className = 'btn';
    // Retrying a signed-out failure as-is would just fail the same way again - offer the actual
    // fix (reconnect) instead of a retry that can't succeed.
    if (loadErrorIsAuthFailure) {
      retryBtn.textContent = 'Reconnect';
      retryBtn.addEventListener('click', () => confirmSignInIfDirty());
    } else {
      retryBtn.textContent = 'Retry';
      retryBtn.addEventListener('click', () => loadFromDriveWithRetry());
    }
    listEmpty.append(msg, retryBtn);
  } else {
    listEmpty.textContent =
      noRemoteFileYet && appState.Tasks.length === 0
        ? 'No Tasky file on Drive yet — create your first task and one will be set up automatically.'
        : 'No tasks here.';
  }

  for (const task of tasks) {
    const li = document.createElement('li');
    if (task.Id === selectedTaskId) li.classList.add('selected');

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = task.IsDone;
    checkbox.title = 'Mark done';
    // change fires exactly once per real toggle no matter how it was triggered (direct click on
    // the checkbox, or the browser's native label-forwarded click below) - using click here
    // instead double-fired: a tap that lands on the label's padding (not the checkbox itself)
    // produces two independent bubbling click events (the real one, target=label; a second one
    // the browser synthesizes and dispatches on the checkbox to activate it), so a click listener
    // on the checkbox alone would toggle correctly but couldn't stop the first (label-targeted)
    // event from also reaching the row's own click-to-open handler below.
    checkbox.addEventListener('change', () => toggleDone(task));
    // A <label> wrapper is the only reliably cross-browser way to grow a native checkbox's tap
    // target without growing its visible size - unlike buttons/divs, padding on the checkbox
    // element itself is not consistently respected for hit-testing (confirmed: computed padding
    // stayed 0 despite matching CSS). stopPropagation lives on the LABEL, not the checkbox, so it
    // catches both of the click events described above (whichever one the label sees, either as
    // the click's target or while it bubbles through as an ancestor) before either can reach `li`.
    const checkboxWrap = document.createElement('label');
    checkboxWrap.className = 'checkbox-tap-target';
    checkboxWrap.addEventListener('click', (e) => e.stopPropagation());
    checkboxWrap.appendChild(checkbox);

    const info = document.createElement('div');
    info.className = 'task-row-info';
    const due = task.DueDate ? formatDate(parseDotNetDate(task.DueDate)) : '';
    const indicators = [];
    if (task.Recurrence !== RecurrenceRule.None) indicators.push(icon('repeat'));
    if (task.Body.some((b) => b.Type === NoteBlockType.Link)) indicators.push(icon('link'));
    if (task.Body.some((b) => b.Type === NoteBlockType.Photo || blockHasInlineImage(b))) indicators.push(icon('image'));
    if (task.Body.some((b) => b.Type === NoteBlockType.File || blockHasInlineFile(b))) indicators.push(icon('paperclip'));
    if (task.Body.some((b) => b.Type === NoteBlockType.Checklist)) indicators.push(icon('checklist'));
    info.innerHTML = `
      <div class="task-title ${task.IsDone ? 'done' : ''}">${task.IsPinned ? icon('pin', 'pin-inline') : ''}${escapeHtml(task.Text || '(untitled)')}</div>
      <div class="task-sub">${due ? `<span>${due}</span>` : ''}${indicators.length ? `<span class="task-indicators">${indicators.join('')}</span>` : ''}</div>
    `;

    li.append(checkboxWrap, info);
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

  const onBodyChange = ({ rerenderBody, error, isAuthFailure }) => {
    touch(task);
    markDirty();
    renderList();
    if (error) {
      setStatus(error);
      saveStatus.classList.toggle('save-status-action', !!isAuthFailure);
    }
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

function commitTagInput() {
  if (!editorTagInput.value.trim()) return;
  const task = findTask(selectedTaskId);
  if (!task) return;
  addTag(task, editorTagInput.value);
  editorTagInput.value = '';
}
editorTagInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') commitTagInput();
});
// Many Android on-screen keyboards (Gboard included, with word prediction active) never fire a
// real keydown for the Enter/Done key - they only surface it here, as an input event carrying
// inputType "insertLineBreak". Without this, Enter silently did nothing on those keyboards even
// though the exact same code path worked fine everywhere else. Reuses commitTagInput()'s own
// guard (empty value after the keydown handler already ran) so a keyboard that fires both isn't
// double-handled.
editorTagInput.addEventListener('input', (e) => {
  if (e.inputType === 'insertLineBreak') commitTagInput();
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
moveAllTrashBtn.addEventListener('click', moveAllDoneToTrash);
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
