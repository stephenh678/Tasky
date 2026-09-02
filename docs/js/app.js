import * as auth from './auth.js?v=22';
import * as drive from './drive.js?v=22';
import {
  NoteBlockType,
  RecurrenceRule,
  TaskPriority,
  parseDotNetDate,
  formatDotNetDate,
  nowDotNet,
  newTaskItem,
  newTaskSyncRecord,
  spawnNextOccurrence,
  blockHasInlineImage,
  blockHasInlineFile,
  parseQuickAdd,
} from './model.js?v=22';
import { deduplicateTombstones, mergeRemoteState, mergeSavedViews } from './sync.js?v=22';
import { renderEditableBody } from './editor.js?v=22';
import { icon } from './icons.js?v=22';
import { DEFAULT_DATA_FILE_NAME, DESKTOP_VERSION } from './config.js?v=22';

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
const brandHomeBtn = el('brand-home-btn');
const sidebarList = el('sidebar-list');
const tagList = el('tag-list');
const savedViewsList = el('saved-views-list');
const taskListEl = el('task-list');
const listEmpty = el('list-empty');
const searchBox = el('search-box');
const searchClearBtn = el('search-clear-btn');
const quickAddInput = el('quick-add-input');
const quickAddRow = document.querySelector('.quick-add-row');
const quickAddPreview = el('quick-add-preview');
const quickAddPopup = el('quick-add-popup');
const quickAddPopupInput = el('quick-add-popup-input');
const quickAddPopupPreview = el('quick-add-popup-preview');
const newTaskBtn = el('new-task-btn');
const sidebarNewTaskBtn = el('sidebar-new-task-btn');
const sortChipGroup = el('sort-chip-group');
const filterChipGroup = el('filter-chip-group');
const listFilterRow = el('list-filter-row');
const filterToggleBtn = el('filter-toggle-btn');
const saveViewBtn = el('save-view-btn');
const editorEmpty = el('editor-empty');
const emptyDueTodayCount = el('empty-due-today-count');
const emptyOverdueCount = el('empty-overdue-count');
const emptyCompletedCount = el('empty-completed-count');
const emptyAllCount = el('empty-all-count');
const emptyDueTodayCard = el('empty-due-today-card');
const emptyOverdueCard = el('empty-overdue-card');
const emptyCompletedCard = el('empty-completed-card');
const emptyAllCard = el('empty-all-card');
const emptyTagsLink = el('empty-tags-link');
const emptySettingsLink = el('empty-settings-link');
const emptyAddTaskBtn = el('empty-add-task-btn');
const emptyShortcutsBtn = el('empty-shortcuts-btn');
const editorContent = el('editor-content');
const editorTitle = el('editor-title');
const editorDue = el('editor-due');
const editorPriority = el('editor-priority');
const editorRecurrence = el('editor-recurrence');
const editorRecurrenceIntervalField = el('editor-recurrence-interval-field');
const editorRecurrenceInterval = el('editor-recurrence-interval');
// ROADMAP.md #31: 1-30, mirrors desktop's TaskDetailViewModel.RecurrenceIntervalOptions.
for (let i = 1; i <= 30; i++) {
  const opt = document.createElement('option');
  opt.value = String(i);
  opt.textContent = String(i);
  editorRecurrenceInterval.appendChild(opt);
}
const editorTags = el('editor-tags');
const editorTagInput = el('editor-tag-input');
const tagSuggestPopup = el('tag-suggest-popup');
const editorBody = el('editor-body');
const editorDone = el('editor-done');
const editorDoneField = el('editor-done-field');
const editorPinBtn = el('editor-pin-btn');
const editorPinLabel = el('editor-pin-label');
const editorMoreBtn = el('editor-more-btn');
const editorMoreDropdown = el('editor-more-dropdown');
const editorDoneBtn = el('editor-done-btn');
const editorTrashBtn = el('editor-trash-btn');
const editorDeleteBtn = el('editor-delete-btn');
const saveStatus = el('save-status');
const saveProgress = el('save-progress');
const saveProgressFill = el('save-progress-fill');
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
const aboutDropdown = el('about-dropdown');
const shortcutsBtn = el('shortcuts-btn');
const shortcutsModal = el('shortcuts-modal');
const shortcutsCloseBtn = el('shortcuts-close-btn');
const onboardingModal = el('onboarding-modal');
const onboardingQuickAddTip = el('onboarding-quickadd-tip');
const onboardingAddSamplesCheck = el('onboarding-add-samples');
const onboardingDoneBtn = el('onboarding-done-btn');
const aboutReplayTourBtn = el('about-replay-tour-btn');
const settingsBtn = el('settings-btn');
const exportMarkdownBtn = el('export-markdown-btn');
const exportHtmlBtn = el('export-html-btn');
const settingsDropdown = el('settings-dropdown');
const showDoneCheckboxToggle = el('setting-show-done-checkbox');
const autoEmptyTrashToggle = el('setting-auto-empty-trash');
const autoEmptyTrashDaysRow = el('setting-auto-empty-trash-days-row');
const autoEmptyTrashDaysSelect = el('setting-auto-empty-trash-days');
const undoToast = el('undo-toast');
const undoToastText = el('undo-toast-text');
const undoToastBtn = el('undo-toast-btn');
const sidebarDrawerBtn = el('sidebar-drawer-btn');
const sidebarCollapseBtn = el('sidebar-collapse-btn');
const navBack = el('nav-back');
const trashActionsRow = el('trash-actions-row');
const emptyTrashBtn = el('empty-trash-btn');
const doneActionsRow = el('done-actions-row');
const moveAllTrashBtn = el('move-all-trash-btn');
const selectToggleBtn = el('select-toggle-btn');
const bulkActionsRow = el('bulk-actions-row');
const bulkSelectedCount = el('bulk-selected-count');
const bulkSelectAllBtn = el('bulk-select-all-btn');
const bulkDoneBtn = el('bulk-done-btn');
const bulkPinBtn = el('bulk-pin-btn');
const bulkDueBtn = el('bulk-due-btn');
const bulkDueInput = el('bulk-due-input');
const bulkTagBtn = el('bulk-tag-btn');
const bulkTagPopup = el('bulk-tag-popup');
const bulkTagInput = el('bulk-tag-input');
const bulkTagSuggestions = el('bulk-tag-suggestions');
const bulkTrashBtn = el('bulk-trash-btn');
const bulkRestoreBtn = el('bulk-restore-btn');
const bulkDeleteBtn = el('bulk-delete-btn');
const bulkCancelBtn = el('bulk-cancel-btn');
const mobileTabbar = el('mobile-tabbar');
const moreSheetPopup = el('more-sheet-popup');
const moreSheetDashboardBtn = el('more-sheet-dashboard');
const moreSheetSectionsBtn = el('more-sheet-sections');
const moreSheetSettingsBtn = el('more-sheet-settings');
const moreSheetAboutBtn = el('more-sheet-about');
const paneResizer = el('pane-resizer');
const offlineBanner = el('offline-banner');
const installBanner = el('install-banner');
const installActionBtn = el('install-action-btn');
const installDismissBtn = el('install-dismiss-btn');
const installIosHint = el('install-ios-hint');
const installBannerMsg = el('install-banner-msg');

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
  if (selectedTaskId) autoResizeEditorTitle();
});
updateOfflineBanner();

// Soft-keyboard awareness on mobile. visualViewport shrinks (independently of window.innerHeight)
// when the on-screen keyboard opens - not supported on every browser this app otherwise runs on,
// hence the guard. Two things react to it:
//  1. body.keyboard-open, toggled past a threshold well above normal rotation/URL-bar-collapse
//     deltas, hides the fixed bottom tab bar (see styles.css) so it can't end up floating
//     mid-screen or hidden behind the keyboard.
//  2. The focused field gets explicitly scrolled into view - .pane elements scroll
//     independently of the page, which the browser's own native "scroll focused input into view"
//     heuristic doesn't always handle correctly through nested scroll containers.
const KEYBOARD_HEIGHT_THRESHOLD = 150;
if (window.visualViewport) {
  window.visualViewport.addEventListener('resize', () => {
    const keyboardOpen = window.innerHeight - window.visualViewport.height > KEYBOARD_HEIGHT_THRESHOLD;
    document.body.classList.toggle('keyboard-open', keyboardOpen);

    if (keyboardOpen) {
      const active = document.activeElement;
      if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable)) {
        // The viewport resize fires before the browser finishes settling layout around it, so an
        // immediate scrollIntoView measures against a still-stale layout.
        setTimeout(() => active.scrollIntoView({ block: 'center', behavior: 'smooth' }), 50);
      }
    }
  });
}

function friendlyErrorMessage(prefix, err) {
  return navigator.onLine ? `${prefix}: ${err.message}` : `${prefix}: no internet connection`;
}

const SECTION_ICONS = { today: 'calendar', all: 'list', recurring: 'repeat', done: 'check', trash: 'trash' };
navBack.innerHTML = icon('back');
sidebarDrawerBtn.innerHTML = icon('menu');
menuBtn.innerHTML = icon('menu');
syncNowBtn.innerHTML = icon('sync');
newTaskBtn.innerHTML = `${icon('plus')}<span class="sidebar-item-label">New Task</span>`;
sidebarNewTaskBtn.innerHTML = `${icon('plus')}<span class="sidebar-item-label">New Task</span>`;
filterToggleBtn.innerHTML = `${icon('filter')}<span id="filter-badge" class="filter-badge hidden"></span>`;
const filterBadge = el('filter-badge');
selectToggleBtn.innerHTML = icon('checkSquare');
el('editor-pin-icon').innerHTML = icon('pin');
editorMoreBtn.innerHTML = icon('moreVertical');
el('editor-due-icon').innerHTML = icon('calendar');
el('editor-priority-icon').innerHTML = icon('flag');
el('editor-repeat-icon').innerHTML = icon('repeat');
// Native date/select controls each draw their own tiny, OS-styled open affordance (a calendar
// glyph, a dropdown arrow) at the far right of these pills - low-contrast in dark mode and
// reported live as "hard to read" and "a difficult workflow" (the exact pixel had to be tapped,
// especially on the date field, for the picker to open). CSS below hides both native indicators;
// this chevron - one consistent, higher-contrast glyph - replaces them, paired with the
// whole-pill showPicker() handlers below so a tap anywhere on the pill opens the picker.
for (const arrow of document.querySelectorAll('.editor-field-arrow')) arrow.innerHTML = icon('chevronDown');
sidebarCollapseBtn.innerHTML = icon('chevronLeft');
shortcutsCloseBtn.innerHTML = icon('x');
searchClearBtn.innerHTML = icon('x');
installDismissBtn.innerHTML = icon('x');
undoToastBtn.innerHTML = 'Undo';
emptyAddTaskBtn.innerHTML = `${icon('plus')}<span>Add Task</span>`;
moreSheetDashboardBtn.textContent = 'Dashboard';
moreSheetSectionsBtn.textContent = 'Sections & Tags';
document.querySelector('button[data-theme-choice="light"]').innerHTML = icon('sun');
document.querySelector('button[data-theme-choice="dark"]').innerHTML = icon('moon');
document.querySelector('button[data-theme-choice="system"]').innerHTML = icon('monitor');
// (hover: hover) and (pointer: fine) is true for a device whose PRIMARY input is a mouse/trackpad
// (real hover states, precise pointer) and false for one primarily driven by touch - the same
// signal attachQuickAddTrigger's gesture choice implicitly depends on, just read once here and
// reused below (both for this tip and for the sample tasks maybeShowOnboarding seeds) instead of
// duplicating the check per use. A touchscreen laptop with a mouse still reads as "fine" (hover
// works), which is the right call here too - it's still primarily mouse-driven.
const primaryInputIsMouse = window.matchMedia('(hover: hover) and (pointer: fine)').matches;
onboardingQuickAddTip.textContent = primaryInputIsMouse
  ? 'Right-click any + New Task/Add Task button for a floating quick-add box you can open from anywhere in the app'
  : 'Long-press any + New Task/Add Task button for a floating quick-add box you can open from anywhere in the app';

let noRemoteFileYet = false;
let loadError = null;
let loadErrorIsAuthFailure = false;
let loadErrorNeedsDriveConsent = false;

let appState = { Tasks: [], DeletedTasks: [], SavedViews: [], DeletedSavedViewIds: [] };
let currentFileId = null;
let currentFileName = DEFAULT_DATA_FILE_NAME;
let taskyFolderId = null;
let currentSection = { kind: 'all' }; // {kind:'all'|'recurring'|'done'|'trash'|'tag', tag?}
let selectedTaskId = null;
let searchQuery = '';
let sortKey = 'modified';
let quickFilter = '';
// taskId -> row refs for renderList()'s keyed diff - lets a row already on screen be patched in
// place (classes/text/checkbox swapped) instead of torn down and rebuilt, which previously
// happened on every render including every single keystroke in the title field or search box
// (see #67). Click/swipe listeners close over `task`, a stable object reference that's mutated in
// place elsewhere in this file rather than replaced, so a reused row's listeners stay correct
// without rebinding.
let taskRowRefs = new Map();
// Bulk multi-select (#142) - mirrors desktop's SelectedTasks/Bulk* commands (mark done/trash/
// restore/delete, MainViewModel.cs InitializeBulkCommands), reimplemented on web with an explicit
// mode toggle since there's no ctrl/shift-click equivalent on touch. selectedIds intentionally
// isn't cleared on every renderList() - only on cancel/section-change/successful bulk action - so
// a selection survives whatever re-render a background sync or a keystroke elsewhere triggers.
let selectionMode = false;
let selectedIds = new Set();

let dirty = false;
let saving = false;
let saveTimer = null;
const SAVE_DEBOUNCE_MS = 4000;

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

// ROADMAP.md #57: mirrors desktop's SyncProgressPercent/IsSyncing - coarse, stage-based percent
// (see saveToDrive/mergeFromRemote below for where each stage actually reports), not true byte
// progress (no transfer-progress event on the fetch calls driveFetch/uploadFileText make). Passing
// null hides the bar; any number shows it at that width.
function setSyncProgress(percent) {
  saveProgress.classList.toggle('hidden', percent === null);
  if (percent === null) return;
  saveProgressFill.style.width = `${percent}%`;
  saveProgress.setAttribute('aria-valuenow', String(percent));
}

// --- Undo (mirrors the desktop app's Ctrl+Z stack) ---------------------------
// Scope matches the desktop app exactly (see MainViewModel.cs's PushUndo call sites): marking a
// task complete/incomplete, and moving a single task to Trash. Restoring from Trash and
// permanent delete are deliberately NOT undoable there either - restoring is already the "undo"
// of trashing, and permanent delete already has its own confirm() dialog.
const undoStack = [];
const MAX_UNDO_DEPTH = 25;
const UNDO_TOAST_MS = 6000;
let undoToastTimer = null;

function pushUndo(description, undo) {
  undoStack.push({ description, undo });
  if (undoStack.length > MAX_UNDO_DEPTH) undoStack.shift();
  showUndoToast(description);
}

function popUndo() {
  const entry = undoStack.pop();
  if (!entry) return;
  entry.undo();
  hideUndoToast();
  markDirty();
  renderSidebar();
  renderList();
  const task = selectedTaskId ? findTask(selectedTaskId) : null;
  if (task) renderEditor(task);
  else showEmptyEditor();
}

function showUndoToast(description) {
  clearTimeout(undoToastTimer);
  undoToastText.textContent = description;
  undoToast.classList.remove('hidden');
  undoToastTimer = setTimeout(hideUndoToast, UNDO_TOAST_MS);
}

function hideUndoToast() {
  clearTimeout(undoToastTimer);
  undoToast.classList.add('hidden');
}

undoToastBtn.addEventListener('click', popUndo);
document.addEventListener('keydown', (e) => {
  if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 'z') return;
  // Don't hijack the browser's native text-field undo (e.g. correcting a typo in the title or an
  // editor-body block, both real text-editing contexts) - only handle Ctrl+Z as an app-level
  // action outside one of those.
  const target = document.activeElement;
  const isEditable = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable);
  if (isEditable) return;
  e.preventDefault();
  popUndo();
});

const SECTIONS = [
  { kind: 'today', label: 'Today' },
  { kind: 'all', label: 'All Tasks' },
  { kind: 'recurring', label: 'Recurring' },
  { kind: 'done', label: 'Completed' },
  { kind: 'trash', label: 'Trash' },
];

// --- Boot: first check whether this load is Google redirecting back from sign-in, then fall
// back to the localStorage cache. Neither path ever risks a surprise redirect on page load -
// handleRedirectReturn() only acts on ?code=/?error= params that Google itself put there.
//
// No longer waits on Google Identity Services' script here - auth.js builds its own
// authorization URL by hand (see its header comment) and doesn't call any GIS API for sign-in,
// so nothing here depends on that script loading. Removing it entirely (ROADMAP.md #117 postmortem)
// also turned out to be required, not just a cleanup: merely having accounts.google.com/gsi/client
// present on the page was enough for Chrome to intercept the redirect to Google's authorization
// endpoint and substitute its own streamlined "Sign in with Google" identity-only flow, silently
// dropping the Drive scope no matter what was actually requested.
async function boot() {
  // handleRedirectReturn() below does a real network round-trip (code-for-token exchange, plus a
  // userinfo fetch - possibly a Cloud Run cold start) before it resolves, and signin-screen starts
  // visible with no JS needed to show it. Flipping the button straight to its normal "ready to
  // sign in" state here, before that await, meant it looked completely unchanged - full sign-in
  // screen, active "Sign in with Google" button - for that whole multi-second window right after
  // the user finished Google's consent screen and got redirected back. Reported live: "the sign in
  // still shows for a few moments then disappears... strange that this is displayed even after I
  // signed in." Detecting a redirect return up front (cheap, synchronous) and showing a distinct
  // "completing" state instead fixes that - it's still the same screen, but it no longer looks
  // like sign-in silently didn't register.
  const params = new URLSearchParams(window.location.search);
  const isRedirectReturn = params.has('code') || params.has('error');
  // manifest.json's "New Task" shortcut (long-press the installed PWA's icon on a phone home
  // screen, or right-click it on a desktop install's taskbar/Start menu) launches straight to this
  // URL - the actual web/PWA equivalent of the desktop app's tray-icon menu item and Ctrl+Alt+T
  // global hotkey, both reachable without the app already being open. Checked once here rather than
  // read fresh after sign-in, since a redirect round-trip through Google's consent screen (see
  // isRedirectReturn above) could plausibly rewrite location.search by the time onSignedIn resolves.
  const quickAddRequested = params.get('quickadd') === '1';
  // Cleared up front so a later page refresh doesn't reopen the popup every time - but only in the
  // direct-launch case, since auth.js's own handleRedirectReturn() below still needs to read
  // ?code=/?error= off window.location.search itself when isRedirectReturn is true (and already
  // clears the whole query string once it's done, taking quickadd along with it).
  if (quickAddRequested && !isRedirectReturn) history.replaceState(null, '', location.pathname);

  if (isRedirectReturn) {
    signinBtn.disabled = true;
    signinBtn.textContent = 'Completing sign-in…';
  } else {
    signinBtn.disabled = false;
    signinBtn.textContent = 'Sign in with Google';
  }

  const redirectResult = await auth.handleRedirectReturn();
  if (redirectResult.status === 'success') {
    await onSignedIn();
    if (quickAddRequested) openQuickAddFromShortcut();
    armHistoryTrap();
    return;
  }
  if (redirectResult.status === 'error') {
    signinBtn.disabled = false;
    signinBtn.textContent = 'Sign in with Google';
    signinStatus.textContent = redirectResult.message;
  }
  if (await auth.restoreFromCache()) {
    await onSignedIn();
    if (quickAddRequested) openQuickAddFromShortcut();
  }
}

// Anchored to whichever "New Task" button the current layout actually shows - which one that is
// depends on viewport width (see the min-width:1024px/767px rules on .new-task-btn in styles.css) -
// rather than a fixed button reference. sidebarNewTaskBtn is visible on both a fresh desktop load
// and a fresh mobile load (appEl.dataset.view starts as 'sidebar', set at module scope below), so
// it's checked first; newTaskBtn (list-pane/mobile FAB) is the fallback for the rarer case of
// landing back on a non-default view (e.g. a restored mobile "list" view - see armHistoryTrap).
function openQuickAddFromShortcut() {
  if (!onboardingModal.classList.contains('hidden')) return; // don't stack popups on a first-run user
  const anchor = sidebarNewTaskBtn.getBoundingClientRect().width > 0 ? sidebarNewTaskBtn : newTaskBtn;
  openQuickAddPopup(anchor);
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
  // Tagged as a 'sidebar' entry (not just { tasky: true }) so it reads as a normal frame in the
  // app's own view stack instead of a hole in it. Untagged, the very next back press out of
  // 'list' landed on this anchor: the view-stack listener below ignored it (no taskyView, so the
  // screen silently froze on 'list'), while this listener saw an untagged frame and replanted a
  // fresh anchor on top - permanently eating that back press and every one after it (reported
  // live as "the back button acts strange"). Tagging it 'sidebar' lets the view-stack listener
  // handle it like any other back-to-sidebar transition, and this listener's own check below then
  // leaves it alone, only re-arming for a popstate that runs past this frame entirely.
  const plantAnchor = () => history.pushState({ taskyView: 'sidebar', tasky: true }, '', location.pathname + location.search);
  plantAnchor();
  window.addEventListener('popstate', (e) => {
    // Back navigation within the app's own tracked view stack (sidebar/list/editor - see
    // showMobileView below) is handled by its own popstate listener and should be left alone;
    // only replant the anchor when back would otherwise escape past that stack toward the stale
    // Google page this trap exists to block.
    if (e.state && e.state.taskyView) return;
    plantAnchor();
  });
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
// Settings/About drill in from menu-dropdown by hiding it and showing themselves (see their own
// click handlers below) - so by the time the hamburger button is clicked again, menu-dropdown is
// often already hidden even though "the hamburger menu" is conceptually still open. A plain toggle
// on menu-dropdown alone would then reopen the top-level list instead of closing everything,
// forcing a second click (reported live: "have to double click hamburger icon to close"). Treat
// the whole family as one open/closed state instead.
menuBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  const familyOpen = [menuDropdown, settingsDropdown, aboutDropdown].some((d) => !d.classList.contains('hidden'));
  closeDropdowns({});
  if (!familyOpen) menuDropdown.classList.remove('hidden');
});
document.addEventListener('click', (e) => {
  closeDropdowns({});
  // Sort/Filter collapsed behind one icon at every width - see filterToggleBtn below. Guarded by
  // containment, unlike closeDropdowns, because a plain "close on every click" would hide it out
  // from under the very click that just opened it (or a click on one of its own chip buttons).
  if (!listFilterRow.contains(e.target)) listFilterRow.classList.add('hidden');
  // Same containment guard as listFilterRow above, for the same reason: the More button's own
  // click handler (see renderMobileTabbar) stops propagation so opening it doesn't immediately
  // trigger this same listener and close it again.
  if (!moreSheetPopup.contains(e.target)) moreSheetPopup.classList.add('hidden');
  // Same containment guard again for the quick-add popup (see attachQuickAddTrigger below) - its
  // own trigger buttons stop propagation on the click that opens it via long-press, for the same
  // reason tagSuggestPopup's exclusion below exists, so this only ever fires for a genuine
  // outside click.
  if (!quickAddPopup.contains(e.target)) quickAddPopup.classList.add('hidden');
  // Same containment guard, plus excluding the input itself - focus fires (and re-renders/shows
  // the popup) before this click listener sees the same click, so without the exclusion tapping
  // into the input would immediately hide the popup it had just opened. Uses composedPath()
  // rather than tagSuggestPopup.contains(e.target): clicking a suggestion (or "+ Create") rebuilds
  // the popup's contents synchronously in its own click handler, which detaches the very button
  // that was just clicked - by the time this listener runs, contains(e.target) sees a removed
  // node with no live ancestor and (wrongly) reads that as an outside click, undoing the refresh
  // that handler just did (reported live: the tag box looked empty/stale right after adding a tag,
  // fixed only by clicking away and back for a focus event that hadn't been clobbered yet).
  // composedPath() is captured at dispatch time, before any handler mutates the DOM, so it still
  // reflects where the click actually happened regardless of what's since been removed.
  const clickPath = e.composedPath();
  if (!clickPath.includes(tagSuggestPopup) && !clickPath.includes(editorTagInput)) closeTagSuggest();
  // Same composedPath() reasoning as tagSuggestPopup above - clicking a suggestion rebuilds
  // bulkTagSuggestions' contents synchronously, detaching the clicked button before this listener runs.
  if (!clickPath.includes(bulkTagPopup)) closeBulkTagPopup();
});
filterToggleBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  if (listFilterRow.classList.contains('hidden')) {
    // openAnchoredPopup (defined below) computes left/top from the button's own rect - needed since
    // #filter-toggle-btn sits inside the middle list-pane column on tablet/desktop, not pinned near
    // the viewport's own right edge the way it is on a full-width mobile layout.
    openAnchoredPopup(listFilterRow, filterToggleBtn);
  } else {
    listFilterRow.classList.add('hidden');
  }
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    closeDropdowns({});
    closeShortcuts();
    closeMoreSheet();
    if (!onboardingModal.classList.contains('hidden')) closeOnboarding();
  }
});
function closeDropdowns({ except }) {
  for (const d of [menuDropdown, accountDropdown, settingsDropdown, aboutDropdown, editorMoreDropdown]) {
    if (d !== except) d.classList.add('hidden');
  }
}

// Settings/About are opened from two different places depending on viewport - the header hamburger
// on desktop/tablet, the bottom tab bar's More popup on mobile (see styles.css - .menu-wrap is
// hidden there) - so unlike menu-dropdown/account-dropdown they can't just be CSS-anchored under one
// fixed parent. Positioned instead from whichever trigger button was actually clicked, same
// position:fixed idiom as #list-filter-row/#more-sheet-popup: flipped to whichever side of the
// button has room, independently on each axis (below/above, left-aligned/right-aligned), so a
// button hard against an edge - #filter-toggle-btn sits flush against the right edge of the list
// toolbar at every width - never pushes the popup itself off-screen.
function openAnchoredPopup(popupEl, anchorBtn) {
  // Captured before closing anything - when anchorBtn lives inside more-sheet-popup, closeMoreSheet
  // would hide it first and getBoundingClientRect() would come back all zeros.
  const rect = anchorBtn.getBoundingClientRect();
  closeDropdowns({});
  closeMoreSheet();
  popupEl.style.left = popupEl.style.right = popupEl.style.top = popupEl.style.bottom = '';
  if (rect.left > window.innerWidth / 2) {
    popupEl.style.right = `${window.innerWidth - rect.right}px`;
  } else {
    popupEl.style.left = `${rect.left}px`;
  }
  if (rect.top > window.innerHeight / 2) {
    popupEl.style.bottom = `${window.innerHeight - rect.top + 6}px`;
  } else {
    popupEl.style.top = `${rect.bottom + 6}px`;
  }
  popupEl.classList.remove('hidden');
}

// Clicking/toggling anything inside Settings (theme, text size, the checkbox toggle) used to bubble
// up to the document-level click listener and close the whole dropdown after every single
// interaction (reported live: "when I click or modify anything in settings, it closes") - stop it
// at the source so only an actual outside click or Escape closes it, same as every other dropdown.
settingsDropdown.addEventListener('click', (e) => e.stopPropagation());

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

// Web/mobile-only preference (desktop has no swipe-to-done gesture, so its row checkbox is never
// redundant there) - lets anyone who's learned the swipe-to-done gesture reclaim the row space,
// while defaulting to on so the checkbox stays the discoverable/accessible path for everyone else.
const SHOW_DONE_CHECKBOX_KEY = 'tasky-show-done-checkbox';
function applyShowDoneCheckbox(show) {
  document.documentElement.classList.toggle('hide-done-checkbox', !show);
  localStorage.setItem(SHOW_DONE_CHECKBOX_KEY, String(show));
  showDoneCheckboxToggle.checked = show;
}
showDoneCheckboxToggle.addEventListener('change', () => applyShowDoneCheckbox(showDoneCheckboxToggle.checked));
applyShowDoneCheckbox(localStorage.getItem(SHOW_DONE_CHECKBOX_KEY) !== 'false');

// ROADMAP.md #135: opt-in (default off, matching desktop's AutoEmptyTrashEnabled), per-device -
// localStorage rather than appState, same as desktop's Settings.json living outside the synced
// .tasky file. Uses ModifiedAt as a "trashed at" proxy rather than a dedicated timestamp field:
// a closed task is read-only (see the editor's IsClosed gating) so nothing else bumps ModifiedAt
// after it lands in Trash - same tradeoff desktop's MainViewModel.AutoEmptyTrashIfNeeded makes,
// and both must agree or a task synced between them would get pruned at different times.
const AUTO_EMPTY_TRASH_ENABLED_KEY = 'tasky-auto-empty-trash-enabled';
const AUTO_EMPTY_TRASH_DAYS_KEY = 'tasky-auto-empty-trash-days';
const DEFAULT_AUTO_EMPTY_TRASH_DAYS = 30;

function applyAutoEmptyTrashSetting(enabled) {
  localStorage.setItem(AUTO_EMPTY_TRASH_ENABLED_KEY, String(enabled));
  autoEmptyTrashToggle.checked = enabled;
  autoEmptyTrashDaysRow.classList.toggle('settings-row-disabled', !enabled);
  autoEmptyTrashDaysSelect.disabled = !enabled;
}
autoEmptyTrashToggle.addEventListener('change', () => {
  applyAutoEmptyTrashSetting(autoEmptyTrashToggle.checked);
  if (autoEmptyTrashToggle.checked) autoEmptyTrashIfNeeded();
});
applyAutoEmptyTrashSetting(localStorage.getItem(AUTO_EMPTY_TRASH_ENABLED_KEY) === 'true');

autoEmptyTrashDaysSelect.value = String(
  Number(localStorage.getItem(AUTO_EMPTY_TRASH_DAYS_KEY)) || DEFAULT_AUTO_EMPTY_TRASH_DAYS);
autoEmptyTrashDaysSelect.addEventListener('change', () => {
  localStorage.setItem(AUTO_EMPTY_TRASH_DAYS_KEY, autoEmptyTrashDaysSelect.value);
  autoEmptyTrashIfNeeded();
});

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

// Reconnecting is a full-page redirect to Google and back - AppState only ever lives in memory
// (see the "online-only" decision), so anything still unsaved at that exact moment is lost with
// no way to recover it afterward. Warn before navigating away if there's actually something to lose.
async function confirmSignInIfDirty() {
  if (dirty) {
    const confirmed = await confirmModal('You have unsaved changes that will be lost when you reconnect. Continue anyway?',
      { title: 'Unsaved Changes', confirmLabel: 'Continue', danger: true });
    if (!confirmed) return false;
  }
  auth.signIn();
  return true;
}

// The header avatar and the save-status line (see performSave/loadFromDriveWithRetry below) are
// both easy to miss - a small icon and a subtle status-bar line, neither of which interrupts
// whatever pane happens to be open (reported live: "sometime I may miss the icon saying I'm
// disconnected"). This is the same weight as the "Drive access not granted" notice, but as an
// actual modal so a genuine sign-out surfaces no matter what's on screen. Shown at most once per
// disconnected episode - showSignedOutModal() itself is called from every failed save/load along
// the way, and autosave retries every couple seconds once dirty again, so without this guard it
// would reopen on top of itself repeatedly instead of just reminding once. Cleared in onSignedIn()
// so a later, separate disconnect still gets its own reminder.
let signedOutModalShown = false;
function showSignedOutModal() {
  if (signedOutModalShown) return;
  signedOutModalShown = true;
  const overlay = document.createElement('div');
  overlay.className = 'confirm-overlay';
  const card = document.createElement('div');
  card.className = 'confirm-card';
  const heading = document.createElement('h2');
  heading.textContent = 'Signed Out';
  const body = document.createElement('p');
  body.textContent = "You've been signed out of Google - nothing you do now will sync to Drive until you reconnect.";
  const actions = document.createElement('div');
  actions.className = 'link-modal-actions';
  const laterBtn = document.createElement('button');
  laterBtn.type = 'button';
  laterBtn.className = 'btn btn-ghost';
  laterBtn.textContent = 'Dismiss';
  const signInBtn = document.createElement('button');
  signInBtn.type = 'button';
  signInBtn.className = 'btn btn-primary';
  signInBtn.textContent = 'Sign In';
  actions.append(laterBtn, signInBtn);
  card.append(heading, body, actions);
  overlay.appendChild(card);
  document.body.appendChild(overlay);
  signInBtn.focus();
  const close = () => {
    overlay.remove();
    document.removeEventListener('keydown', onKeydown);
  };
  function onKeydown(e) {
    if (e.key === 'Escape') close();
  }
  laterBtn.addEventListener('click', close);
  signInBtn.addEventListener('click', () => {
    close();
    confirmSignInIfDirty();
  });
  overlay.addEventListener('click', (e) => {
    if (e.target === overlay) close();
  });
  document.addEventListener('keydown', onKeydown);
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
  signedOutModalShown = false;

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
      currentFileName = match.name;
      drive.setSyncContext(taskyFolderId, match.name);
      const text = await drive.downloadFileText(match.id);
      appState = JSON.parse(text);
      appState.Tasks ??= [];
      appState.DeletedTasks = deduplicateTombstones(appState.DeletedTasks ?? []);
      appState.SavedViews ??= [];
      appState.DeletedSavedViewIds ??= [];
      autoEmptyTrashIfNeeded();
      setStatus(`Loaded ${appState.Tasks.length} task(s)`, { autoHide: true });
    } else {
      currentFileName = DEFAULT_DATA_FILE_NAME;
      drive.setSyncContext(taskyFolderId, DEFAULT_DATA_FILE_NAME);
      setStatus('');
      noRemoteFileYet = true;
      maybeShowOnboarding();
    }

    migrateLegacyLocalViewsIfNeeded();

    loadError = null;
    loadErrorIsAuthFailure = false;
    loadErrorNeedsDriveConsent = false;
    renderSidebar();
    renderList();
  } catch (err) {
    console.error(err);
    // Leaving the list pane blank here would look identical to "nothing to show," with only the
    // easy-to-miss header status line explaining why - render an explicit error + retry instead,
    // reusing the same empty-state slot renderList() already owns.
    loadErrorIsAuthFailure = err.message === 'NOT_SIGNED_IN';
    loadErrorNeedsDriveConsent = err.message === 'DRIVE_SCOPE_MISSING';
    if (loadErrorIsAuthFailure) {
      setStatus('Signed out — click to reconnect');
      saveStatus.classList.add('save-status-action');
      loadError = 'signed out';
      showSignedOutModal();
    } else if (loadErrorNeedsDriveConsent) {
      // Distinct from "signed out" - the sign-in itself succeeded, but Google's consent screen
      // shows Drive access as an opt-in checkbox separate from "Continue", unchecked by default,
      // so it's easy to click through without granting it. Same fix (sign in again) but needs its
      // own explanation, or "click to reconnect" reads like nothing was wrong with the account.
      setStatus('Drive access not granted — click to fix');
      saveStatus.classList.add('save-status-action');
      loadError = 'Google Drive access wasn’t granted';
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
    case 'today': {
      const today = new Date();
      return appState.Tasks.filter((t) => {
        if (t.IsClosed || t.IsDone) return false;
        if (t.IsPinned) return true;
        if (!t.DueDate) return false;
        const due = parseDotNetDate(t.DueDate);
        return isSameDate(due, today) || isTaskOverdue(t);
      });
    }
    case 'all':
      return appState.Tasks.filter((t) => !t.IsClosed && !t.IsDone);
    case 'recurring':
      return appState.Tasks.filter((t) => !t.IsClosed && !t.IsDone && t.Recurrence !== RecurrenceRule.None);
    case 'done':
      return appState.Tasks.filter((t) => !t.IsClosed && t.IsDone);
    case 'trash':
      return appState.Tasks.filter((t) => t.IsClosed);
    case 'tag':
      return appState.Tasks.filter((t) => !t.IsClosed && t.Tags.some((tg) => tg.toLowerCase() === section.tag.toLowerCase()));
    case 'view': {
      const view = appState.SavedViews.find((v) => v.Id === section.viewId);
      return view ? applySearch(tasksForSection({ kind: 'all' }), view.Query) : [];
    }
    default:
      return [];
  }
}

function isSameDate(a, b) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

function isTaskOverdue(task) {
  if (!task.DueDate || task.IsDone) return false;
  const due = parseDotNetDate(task.DueDate);
  const today = new Date();
  return due < today && !isSameDate(due, today);
}

function applyQuickFilter(tasks) {
  if (!quickFilter) return tasks;
  const today = new Date();
  return tasks.filter((t) => {
    const due = t.DueDate ? parseDotNetDate(t.DueDate) : null;
    switch (quickFilter) {
      case 'overdue':
        return isTaskOverdue(t);
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

// Operator tokens (tag:x, is:overdue, has:link, due:today) are pulled out of the query first and
// applied as their own filters; whatever text is left over (if any) still does the original plain
// substring search across title/tags/body. This lets operators combine with each other AND with
// free text (e.g. "is:overdue groceries"), and gives typed search the same filtering power as the
// quick-filter chips (applyQuickFilter above) for anyone who'd rather type than click.
const SEARCH_OPERATOR_RE = /\b(tag|is|has|due):(\S+)/gi;

// queryOverride lets a saved view (ROADMAP.md #82) evaluate its own stored query without
// mutating the live search box's searchQuery state - the normal search-box path just omits it.
function applySearch(tasks, queryOverride) {
  const raw = (queryOverride ?? searchQuery).trim();
  if (!raw) return tasks;

  const operators = [];
  const freeText = raw.replace(SEARCH_OPERATOR_RE, (_match, key, value) => {
    operators.push({ key: key.toLowerCase(), value: value.toLowerCase() });
    return '';
  }).trim().toLowerCase();

  let result = tasks;
  const today = new Date();
  for (const { key, value } of operators) {
    switch (key) {
      case 'tag':
        result = result.filter((t) => t.Tags.some((tg) => tg.toLowerCase().includes(value)));
        break;
      case 'is':
        if (value === 'overdue') result = result.filter((t) => isTaskOverdue(t));
        else if (value === 'pinned') result = result.filter((t) => t.IsPinned);
        else if (value === 'recurring') result = result.filter((t) => t.Recurrence !== RecurrenceRule.None);
        else if (value === 'done') result = result.filter((t) => t.IsDone);
        // No "High Priority" quick-filter chip here (desktop-only), but the operator itself is
        // shared vocabulary - mirrors TaskSearchMatcher.cs's "is:highpriority" - so a view saved on
        // desktop using it still matches correctly when opened here.
        else if (value === 'highpriority') result = result.filter((t) => t.Priority === TaskPriority.High);
        break;
      case 'has':
        if (value === 'link') result = result.filter((t) => t.Body.some((b) => b.Type === NoteBlockType.Link));
        else if (value === 'attachment') {
          result = result.filter((t) => t.Body.some((b) =>
            b.Type === NoteBlockType.Photo || b.Type === NoteBlockType.File || blockHasInlineImage(b) || blockHasInlineFile(b)));
        }
        break;
      case 'due':
        if (value === 'today') result = result.filter((t) => t.DueDate && isSameDate(parseDotNetDate(t.DueDate), today));
        else if (value === 'week') {
          result = result.filter((t) => {
            if (!t.DueDate) return false;
            const d = parseDotNetDate(t.DueDate);
            const weekOut = new Date(today);
            weekOut.setDate(weekOut.getDate() + 7);
            return d >= startOfDay(today) && d <= endOfDay(weekOut);
          });
        } else if (value === 'none') result = result.filter((t) => !t.DueDate);
        break;
    }
  }

  if (!freeText) return result;
  // ROADMAP.md #122: previously only Text-block content was searchable, not checklist items or
  // attachment/link metadata - mirrors TaskSearchMatcher.cs's BlockMatches on the desktop side.
  return result.filter((t) => {
    if (t.Text.toLowerCase().includes(freeText)) return true;
    if (t.Tags.some((tg) => tg.toLowerCase().includes(freeText))) return true;
    return t.Body.some((b) => blockMatchesSearch(b, freeText));
  });
}

function blockMatchesSearch(block, freeText) {
  const has = (s) => (s || '').toLowerCase().includes(freeText);
  if (has(block.Text) || has(block.FileName) || has(block.LinkLabel) || has(block.Url)) return true;
  return (block.ChecklistItems || []).some((item) => has(item.Text));
}

function startOfDay(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}
function endOfDay(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate(), 23, 59, 59, 999);
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
    for (const tag of t.Tags) s.add(tag.toLowerCase());
  }
  return [...s].sort();
}

function currentTasks() {
  return sortTasks(applySearch(applyQuickFilter(tasksForSection(currentSection))));
}

// --- Saved smart filters (ROADMAP.md #82, synced via Drive as of #148) ------
// A saved view is just a named, persisted search-box query string - it reuses the search
// operators (tag:, is:, has:, due:) and free text applySearch() already supports, rather than
// being a second filtering system. Lives in appState.SavedViews/DeletedSavedViewIds so it round-
// trips through Drive the same way Tasks/DeletedTasks do (see mergeSavedViews in sync.js), matching
// Desktop's C# SavedView shape (Id/Label/Query, PascalCase) instead of the original camelCase
// {id,label,query} this used to be when it was a device-local-only tasky-saved-views localStorage
// key - see migrateLegacyLocalViewsIfNeeded for the one-time upgrade path for existing users.
const LEGACY_SAVED_VIEWS_KEY = 'tasky-saved-views';
function migrateLegacyLocalViewsIfNeeded() {
  if (appState.SavedViews.length > 0) return; // already has synced views - never overwrite them
  let legacy;
  try {
    legacy = JSON.parse(localStorage.getItem(LEGACY_SAVED_VIEWS_KEY) ?? '[]');
  } catch {
    legacy = [];
  }
  if (!Array.isArray(legacy) || legacy.length === 0) return;

  appState.SavedViews = legacy.map((v) => ({ Id: v.id, Label: v.label, Query: v.query }));
  localStorage.removeItem(LEGACY_SAVED_VIEWS_KEY);
  markDirty();
}

function promptForViewName() {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';

    const card = document.createElement('div');
    card.className = 'modal-card link-modal-card';

    const heading = document.createElement('h2');
    heading.textContent = 'Save View';

    const nameLabel = document.createElement('label');
    nameLabel.className = 'link-modal-field';
    nameLabel.textContent = 'Name';
    const nameInput = document.createElement('input');
    nameInput.type = 'text';
    nameInput.placeholder = 'e.g. Urgent';
    nameLabel.appendChild(nameInput);

    const errorMsg = document.createElement('p');
    errorMsg.className = 'link-modal-error hidden';
    errorMsg.textContent = 'Enter a name for this view.';

    const actions = document.createElement('div');
    actions.className = 'link-modal-actions';
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-ghost';
    cancelBtn.textContent = 'Cancel';
    const saveBtn = document.createElement('button');
    saveBtn.type = 'button';
    saveBtn.className = 'btn btn-primary';
    saveBtn.textContent = 'Save';
    actions.append(cancelBtn, saveBtn);

    card.append(heading, nameLabel, errorMsg, actions);
    overlay.appendChild(card);
    document.body.appendChild(overlay);
    nameInput.focus();

    function close(result) {
      document.removeEventListener('keydown', onKeydown);
      overlay.remove();
      resolve(result);
    }
    function submit() {
      const name = nameInput.value.trim();
      if (!name) {
        errorMsg.classList.remove('hidden');
        nameInput.focus();
        return;
      }
      close(name);
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
    saveBtn.addEventListener('click', submit);
  });
}

async function saveCurrentSearchAsView() {
  const query = searchQuery.trim();
  if (!query) return;
  const label = await promptForViewName();
  if (!label) return;
  appState.SavedViews.push({ Id: crypto.randomUUID(), Label: label, Query: query });
  markDirty();
  renderSidebar();
}

function deleteSavedView(id) {
  appState.SavedViews = appState.SavedViews.filter((v) => v.Id !== id);
  appState.DeletedSavedViewIds.push(id);
  markDirty();
  if (currentSection.kind === 'view' && currentSection.viewId === id) {
    selectSection({ kind: 'all' });
  } else {
    renderSidebar();
  }
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
  setSyncProgress(5);
  try {
    const conflicted = await saveToDrive();
    setSyncProgress(100);
    setStatus(
      conflicted > 0
        ? `Synced - ${conflicted} edit${conflicted === 1 ? '' : 's'} conflicted with a remote change and ` +
          `${conflicted === 1 ? 'was' : 'were'} kept as "(conflicted copy)".`
        : 'Saved',
      { autoHide: conflicted === 0 });
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
      showSignedOutModal();
    } else if (err.message === 'DRIVE_SCOPE_MISSING') {
      setStatus('Drive access not granted — click to fix');
      saveStatus.classList.add('save-status-action');
    } else {
      setStatus(friendlyErrorMessage(`${statusVerb} failed`, err));
      console.error(err);
    }
  } finally {
    saving = false;
    // Hides the bar shortly after it lands at 100% (or wherever it got to on failure) instead of
    // snapping away the instant the promise settles - matches desktop, where IsSyncing=false hides
    // it immediately but the status text lingers; here the bar itself gets the same brief hold so
    // a fast sync doesn't just flash and vanish before it's even legible.
    setTimeout(() => setSyncProgress(null), 400);
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

// Desktop-browser backstop for the same problem visibilitychange covers on mobile: closing or
// refreshing the tab does fire visibilitychange first, but triggerSave() is fire-and-forget from
// that handler, and the browser is free to cancel the in-flight Drive upload once the page
// actually unloads a moment later - there's no guarantee the save wins the race. Asking first
// (native browser prompt, message text is ignored by modern browsers) gives the in-flight save a
// chance to land instead of silently losing whatever hasn't synced yet.
window.addEventListener('beforeunload', (e) => {
  if (!dirty) return;
  e.preventDefault();
  e.returnValue = '';
});

saveStatus.addEventListener('click', () => {
  if (!saveStatus.classList.contains('save-status-action')) return;
  // Nothing left to retry here after the redirect either way - the resumed session picks back up
  // normally via boot()'s handleRedirectReturn(). confirmSignInIfDirty() is what stops this from
  // silently discarding an in-flight edit (see its own comment).
  confirmSignInIfDirty();
});

// Downloads + merges the remote file into appState. Returns the remote file's modifiedTime at the
// moment of that download (for saveToDrive's concurrent-write check below) and how many of this
// device's edits conflicted with a remote change (see mergeRemoteState/ROADMAP.md #119), or
// { modifiedTime: null, conflicted: 0 } if the remote was unreadable (empty/corrupt/interrupted
// upload - falls through so the caller uploads local state as-is, same fallback the desktop app
// uses).
async function mergeFromRemote() {
  try {
    setSyncProgress(15);
    const [text, meta] = await Promise.all([
      drive.downloadFileText(currentFileId),
      drive.getFileMetadata(currentFileId),
    ]);
    setSyncProgress(45);
    const remoteState = JSON.parse(text);
    remoteState.Tasks ??= [];
    remoteState.DeletedTasks = deduplicateTombstones(remoteState.DeletedTasks ?? []);
    remoteState.SavedViews ??= [];
    remoteState.DeletedSavedViewIds ??= [];
    const storedLastSync = localStorage.getItem(LAST_SYNCED_KEY);
    const { conflicted } = mergeRemoteState(appState, remoteState, storedLastSync ? new Date(storedLastSync) : null);
    mergeSavedViews(appState, remoteState);
    autoEmptyTrashIfNeeded();
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
    return { modifiedTime: meta?.modifiedTime ?? null, conflicted };
  } catch (err) {
    console.warn('Could not read remote file for merge, uploading local state as-is.', err);
    return { modifiedTime: null, conflicted: 0 };
  }
}

// Returns how many of this device's edits conflicted with a remote change during the merge(s)
// below (see mergeFromRemote), so performSave can tell the user rather than silently uploading
// over a conflict resolution nobody saw happen.
async function saveToDrive() {
  // currentFileId is only null because loadFromDriveWithRetry's search came up empty back when
  // the page loaded - an arbitrary amount of time (waiting for a first edit, the autosave
  // debounce) can pass between that check and this, the first save. If another device's first
  // sync landed in that gap, blindly uploading as "create new" below would produce a second
  // Tasky.tasky next to the one that already exists instead of ever finding it. Re-check right
  // before deciding to create, same as desktop's SyncCoordinator does on every sync until it has
  // a cached file ID (see FindExistingFileIdAsync there) - this is the one-time equivalent for
  // the web app's single "resolve once at load" flow.
  if (!currentFileId) {
    const existingId = await drive.findFileByName(currentFileName, taskyFolderId);
    if (existingId) {
      currentFileId = existingId;
      drive.setSyncContext(taskyFolderId, currentFileName);
    }
  }

  let { modifiedTime: remoteModifiedAtDownload, conflicted } = currentFileId
    ? await mergeFromRemote()
    : { modifiedTime: null, conflicted: 0 };

  // Cheap mitigation for the concurrent-save race: there's no revision check between this
  // download+merge and the upload below, so a desktop client auto-syncing mid-edit can write to
  // Drive in that gap and get silently clobbered by this upload. Re-check modifiedTime right
  // before uploading and redo the merge against the newer copy if it moved, instead of blindly
  // overwriting whatever landed there in the meantime. Not airtight (the same gap still exists
  // between this recheck and the upload call), but it closes the window from "however long a
  // save takes" down to one more round-trip.
  if (currentFileId && remoteModifiedAtDownload) {
    const latest = await drive.getFileMetadata(currentFileId).catch(() => null);
    if (latest && latest.modifiedTime !== remoteModifiedAtDownload) {
      const remerge = await mergeFromRemote();
      remoteModifiedAtDownload = remerge.modifiedTime;
      conflicted += remerge.conflicted;
    }
  }

  setSyncProgress(75);
  const json = JSON.stringify(appState, null, 2);
  const newId = await drive.uploadFileText(currentFileId, currentFileName, taskyFolderId, json);
  currentFileId = newId;
  noRemoteFileYet = false;
  return conflicted;
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

// Rapid capture from the list pane's quick-add row - unlike createTask(), this never opens the
// full editor or switches mobile views, so the input stays focused and ready for the next task.
function createQuickTask(raw) {
  const parsed = parseQuickAdd(raw);
  const task = newTaskItem({ text: parsed.text || raw.trim() });
  task.DueDate = parsed.dueDate;
  for (const tag of parsed.tags) {
    const lower = tag.toLowerCase(); // matches addTag()'s own normalization - tags are always lowercase
    if (!task.Tags.includes(lower)) task.Tags.push(lower);
  }
  appState.Tasks.push(task);
  if (currentSection.kind !== 'all') currentSection = { kind: 'all' };
  markDirty();
  renderSidebar();
  renderList();
}

// Onboarding's sample tasks (see onboardingDoneBtn below) - a sibling of createQuickTask() rather
// than a reuse of it, since createQuickTask always runs `raw` through parseQuickAdd, stripping any
// #tag/!due/@time tokens out of the *displayed* title once they're consumed. That's the right
// behavior for a real quick-added task, but wrong for a task whose whole job is to teach that
// syntax by example - reported live: a sample task titled "Try Quick Add syntax right here
// !due:today @9am" ended up on screen as just "Try Quick Add syntax right here" (due date attached
// but invisible) with no way to tell what produced it. Here, `title` is shown verbatim - tokens
// live in the separate `quickAddTokens` string, parsed only to derive DueDate/Tags, never touching
// what's actually displayed.
function createDemoTask(title, quickAddTokens) {
  const task = newTaskItem({ text: title });
  if (quickAddTokens) {
    const parsed = parseQuickAdd(quickAddTokens);
    task.DueDate = parsed.dueDate;
    for (const tag of parsed.tags) {
      const lower = tag.toLowerCase();
      if (!task.Tags.includes(lower)) task.Tags.push(lower);
    }
  }
  appState.Tasks.push(task);
  if (currentSection.kind !== 'all') currentSection = { kind: 'all' };
  markDirty();
  renderSidebar();
  renderList();
}

// ROADMAP.md #66: navigator.vibrate is Android-only in practice (iOS Safari has never
// implemented the Vibration API, and silently ignores the call rather than throwing) - the
// optional-chaining call below is a no-op everywhere else, so no feature-detection branch needed.
function haptic(pattern = 15) {
  navigator.vibrate?.(pattern);
}

function toggleDone(task) {
  const wasDone = task.IsDone;
  task.IsDone = !wasDone;
  if (task.IsDone) haptic(); // only on completing, not un-completing - that's an "oops, undo", not a win
  touch(task);

  let spawned = null;
  if (task.IsDone && task.Recurrence !== RecurrenceRule.None) {
    spawned = spawnNextOccurrence(task);
    appState.Tasks.push(spawned);
  }
  markDirty();
  renderSidebar();
  renderList();

  const label = task.Text || '(untitled)';
  const description = task.IsDone
    ? spawned
      ? `Completed "${label}" — next occurrence created`
      : `Completed "${label}"`
    : `Marked "${label}" incomplete`;
  pushUndo(description, () => {
    task.IsDone = wasDone;
    touch(task);
    if (spawned) appState.Tasks = appState.Tasks.filter((t) => t.Id !== spawned.Id);
  });
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
  const wasClosed = task.IsClosed;
  task.IsClosed = !wasClosed;
  touch(task);
  markDirty();
  renderSidebar();
  renderList();
  renderEditor(task);

  // Desktop only makes moving TO Trash undoable (restoring is already the "undo" of trashing there)
  // - Web diverges on purpose here (reported live: "I do not get a restore toast with undo when I
  // restore from trash") since restoring is also reachable by an accidental swipe, same as trashing
  // is, and deserves the same safety net.
  const label = task.Text || '(untitled)';
  pushUndo(wasClosed ? `Restored "${label}" from Trash` : `Moved "${label}" to Trash`, () => {
    task.IsClosed = wasClosed;
    touch(task);
  });
}

// ROADMAP.md #141: replaces native confirm() with the app's own modal style - a browser confirm()
// can't be themed and looks jarringly out of place next to the rest of the UI. Uses the lighter
// .confirm-overlay/.confirm-card styling (not .modal-overlay/.modal-card, which About/Shortcuts/Add
// Link use) - reported live as feeling like "a big popup" next to the small toast completing a task
// gives, even though both already draw from the same theme tokens. Resolves to true (confirmed) or
// false (cancelled, Escape, or clicking outside). danger:true starts focus on Cancel rather than
// the confirm button (and styles it as .btn.danger) - safer default for a destructive action than
// having Enter/Space on an already-focused button do the irreversible thing.
function confirmModal(message, { title = 'Are you sure?', confirmLabel = 'Confirm', danger = false } = {}) {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'confirm-overlay';

    const card = document.createElement('div');
    card.className = 'confirm-card';

    const heading = document.createElement('h2');
    heading.textContent = title;

    const body = document.createElement('p');
    body.textContent = message;

    const actions = document.createElement('div');
    actions.className = 'link-modal-actions';
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-ghost';
    cancelBtn.textContent = 'Cancel';
    const confirmBtn = document.createElement('button');
    confirmBtn.type = 'button';
    confirmBtn.className = danger ? 'btn btn-ghost danger' : 'btn btn-primary';
    confirmBtn.textContent = confirmLabel;
    actions.append(cancelBtn, confirmBtn);

    card.append(heading, body, actions);
    overlay.appendChild(card);
    document.body.appendChild(overlay);
    (danger ? cancelBtn : confirmBtn).focus();

    function close(result) {
      document.removeEventListener('keydown', onKeydown);
      overlay.remove();
      resolve(result);
    }
    function onKeydown(e) {
      if (e.key === 'Escape') close(false);
    }
    document.addEventListener('keydown', onKeydown);
    overlay.addEventListener('click', (e) => {
      if (e.target === overlay) close(false);
    });
    cancelBtn.addEventListener('click', () => close(false));
    confirmBtn.addEventListener('click', () => close(true));
  });
}

function recordTombstone(taskId) {
  const existing = appState.DeletedTasks.find((r) => r.TaskId === taskId);
  if (existing) existing.Timestamp = nowDotNet();
  else appState.DeletedTasks.push(newTaskSyncRecord(taskId));
}

function autoEmptyTrashIfNeeded() {
  if (localStorage.getItem(AUTO_EMPTY_TRASH_ENABLED_KEY) !== 'true') return;
  const days = Number(localStorage.getItem(AUTO_EMPTY_TRASH_DAYS_KEY)) || DEFAULT_AUTO_EMPTY_TRASH_DAYS;
  const cutoffMs = Date.now() - days * 24 * 60 * 60 * 1000;
  const expired = appState.Tasks.filter((t) => t.IsClosed && parseDotNetDate(t.ModifiedAt).getTime() < cutoffMs);
  if (expired.length === 0) return;

  const expiredIds = new Set(expired.map((t) => t.Id));
  appState.Tasks = appState.Tasks.filter((t) => !expiredIds.has(t.Id));
  for (const id of expiredIds) recordTombstone(id);
  if (selectedTaskId && expiredIds.has(selectedTaskId)) {
    selectedTaskId = null;
    showEmptyEditor();
  }
  markDirty();
}

async function deleteForever(task) {
  const confirmed = await confirmModal(`Delete "${task.Text || '(untitled)'}" permanently? This cannot be undone.`,
    { title: 'Delete Permanently', confirmLabel: 'Delete', danger: true });
  if (!confirmed) return;
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

async function emptyTrash() {
  const trashed = appState.Tasks.filter((t) => t.IsClosed);
  if (trashed.length === 0) return;
  const confirmed = await confirmModal(`Permanently delete ${trashed.length} task(s) in Trash? This cannot be undone.`,
    { title: 'Empty Trash', confirmLabel: 'Delete All', danger: true });
  if (!confirmed) return;

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

async function moveAllDoneToTrash() {
  const done = appState.Tasks.filter((t) => !t.IsClosed && t.IsDone);
  if (done.length === 0) return;
  const confirmed = await confirmModal(`Move ${done.length} completed task(s) to Trash?`,
    { title: 'Move to Trash', confirmLabel: 'Move' });
  if (!confirmed) return;

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

function normalizeTagName(rawTag) {
  return rawTag.trim().replace(/^#+/, '').replace(/[^\w-]/g, '').toLowerCase();
}

function addTag(task, rawTag) {
  const tag = normalizeTagName(rawTag);
  if (!tag || task.Tags.some((t) => t.toLowerCase() === tag)) return;
  task.Tags.push(tag);
  touch(task);
  markDirty();
  renderSidebar();
  renderEditor(task);
}

function removeTag(task, tag) {
  task.Tags = task.Tags.filter((t) => t.toLowerCase() !== tag.toLowerCase());
  touch(task);
  markDirty();
  renderSidebar();
  renderEditor(task);
}

// Sidebar sections and tags render as <li role="button" tabindex="0"> rather than real <button>
// elements (keeping the existing flex/padding/hover CSS that already targets `li` directly), so
// Enter/Space activation has to be wired up by hand the way a native button gets it for free -
// otherwise these rows are mouse-only and invisible to keyboard/screen-reader users entirely.
function activateOnEnterOrSpace(activate) {
  return (e) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      activate();
    }
  };
}

// Touch-only swipe-to-reveal for a task-list row's .task-content layer (see the CSS comment above
// .task-list li in styles.css for the two-layer structure this drags across). Mouse/trackpad users
// never see anything different - these are touch events, which simply never fire without a
// touchscreen, so there's no separate desktop code path to keep in sync.
const SWIPE_COMMIT_THRESHOLD = 64;
const SWIPE_MAX_REVEAL = 92;
const SWIPE_DIRECTION_DEADZONE = 8; // px of ambiguous movement before committing to horizontal vs. vertical

function bindSwipeGesture(content, { onCommit }) {
  let startX = 0;
  let startY = 0;
  let dx = 0;
  let dragging = false;
  let decided = false; // has direction (horizontal vs. vertical scroll) been determined yet?
  let horizontal = false;

  content.addEventListener(
    'touchstart',
    (e) => {
      if (e.touches.length !== 1 || selectionMode) return;
      startX = e.touches[0].clientX;
      startY = e.touches[0].clientY;
      dx = 0;
      dragging = true;
      decided = false;
      horizontal = false;
      content.style.transition = 'none';
    },
    { passive: true }
  );

  content.addEventListener(
    'touchmove',
    (e) => {
      if (!dragging) return;
      const touch = e.touches[0];
      const deltaX = touch.clientX - startX;
      const deltaY = touch.clientY - startY;
      if (!decided) {
        if (Math.abs(deltaX) < SWIPE_DIRECTION_DEADZONE && Math.abs(deltaY) < SWIPE_DIRECTION_DEADZONE) return;
        decided = true;
        horizontal = Math.abs(deltaX) > Math.abs(deltaY);
        if (!horizontal) {
          // A vertical drag: this is a list scroll, not a swipe - let the browser handle it
          // natively instead of fighting it (touch-action: pan-y on .task-content already permits
          // this even mid-gesture).
          dragging = false;
          return;
        }
      }
      if (!horizontal) return;
      e.preventDefault();
      dx = Math.max(-SWIPE_MAX_REVEAL, Math.min(SWIPE_MAX_REVEAL, deltaX));
      content.style.transform = `translateX(${dx}px)`;
    },
    { passive: false }
  );

  function finish() {
    if (!dragging) return;
    dragging = false;
    content.style.transition = '';
    content.style.transform = '';
    if (!horizontal) return;
    if (dx >= SWIPE_COMMIT_THRESHOLD) {
      haptic();
      onCommit('right');
    } else if (dx <= -SWIPE_COMMIT_THRESHOLD) {
      haptic();
      onCommit('left');
    }
  }

  content.addEventListener('touchend', finish);
  content.addEventListener('touchcancel', () => {
    dragging = false;
    content.style.transition = '';
    content.style.transform = '';
  });
}

// --- Rendering --------------------------------------------------------------
// Shared by the sidebar list, the tag list, and the mobile tab bar - all three are just
// different views onto the same "which section is current" state. Any quick filter (Overdue,
// Due Today, etc.) is section-scoped in the user's mental model, not global - navigating away
// without clearing it left it silently still applied to whatever section you land on next. The
// dashboard cards below are the one exception: they set a filter and navigate to "All Tasks" in
// the same gesture, so they opt out via preserveFilter.
function selectSection(section, { preserveFilter = false } = {}) {
  currentSection = section;
  searchBox.value = '';
  searchQuery = '';
  saveViewBtn.disabled = true;
  // A bulk selection is scoped to whatever list it was made in - carrying it across to an entirely
  // different section's task set would be confusing (and Trash's swipe/checkbox semantics differ
  // from every other section - see updateTaskRow's sectionKind branches).
  selectionMode = false;
  selectedIds.clear();
  if (!preserveFilter) setQuickFilterChip('');
  renderSidebar();
  renderList();
  showMobileView('list');
}

// Counts every fixed section (Today/All/Recurring/Completed/Trash) in a single pass over
// appState.Tasks, mirroring tasksForSection()'s own per-kind predicates - calling
// tasksForSection() once per section like renderSidebar() used to do costs O(sections x tasks)
// on every render just to produce a badge count (#67).
function sectionCounts() {
  const today = new Date();
  const counts = { today: 0, all: 0, recurring: 0, done: 0, trash: 0 };
  for (const t of appState.Tasks) {
    if (t.IsClosed) {
      counts.trash++;
      continue;
    }
    if (t.IsDone) {
      counts.done++;
      continue;
    }
    counts.all++;
    if (t.Recurrence !== RecurrenceRule.None) counts.recurring++;
    if (t.IsPinned) counts.today++;
    else if (t.DueDate) {
      const due = parseDotNetDate(t.DueDate);
      if (isSameDate(due, today) || isTaskOverdue(t)) counts.today++;
    }
  }
  return counts;
}

function renderSidebar() {
  sidebarList.innerHTML = '';
  const counts = sectionCounts();
  for (const section of SECTIONS) {
    const li = document.createElement('li');
    const count = counts[section.kind];
    li.title = section.label;
    li.tabIndex = 0;
    li.setAttribute('role', 'button');
    li.setAttribute('aria-label', `${section.label} (${count})`);
    li.innerHTML = `${icon(SECTION_ICONS[section.kind])}<span class="sidebar-item-label">${section.label}</span><span class="count">${count}</span>`;
    if (currentSection.kind === section.kind) {
      li.classList.add('active');
      li.setAttribute('aria-current', 'true');
    }
    const activate = () => selectSection({ kind: section.kind });
    li.addEventListener('click', activate);
    li.addEventListener('keydown', activateOnEnterOrSpace(activate));
    sidebarList.appendChild(li);
  }

  tagList.innerHTML = '';
  for (const tag of allTags()) {
    const li = document.createElement('li');
    li.tabIndex = 0;
    li.setAttribute('role', 'button');
    li.setAttribute('aria-label', `Tag: ${tag}`);
    li.innerHTML = `<span>#${escapeHtml(tag)}</span>`;
    if (currentSection.kind === 'tag' && currentSection.tag === tag) {
      li.classList.add('active');
      li.setAttribute('aria-current', 'true');
    }
    const activate = () => selectSection({ kind: 'tag', tag });
    li.addEventListener('click', activate);
    li.addEventListener('keydown', activateOnEnterOrSpace(activate));
    tagList.appendChild(li);
  }

  savedViewsList.innerHTML = '';
  for (const view of appState.SavedViews) {
    const li = document.createElement('li');
    li.tabIndex = 0;
    li.setAttribute('role', 'button');
    li.setAttribute('aria-label', `View: ${view.Label}`);
    li.innerHTML = `<span>${escapeHtml(view.Label)}</span><button class="view-delete-btn" aria-label="Delete view">${icon('x')}</button>`;
    if (currentSection.kind === 'view' && currentSection.viewId === view.Id) {
      li.classList.add('active');
      li.setAttribute('aria-current', 'true');
    }
    const activate = () => selectSection({ kind: 'view', viewId: view.Id });
    li.addEventListener('click', activate);
    li.addEventListener('keydown', activateOnEnterOrSpace(activate));
    li.querySelector('.view-delete-btn').addEventListener('click', (e) => {
      e.stopPropagation();
      deleteSavedView(view.Id);
    });
    savedViewsList.appendChild(li);
  }

  renderMobileTabbar();
}

// Mobile-only bottom tab bar: the 4 original fixed sections get one tap instead of
// open-drawer-then-pick, with a "More" tab standing in for Tags (a variable-length list that
// can't be fixed tabs), Today (reachable via the sidebar drawer instead - a 5th real tab left
// each of these ~62px wide on a 375px phone, cramped enough to risk the label text wrapping), and
// for the sidebar screen generally. Stays visible on the editor view too, so navigation is always
// one tap away instead of back-then-tap.
function renderMobileTabbar() {
  mobileTabbar.innerHTML = '';
  const onSidebarView = appEl.dataset.view === 'sidebar';

  for (const section of SECTIONS.filter((s) => s.kind !== 'today')) {
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
  // Same inline-popup idiom as filterToggleBtn below - anchored near the button rather than a
  // full-screen modal - just anchored by its bottom edge instead of its top, since this button
  // lives in the fixed bottom tab bar rather than the header.
  moreBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    closeDropdowns({});
    listFilterRow.classList.add('hidden');
    const wasHidden = moreSheetPopup.classList.contains('hidden');
    if (wasHidden) {
      const rect = moreBtn.getBoundingClientRect();
      moreSheetPopup.style.right = `${window.innerWidth - rect.right}px`;
      moreSheetPopup.style.bottom = `${window.innerHeight - rect.top + 6}px`;
    }
    moreSheetPopup.classList.toggle('hidden', !wasHidden);
  });
  mobileTabbar.appendChild(moreBtn);
}

// Builds one task row's static DOM structure - called only the first time a given task appears in
// the list (see renderList's keyed diff). Every listener below closes over `task`, a stable object
// reference mutated in place elsewhere in this file rather than replaced, and reads
// currentSection/selectionMode/selectedIds fresh (module state, not captured) at event time - so
// a row that's reused across many renders never needs its listeners rebound, only its visible
// content refreshed by updateTaskRow.
function buildTaskRow(task) {
  const li = document.createElement('li');

  // Marking done doesn't mean much for an already-trashed task - swapped for a permanent delete in
  // the Trash section instead (danger-colored, since it's destructive), and the other side
  // recolored as a safe/positive action since restoring isn't. See updateTaskRow for the per-render
  // content/class refresh this only creates the shell for.
  const completeAction = document.createElement('div');
  completeAction.className = 'task-swipe-action complete';
  completeAction.setAttribute('aria-hidden', 'true');

  const trashAction = document.createElement('div');
  trashAction.className = 'task-swipe-action trash';
  trashAction.setAttribute('aria-hidden', 'true');

  const content = document.createElement('div');
  content.className = 'task-content';

  // Selection checkbox for bulk actions (#142) - always in the DOM, shown/hidden purely via the
  // .task-list.selecting CSS class (see styles.css) so entering/exiting selection mode is a class
  // flip rather than a re-render of every row.
  const selectCheckbox = document.createElement('input');
  selectCheckbox.type = 'checkbox';
  selectCheckbox.title = 'Select';
  selectCheckbox.setAttribute('aria-label', 'Select task');
  selectCheckbox.addEventListener('change', () => {
    if (selectCheckbox.checked) selectedIds.add(task.Id);
    else selectedIds.delete(task.Id);
    li.classList.toggle('row-selected', selectCheckbox.checked);
    updateBulkActionsBar();
  });
  const selectCheckboxWrap = document.createElement('label');
  selectCheckboxWrap.className = 'checkbox-tap-target select-checkbox-wrap';
  selectCheckboxWrap.addEventListener('click', (e) => e.stopPropagation());
  selectCheckboxWrap.appendChild(selectCheckbox);

  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  checkbox.title = 'Mark done';
  checkbox.setAttribute('aria-label', 'Mark done');
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
  checkboxWrap.className = 'checkbox-tap-target done-checkbox-wrap';
  checkboxWrap.addEventListener('click', (e) => e.stopPropagation());
  checkboxWrap.appendChild(checkbox);

  const info = document.createElement('div');
  info.className = 'task-row-info';

  content.append(selectCheckboxWrap, checkboxWrap, info);
  li.append(completeAction, trashAction, content);
  li.tabIndex = 0;
  li.setAttribute('role', 'button');

  const openTask = () => {
    selectedTaskId = task.Id;
    renderList();
    renderEditor(task);
    showMobileView('editor');
  };
  // A swipe that crossed bindSwipeGesture's own commit threshold moved the touch point 64px+ -
  // real browsers already suppress the synthetic click that follows a drag that large, but this
  // flag is a belt-and-suspenders guard against the rare engine that doesn't, so a completed
  // swipe never also opens the task underneath it.
  let suppressClick = false;
  li.addEventListener('click', () => {
    if (suppressClick) {
      suppressClick = false;
      return;
    }
    if (selectionMode) {
      selectCheckbox.checked = !selectCheckbox.checked;
      selectCheckbox.dispatchEvent(new Event('change'));
      return;
    }
    openTask();
  });
  li.addEventListener('keydown', activateOnEnterOrSpace(openTask));
  bindSwipeGesture(content, {
    onCommit: (direction) => {
      if (selectionMode) return;
      suppressClick = true;
      // Right always sends the task one step forward along its lifecycle (Active -> Done ->
      // Trash -> gone for good); left is the corresponding step back. Today/All/Recurring: right
      // completes it (the common, low-stakes gesture on the lists you live in day to day), left
      // trashes it directly. Completed: right trashes it (its forward step now that Done is
      // behind it), left un-completes it. Trash: right deletes forever, left restores.
      if (currentSection.kind === 'trash') {
        if (direction === 'right') deleteForever(task);
        else toggleTrash(task); // restores - IsClosed is already true here
      } else if (currentSection.kind === 'done') {
        if (direction === 'right') toggleTrash(task);
        else toggleDone(task); // un-completes
      } else if (direction === 'right') {
        toggleDone(task);
      } else {
        toggleTrash(task);
      }
    },
  });

  const refs = { li, completeAction, trashAction, checkbox, selectCheckbox, info };
  taskRowRefs.set(task.Id, refs);
  return refs;
}

// Refreshes a row's visible content/classes to match `task` - shared by both the first render of a
// row (right after buildTaskRow) and every later re-render that reuses it, so the two never drift.
function updateTaskRow(refs, task, sectionKind) {
  const { li, completeAction, trashAction, checkbox, selectCheckbox, info } = refs;
  li.classList.toggle('selected', task.Id === selectedTaskId);
  li.classList.toggle('pinned', task.IsPinned);
  li.classList.toggle('row-selected', selectedIds.has(task.Id));

  // Left slot = right-swipe, right slot = left-swipe - see the onCommit direction mapping above,
  // which this always matches. Today/All/Recurring keep the plain default colors (left=green/
  // Done, right=red/Trash) since right is the forward/positive step there; Completed and Trash
  // both use right-is-the-away-step instead, so they invert the defaults via .danger/.safe, same
  // trick, just different labels (Trash/Undo vs Delete/Restore).
  if (sectionKind === 'trash') {
    completeAction.className = 'task-swipe-action complete danger';
    completeAction.innerHTML = `${icon('trash')}<span>Delete</span>`;
    trashAction.className = 'task-swipe-action trash safe';
    trashAction.innerHTML = `${icon('trash')}<span>Restore</span>`;
  } else if (sectionKind === 'done') {
    completeAction.className = 'task-swipe-action complete danger';
    completeAction.innerHTML = `${icon('trash')}<span>Trash</span>`;
    trashAction.className = 'task-swipe-action trash safe';
    trashAction.innerHTML = `${icon('check')}<span>Undo</span>`;
  } else {
    completeAction.className = 'task-swipe-action complete';
    completeAction.innerHTML = `${icon('check')}<span>${task.IsDone ? 'Undo' : 'Done'}</span>`;
    trashAction.className = 'task-swipe-action trash';
    trashAction.innerHTML = `${icon('trash')}<span>Trash</span>`;
  }

  checkbox.checked = task.IsDone;
  selectCheckbox.checked = selectedIds.has(task.Id);

  const due = task.DueDate ? formatDate(parseDotNetDate(task.DueDate)) : '';
  const overdue = isTaskOverdue(task);
  const indicators = [];
  if (task.Recurrence !== RecurrenceRule.None) indicators.push(icon('repeat'));
  if (task.Body.some((b) => b.Type === NoteBlockType.Link)) indicators.push(icon('link'));
  if (task.Body.some((b) => b.Type === NoteBlockType.Photo || blockHasInlineImage(b))) indicators.push(icon('image'));
  if (task.Body.some((b) => b.Type === NoteBlockType.File || blockHasInlineFile(b))) indicators.push(icon('paperclip'));
  if (task.Body.some((b) => b.Type === NoteBlockType.Checklist)) indicators.push(icon('checklist'));
  const tagChips = (task.Tags || []).map((t) => `<span class="task-tag-chip">#${escapeHtml(t)}</span>`).join('');
  // Mirrors desktop's row-level priority Ellipse (MainWindow.xaml + PriorityColorConverter) -
  // hidden entirely at None, same as there.
  const priorityInfo = { [TaskPriority.Low]: ['priority-low', 'Low'], [TaskPriority.Medium]: ['priority-medium', 'Medium'], [TaskPriority.High]: ['priority-high', 'High'] }[task.Priority];
  const priorityDot = priorityInfo ? `<span class="task-priority-dot ${priorityInfo[0]}" title="${priorityInfo[1]} priority"></span>` : '';
  info.innerHTML = `
    <div class="task-title ${task.IsDone ? 'done' : ''}">${priorityDot}${task.IsPinned ? icon('pin', 'pin-inline') : ''}${escapeHtml(task.Text || '(untitled)')}</div>
    <div class="task-sub">${due ? `<span class="${overdue ? 'task-due-overdue' : ''}">${due}</span>` : ''}${indicators.length ? `<span class="task-indicators">${indicators.join('')}</span>` : ''}${tagChips ? `<span class="task-tags">${tagChips}</span>` : ''}</div>
  `;

  li.setAttribute('aria-label', `${task.Text || '(untitled)'}${due ? `, due ${due}` : ''}${overdue ? ' (overdue)' : ''}`);
}

function updateBulkActionsBar() {
  bulkActionsRow.classList.toggle('hidden', !selectionMode);
  selectToggleBtn.classList.toggle('active', selectionMode);
  // The icon itself flips to an X while selecting - reported live as hard to tell selection mode
  // was even on, since the accent-fill alone is a subtle change among several other header icons.
  // An icon swap reads at a glance, and doubles as "tap here to exit" (see styles.css for the
  // other reinforcing signals: a tinted bulk-actions bar and a faint tint across the whole list).
  selectToggleBtn.innerHTML = icon(selectionMode ? 'x' : 'checkSquare');
  const label = selectionMode ? 'Cancel selection' : 'Select multiple';
  selectToggleBtn.title = label;
  selectToggleBtn.setAttribute('aria-label', label);
  const count = selectedIds.size;
  bulkSelectedCount.textContent = `${count} selected`;
  bulkDoneBtn.disabled = count === 0;
  bulkPinBtn.disabled = count === 0;
  bulkDueBtn.disabled = count === 0;
  bulkTagBtn.disabled = count === 0;
  bulkTrashBtn.disabled = count === 0;
  bulkRestoreBtn.disabled = count === 0;
  bulkDeleteBtn.disabled = count === 0;
}

function renderList() {
  const tasks = currentTasks();
  // Creating a task always drops it into "All Tasks" (see createTask/createQuickTask) - offering
  // either input while looking at Completed or Trash would just be a confusing way to leave the
  // page you're on.
  const hideAdd = currentSection.kind === 'done' || currentSection.kind === 'trash';
  newTaskBtn.classList.toggle('hidden', hideAdd);
  quickAddRow.classList.toggle('hidden', hideAdd);
  trashActionsRow.classList.toggle('hidden', selectionMode || currentSection.kind !== 'trash' || tasksForSection({ kind: 'trash' }).length === 0);
  doneActionsRow.classList.toggle('hidden', selectionMode || currentSection.kind !== 'done' || tasksForSection({ kind: 'done' }).length === 0);
  taskListEl.classList.toggle('selecting', selectionMode);
  updateBulkActionsBar();
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
    } else if (loadErrorNeedsDriveConsent) {
      // Same recovery action as a reconnect (sign in again) - the difference is purely the
      // explanation, since the account itself is fine and re-showing "Reconnect" here would
      // suggest something's wrong with the sign-in when it's really just a missed checkbox.
      msg.textContent =
        "Tasky needs Google Drive access to sync your tasks, but that permission wasn't granted. " +
        "Google's sign-in screen shows Drive access as its own checkbox, separate from the main " +
        'button - click below and make sure to check it this time.';
      retryBtn.textContent = 'Grant Drive Access';
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

  // Keyed diff (#67): a row already on screen for a given task ID gets patched in place and moved
  // to its new position (appendChild on an existing child just moves it, it doesn't clone or
  // rebind anything) instead of torn down and rebuilt - previously every single render, including
  // every keystroke in the title field or search box, rebuilt every row's DOM and listeners from
  // scratch regardless of what actually changed. remainingIds starts as every row left over from
  // the last render and has each still-wanted task's ID removed below the loop; whatever's left in
  // it afterward is a row no longer in view and gets torn down.
  const remainingIds = new Set(taskRowRefs.keys());
  for (const task of tasks) {
    let refs = taskRowRefs.get(task.Id);
    if (!refs) refs = buildTaskRow(task);
    updateTaskRow(refs, task, currentSection.kind);
    taskListEl.appendChild(refs.li);
    remainingIds.delete(task.Id);
  }
  for (const staleId of remainingIds) {
    taskRowRefs.get(staleId)?.li.remove();
    taskRowRefs.delete(staleId);
  }

  updateEmptyDashboard();
}

// Backs the editor pane's welcome dashboard (shown whenever no task is selected) - refreshed
// every renderList() since a task can be checked off directly from the list without the editor
// ever opening, and the dashboard needs to reflect that live.
function updateEmptyDashboard() {
  const today = new Date();
  let dueToday = 0;
  let overdue = 0;
  let completed = 0;
  let total = 0; // Kept in lockstep with tasksForSection({kind:'all'}) - same predicate, same count.
  for (const t of appState.Tasks) {
    if (t.IsClosed) continue;
    if (t.IsDone) {
      completed++;
      continue;
    }
    total++;
    if (!t.DueDate) continue;
    const due = parseDotNetDate(t.DueDate);
    if (isSameDate(due, today)) dueToday++;
    else if (isTaskOverdue(t)) overdue++;
  }
  emptyDueTodayCount.textContent = String(dueToday);
  emptyOverdueCount.textContent = String(overdue);
  emptyCompletedCount.textContent = String(completed);
  emptyAllCount.textContent = String(total);
}

// Dashboard cards double as navigation shortcuts. Due Today/Overdue aren't sections of their own -
// they're the existing quick-filter chips applied on top of "All Tasks" - while Completed and All
// Tasks are real sections, so those two explicitly clear any lingering quick filter first (e.g. if
// you'd previously filtered to Overdue, clicking Completed should show every completed task, not
// an empty list from "Overdue AND Completed").
function setQuickFilterChip(value) {
  quickFilter = value;
  for (const chip of filterChipGroup.querySelectorAll('.chip')) {
    chip.classList.toggle('active', chip.dataset.filter === value);
  }
  updateFilterBadge();
}
// Only the quick-filter chips count here, not Sort (that's an ordering preference, not a filter)
// and not "All" (that's the unfiltered default, not a filter someone turned on) - so today this is
// always 0 or 1 since the chips are single-select, but it's written as a count rather than a
// boolean so it keeps working if the chip group ever grows multi-select.
function updateFilterBadge() {
  const count = quickFilter ? 1 : 0;
  filterBadge.textContent = String(count);
  filterBadge.classList.toggle('hidden', count === 0);
}
emptyDueTodayCard.addEventListener('click', () => {
  setQuickFilterChip('dueToday');
  selectSection({ kind: 'all' }, { preserveFilter: true });
});
emptyOverdueCard.addEventListener('click', () => {
  setQuickFilterChip('overdue');
  selectSection({ kind: 'all' }, { preserveFilter: true });
});
emptyCompletedCard.addEventListener('click', () => {
  selectSection({ kind: 'done' });
});
emptyAllCard.addEventListener('click', () => {
  selectSection({ kind: 'all' });
});
// Mobile-only dashboard shortcuts (see .empty-dashboard-links in styles.css) - same destinations
// as the bottom tab bar's More sheet (moreSheetSectionsBtn/moreSheetSettingsBtn below), just
// reachable directly from the dashboard without opening that sheet first.
emptyTagsLink.addEventListener('click', () => {
  showMobileView('sidebar');
});
emptySettingsLink.addEventListener('click', (e) => {
  e.stopPropagation();
  openAnchoredPopup(settingsDropdown, emptySettingsLink);
});

// Clicking the header logo/title is the only way back to the welcome dashboard once a task is
// selected - deselecting a task otherwise only happens as a side effect of that task being
// deleted/trashed/completed away (see the selectedTaskId resets above). Deliberately doesn't touch
// currentSection/quickFilter - just closes whatever task is open, same as clicking the header logo
// does in most apps.
function goToDashboard() {
  selectedTaskId = null;
  renderList();
  showEmptyEditor();
  showMobileView('editor');
}
brandHomeBtn.addEventListener('click', goToDashboard);

function showEmptyEditor() {
  editorEmpty.classList.remove('hidden');
  editorContent.classList.add('hidden');
  // Dashboard reuses the editor pane's "empty" slot (see goToDashboard), so data-view="editor" is
  // ambiguous between "viewing a task" (came from the list, back makes sense) and "on the
  // dashboard" (a top-level destination reached from the tab bar/brand logo, same as any section -
  // nothing to go "back" to). Reported live: "why do I have a back button when I click on
  // Dashboard?" - hide it here, restored in renderEditor below whenever a real task is showing.
  navBack.classList.add('hidden');
}

function renderEditor(task) {
  editorEmpty.classList.add('hidden');
  editorContent.classList.remove('hidden');
  navBack.classList.remove('hidden');

  editorDone.checked = task.IsDone;
  editorPinBtn.classList.toggle('active', task.IsPinned);
  editorPinBtn.title = task.IsPinned ? 'Unpin' : 'Pin';
  editorPinBtn.setAttribute('aria-label', task.IsPinned ? 'Unpin' : 'Pin');
  editorPinLabel.textContent = task.IsPinned ? 'Pinned' : 'Pin';
  editorDoneBtn.textContent = task.IsDone ? 'Mark Not Done' : 'Mark Done';
  editorTrashBtn.textContent = task.IsClosed ? 'Restore from Trash' : 'Move to Trash';
  editorDeleteBtn.classList.toggle('hidden', !task.IsClosed);

  editorTitle.value = task.Text;
  autoResizeEditorTitle();
  editorDue.value = task.DueDate ? toDateInputValue(parseDotNetDate(task.DueDate)) : '';
  editorDue.closest('.editor-field').classList.toggle('overdue', isTaskOverdue(task));
  editorPriority.value = String(task.Priority ?? TaskPriority.None);
  editorRecurrence.value = String(task.Recurrence);
  editorRecurrenceInterval.value = String(task.RecurrenceInterval ?? 1);
  editorRecurrenceIntervalField.classList.toggle('hidden', task.Recurrence === RecurrenceRule.None);

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
// A tap landing anywhere on the due/repeat pill opens its picker, rather than only the exact
// native icon pixel - see the chevron comment above for why. showPicker() is a no-op/safe to call
// even when the browser's own default click handling already opened the same picker.
for (const control of [editorDue, editorPriority, editorRecurrence, editorRecurrenceInterval]) {
  control.closest('.editor-field').addEventListener('click', (e) => {
    // A click that lands on the control itself already opens the picker via the browser's own
    // default label/control activation - that activation then re-dispatches a second click, on
    // the control, which bubbles back through this same listener. Skipping that one avoids
    // calling showPicker() twice per tap (which can toggle some pickers closed again).
    if (e.target === control) return;
    try { control.showPicker(); } catch { /* unsupported browser - falls back to native click behavior */ }
  });
}

// A <textarea> so long titles wrap instead of scrolling off the edge of the box (reported live:
// "the name box does not wrap cutting off some of the task name"). Auto-grows via JS rather than
// the CSS field-sizing property, matching this codebase's build-everything-natively/no-bleeding-edge
// stance - field-sizing isn't supported everywhere this app runs yet.
function autoResizeEditorTitle() {
  // Deferred to just before the next paint so it measures *after* any DOM changes made in the same
  // synchronous block - renderEditor() (which sets .value and calls this) runs before
  // showMobileView('editor') at several call sites, so measuring synchronously here would read
  // scrollHeight while the editor pane's mobile view is still hidden (scrollHeight of a hidden
  // element is always 0), collapsing the title to zero height right when a task is opened on mobile.
  requestAnimationFrame(() => {
    editorTitle.style.height = 'auto';
    editorTitle.style.height = `${editorTitle.scrollHeight}px`;
  });
}
editorTitle.addEventListener('keydown', (e) => {
  // Titles are single-paragraph text, not multi-line notes (that's what the body editor is for) -
  // Enter shouldn't insert a literal newline into Task.Text.
  if (e.key === 'Enter') {
    e.preventDefault();
    editorTitle.blur();
  }
});
editorTitle.addEventListener('input', (e) => {
  // Same Android/Gboard fallback as editorTagInput/quickAddInput above - some on-screen keyboards
  // never fire a real keydown for Enter/Done, only this input event, so the keydown handler above
  // alone can't be relied on to stop a newline from landing in the title.
  if (e.inputType === 'insertLineBreak') {
    editorTitle.value = editorTitle.value.replace(/\n/g, '');
    editorTitle.blur();
  }
  autoResizeEditorTitle();
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
  task.DueDate = editorDue.value ? formatDotNetDate(withDatePickerValue(task.DueDate, editorDue.value)) : null;
  touch(task);
  markDirty();
  renderList();
  editorDue.closest('.editor-field').classList.toggle('overdue', isTaskOverdue(task));
});

editorPriority.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  task.Priority = Number(editorPriority.value);
  touch(task);
  markDirty();
  renderList();
});

editorRecurrence.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  task.Recurrence = Number(editorRecurrence.value);
  editorRecurrenceIntervalField.classList.toggle('hidden', task.Recurrence === RecurrenceRule.None);
  touch(task);
  markDirty();
  renderSidebar();
});

editorRecurrenceInterval.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  task.RecurrenceInterval = Number(editorRecurrenceInterval.value);
  touch(task);
  markDirty();
});

function commitTagInput() {
  if (!editorTagInput.value.trim()) return;
  const task = findTask(selectedTaskId);
  if (!task) return;
  addTag(task, editorTagInput.value);
  editorTagInput.value = '';
  renderTagSuggestions();
}
editorTagInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') commitTagInput();
  else if (e.key === 'Escape') closeTagSuggest();
});
// Many Android on-screen keyboards (Gboard included, with word prediction active) never fire a
// real keydown for the Enter/Done key - they only surface it here, as an input event carrying
// inputType "insertLineBreak". Without this, Enter silently did nothing on those keyboards even
// though the exact same code path worked fine everywhere else. Reuses commitTagInput()'s own
// guard (empty value after the keydown handler already ran) so a keyboard that fires both isn't
// double-handled.
editorTagInput.addEventListener('input', (e) => {
  if (e.inputType === 'insertLineBreak') commitTagInput();
  else renderTagSuggestions();
});

// Tag picker dropdown (mirrors desktop's tag popup - TaskDetailViewModel's IsTagPopupOpen/
// FilteredAvailableTags/CanCreateNewTag) - reported live as "tag dropdown is not working" since web
// previously had only the bare text input with no way to browse or click an existing tag. Opens on
// focus (so tapping into an empty box still shows every available tag to browse), narrows live as
// you type, and offers a "+ Create" row once what's typed doesn't match anything existing.
function closeTagSuggest() {
  tagSuggestPopup.classList.add('hidden');
}
function renderTagSuggestions() {
  const task = findTask(selectedTaskId);
  if (!task) {
    closeTagSuggest();
    return;
  }
  const q = editorTagInput.value.trim().replace(/^#+/, '').toLowerCase();
  const taskTagsLower = task.Tags.map((t) => t.toLowerCase());
  const available = allTags().filter((t) => !taskTagsLower.includes(t));
  const filtered = q ? available.filter((t) => t.includes(q)) : available;
  const canCreate = q.length > 0 && !available.includes(q) && !taskTagsLower.includes(q);

  tagSuggestPopup.innerHTML = '';
  const addAndClose = (tag) => {
    addTag(task, tag);
    editorTagInput.value = '';
    editorTagInput.focus();
    // Refreshes the popup directly instead of closing it and counting on that focus() call to
    // reopen it via the 'focus' listener below - many mobile browsers don't blur a focused text
    // input for a tap on a button positioned right next to it (avoids flickering the on-screen
    // keyboard closed/open again for what's visually one continuous interaction), so the input
    // was often already the focused element going into this tap. focus() on an already-focused
    // element fires no new 'focus' event, so that reopen never happened - the exact same class of
    // bug as commitTagInput() above, just triggered by tapping a suggestion instead of pressing
    // Enter (reported live: empty/stale box after adding a tag, fixed only by tapping away and
    // back in for a real focus transition).
    renderTagSuggestions();
  };
  if (canCreate) {
    const createBtn = document.createElement('button');
    createBtn.type = 'button';
    createBtn.className = 'tag-suggest-item create';
    createBtn.textContent = `+ Create "${q}"`;
    createBtn.addEventListener('click', () => addAndClose(q));
    tagSuggestPopup.appendChild(createBtn);
  }
  if (filtered.length === 0 && !canCreate) {
    const empty = document.createElement('div');
    empty.className = 'tag-suggest-empty';
    if (taskTagsLower.includes(q)) {
      empty.textContent = `"#${q}" is already added to this task`;
    } else if (allTags().length > 0 && available.length === 0 && !q) {
      empty.textContent = 'All existing tags are added to this task';
    } else {
      empty.textContent = q ? 'No matching tags' : 'No tags yet - type to create one';
    }
    tagSuggestPopup.appendChild(empty);
  } else {
    for (const tag of filtered) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'tag-suggest-item';
      btn.textContent = tag;
      btn.addEventListener('click', () => addAndClose(tag));
      tagSuggestPopup.appendChild(btn);
    }
  }
  tagSuggestPopup.classList.remove('hidden');
}
editorTagInput.addEventListener('focus', renderTagSuggestions);

editorDone.addEventListener('change', () => {
  const task = findTask(selectedTaskId);
  if (!task) return;
  toggleDone(task);
  editorDue.closest('.editor-field').classList.toggle('overdue', isTaskOverdue(task));
});

editorPinBtn.addEventListener('click', () => {
  const task = findTask(selectedTaskId);
  if (task) togglePin(task);
});

editorMoreBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  const opening = editorMoreDropdown.classList.contains('hidden');
  closeDropdowns({});
  if (opening) editorMoreDropdown.classList.remove('hidden');
});

editorDoneBtn.addEventListener('click', () => {
  closeDropdowns({});
  const task = findTask(selectedTaskId);
  if (!task) return;
  toggleDone(task);
  renderEditor(task); // toggleDone doesn't self-refresh (also called from list swipes/bulk actions)
});

editorTrashBtn.addEventListener('click', () => {
  closeDropdowns({});
  const task = findTask(selectedTaskId);
  if (task) toggleTrash(task);
});

editorDeleteBtn.addEventListener('click', () => {
  closeDropdowns({});
  const task = findTask(selectedTaskId);
  if (task) deleteForever(task);
});

// attachQuickAddTrigger (defined near the quick-add popup below) layers long-press/right-click
// quick-capture onto every "New Task"/"Add Task" entry point, mirroring the desktop tray icon.
attachQuickAddTrigger(newTaskBtn, createTask);
attachQuickAddTrigger(sidebarNewTaskBtn, createTask);
attachQuickAddTrigger(emptyAddTaskBtn, createTask);
emptyTrashBtn.addEventListener('click', emptyTrash);
moveAllTrashBtn.addEventListener('click', moveAllDoneToTrash);

// --- Bulk multi-select (#142) ------------------------------------------------
// Mirrors desktop's SelectedTasks/Bulk* commands (mark done/trash/restore/delete - see
// MainViewModel.cs's InitializeBulkCommands): all four actions are always available and each
// filters its own targets (e.g. Restore only touches already-trashed selections) rather than the
// UI hiding buttons per section, exactly like the desktop toolbar's CanExecute-gated buttons.
function finishBulkAction() {
  markDirty();
  selectionMode = false;
  selectedIds.clear();
  renderSidebar();
  renderList();
}

selectToggleBtn.addEventListener('click', () => {
  selectionMode = !selectionMode;
  if (!selectionMode) selectedIds.clear();
  renderList();
});

bulkCancelBtn.addEventListener('click', () => {
  selectionMode = false;
  selectedIds.clear();
  renderList();
});

bulkSelectAllBtn.addEventListener('click', () => {
  const visible = currentTasks();
  const allSelected = visible.length > 0 && visible.every((t) => selectedIds.has(t.Id));
  for (const t of visible) {
    if (allSelected) selectedIds.delete(t.Id);
    else selectedIds.add(t.Id);
  }
  renderList();
});

bulkDoneBtn.addEventListener('click', async () => {
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id) && !t.IsDone);
  if (targets.length === 0) return;
  const confirmed = await confirmModal(`Mark ${targets.length} task(s) complete?`,
    { title: 'Mark Complete', confirmLabel: 'Mark Complete' });
  if (!confirmed) return;
  for (const t of targets) {
    t.IsDone = true;
    touch(t);
  }
  finishBulkAction();
  pushUndo(`Completed ${targets.length} task(s)`, () => {
    for (const t of targets) {
      t.IsDone = false;
      touch(t);
    }
  });
});

// Pin, due date, and tags, unlike the actions above, don't remove the selected tasks from view or
// make the selection stop making sense - selectionMode stays on and selectedIds stays intact
// afterward (refreshAfterBulkEdit, not finishBulkAction) so these can be chained on the same
// selection without re-selecting the tasks in between.
function refreshAfterBulkEdit() {
  markDirty();
  renderSidebar();
  renderList();
}

// Mirrors desktop's BulkTogglePinCommand: toggles each selected task's own pin state independently
// rather than forcing every selected task to the same pinned/unpinned value.
bulkPinBtn.addEventListener('click', async () => {
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id));
  if (targets.length === 0) return;
  const confirmed = await confirmModal(`Toggle pin on ${targets.length} task(s)?`,
    { title: 'Toggle Pin', confirmLabel: 'Toggle Pin' });
  if (!confirmed) return;
  for (const t of targets) {
    t.IsPinned = !t.IsPinned;
    touch(t);
  }
  refreshAfterBulkEdit();
  pushUndo(`Toggled pin on ${targets.length} task(s)`, () => {
    for (const t of targets) {
      t.IsPinned = !t.IsPinned;
      touch(t);
    }
  });
});

bulkDueBtn.addEventListener('click', () => {
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id));
  if (targets.length === 0) return;
  bulkDueInput.value = '';
  try { bulkDueInput.showPicker(); } catch { bulkDueInput.click(); }
});
bulkDueInput.addEventListener('change', async () => {
  if (!bulkDueInput.value) return;
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id));
  if (targets.length === 0) return;
  const [y, mo, d] = bulkDueInput.value.split('-').map(Number);
  const confirmed = await confirmModal(`Set the due date to ${formatDate(new Date(y, mo - 1, d))} on ${targets.length} task(s)?`,
    { title: 'Set Due Date', confirmLabel: 'Set Due Date' });
  if (!confirmed) return;
  // Snapshot each task's own prior due date - they didn't necessarily share one before this edit.
  const previous = targets.map((t) => [t, t.DueDate]);
  for (const t of targets) {
    t.DueDate = formatDotNetDate(withDatePickerValue(t.DueDate, bulkDueInput.value));
    touch(t);
  }
  refreshAfterBulkEdit();
  pushUndo(`Set due date on ${targets.length} task(s)`, () => {
    for (const [t, due] of previous) {
      t.DueDate = due;
      touch(t);
    }
  });
});

// Bulk tag popup mirrors the single-task tag picker's visual language (#tag-suggest-popup's
// tag-suggest-item/tag-suggest-empty classes) but isn't built on top of renderTagSuggestions()
// itself - that function's "already on this task" filtering and empty-state copy are both written
// in terms of exactly one task, which doesn't translate to a set of tasks that may each already
// have a different subset of tags.
let bulkTagTargets = [];
function closeBulkTagPopup() {
  bulkTagPopup.classList.add('hidden');
  bulkTagTargets = [];
}
async function applyBulkTag(tag) {
  const targets = bulkTagTargets;
  const confirmed = await confirmModal(`Add the "${tag}" tag to ${targets.length} task(s)?`,
    { title: 'Add Tag', confirmLabel: 'Add Tag' });
  closeBulkTagPopup();
  if (!confirmed) return;
  // Only the tasks that didn't already carry this tag actually change - undo must revert exactly
  // that subset, or it would strip a tag a task already had on its own before this edit.
  const added = [];
  for (const t of targets) {
    if (!t.Tags.some((x) => x.toLowerCase() === tag)) {
      t.Tags.push(tag);
      added.push(t);
    }
    touch(t);
  }
  refreshAfterBulkEdit();
  if (added.length === 0) return;
  pushUndo(`Added tag "${tag}" to ${added.length} task(s)`, () => {
    for (const t of added) {
      t.Tags = t.Tags.filter((x) => x.toLowerCase() !== tag);
      touch(t);
    }
  });
}
function renderBulkTagSuggestions() {
  const q = normalizeTagName(bulkTagInput.value);
  const available = allTags();
  const filtered = q ? available.filter((t) => t.includes(q)) : available;
  const canCreate = q.length > 0 && !available.includes(q);

  bulkTagSuggestions.innerHTML = '';
  if (canCreate) {
    const createBtn = document.createElement('button');
    createBtn.type = 'button';
    createBtn.className = 'tag-suggest-item create';
    createBtn.textContent = `+ Create "${q}"`;
    createBtn.addEventListener('click', () => applyBulkTag(q));
    bulkTagSuggestions.appendChild(createBtn);
  }
  if (filtered.length === 0 && !canCreate) {
    const empty = document.createElement('div');
    empty.className = 'tag-suggest-empty';
    empty.textContent = allTags().length === 0 ? 'No tags yet - type to create one' : q ? 'No matching tags' : 'No tags yet';
    bulkTagSuggestions.appendChild(empty);
  } else {
    for (const tag of filtered) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'tag-suggest-item';
      btn.textContent = tag;
      btn.addEventListener('click', () => applyBulkTag(tag));
      bulkTagSuggestions.appendChild(btn);
    }
  }
}
bulkTagBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  bulkTagTargets = appState.Tasks.filter((t) => selectedIds.has(t.Id));
  if (bulkTagTargets.length === 0) return;
  bulkTagInput.value = '';
  openAnchoredPopup(bulkTagPopup, bulkTagBtn);
  renderBulkTagSuggestions();
  bulkTagInput.focus();
});
function commitBulkTagInput() {
  const tag = normalizeTagName(bulkTagInput.value);
  if (!tag || bulkTagTargets.length === 0) return;
  applyBulkTag(tag);
}
bulkTagInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') commitBulkTagInput();
  else if (e.key === 'Escape') closeBulkTagPopup();
});
// Same Android/Gboard "insertLineBreak" fallback as editorTagInput/quickAddInput elsewhere in this
// file - some on-screen keyboards never fire a real keydown for Enter/Done, only this input event.
bulkTagInput.addEventListener('input', (e) => {
  if (e.inputType === 'insertLineBreak') commitBulkTagInput();
  else renderBulkTagSuggestions();
});

bulkTrashBtn.addEventListener('click', async () => {
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id) && !t.IsClosed);
  if (targets.length === 0) return;
  const confirmed = await confirmModal(`Move ${targets.length} task(s) to Trash?`,
    { title: 'Move to Trash', confirmLabel: 'Move to Trash' });
  if (!confirmed) return;
  for (const t of targets) {
    t.IsClosed = true;
    touch(t);
  }
  if (selectedTaskId && targets.some((t) => t.Id === selectedTaskId)) {
    selectedTaskId = null;
    showEmptyEditor();
  }
  finishBulkAction();
  pushUndo(`Moved ${targets.length} task(s) to Trash`, () => {
    for (const t of targets) {
      t.IsClosed = false;
      touch(t);
    }
  });
});

bulkRestoreBtn.addEventListener('click', async () => {
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id) && t.IsClosed);
  if (targets.length === 0) return;
  const confirmed = await confirmModal(`Restore ${targets.length} task(s) from Trash?`,
    { title: 'Restore from Trash', confirmLabel: 'Restore' });
  if (!confirmed) return;
  for (const t of targets) {
    t.IsClosed = false;
    touch(t);
  }
  finishBulkAction();
  pushUndo(`Restored ${targets.length} task(s) from Trash`, () => {
    for (const t of targets) {
      t.IsClosed = true;
      touch(t);
    }
  });
});

bulkDeleteBtn.addEventListener('click', async () => {
  const targets = appState.Tasks.filter((t) => selectedIds.has(t.Id));
  if (targets.length === 0) return;
  const confirmed = await confirmModal(`Delete ${targets.length} task(s) permanently? This cannot be undone.`,
    { title: 'Delete Permanently', confirmLabel: 'Delete', danger: true });
  if (!confirmed) return;
  const targetIds = new Set(targets.map((t) => t.Id));
  appState.Tasks = appState.Tasks.filter((t) => !targetIds.has(t.Id));
  for (const id of targetIds) recordTombstone(id);
  if (selectedTaskId && targetIds.has(selectedTaskId)) {
    selectedTaskId = null;
    showEmptyEditor();
  }
  finishBulkAction();
});
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') {
    e.preventDefault();
    createTask();
  }
});

document.addEventListener('keydown', (e) => {
  const isFindShortcut = (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f';
  if (!isFindShortcut && e.key !== '/') return;
  if (e.key === '/') {
    // Ctrl+F is a modifier combo (like Ctrl+N above) so it can't collide with normal typing, but
    // a bare "/" is a real character - don't hijack it while the user is actually typing one into
    // a task title, tag, or note body.
    const target = document.activeElement;
    const isEditable = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable);
    if (isEditable) return;
  }
  e.preventDefault(); // also suppresses the browser's own native Ctrl+F find-in-page
  searchBox.focus();
  searchBox.select();
});

searchBox.addEventListener('input', () => {
  searchQuery = searchBox.value;
  searchClearBtn.classList.toggle('hidden', !searchQuery);
  saveViewBtn.disabled = !searchQuery.trim();
  renderList();
});
searchClearBtn.addEventListener('click', () => {
  searchBox.value = '';
  searchQuery = '';
  searchClearBtn.classList.add('hidden');
  saveViewBtn.disabled = true;
  renderList();
  searchBox.focus();
});
saveViewBtn.addEventListener('click', saveCurrentSearchAsView);

sortChipGroup.addEventListener('click', (e) => {
  const btn = e.target.closest('.chip');
  if (!btn) return;
  sortKey = btn.dataset.sort;
  for (const chip of sortChipGroup.querySelectorAll('.chip')) chip.classList.toggle('active', chip === btn);
  renderList();
});
filterChipGroup.addEventListener('click', (e) => {
  const btn = e.target.closest('.chip');
  if (!btn) return;
  setQuickFilterChip(btn.dataset.filter);
  renderList();
});

// --- Quick-add: the list pane's always-visible row, and the long-press/right-click popup below --
// Deliberately a one-shot parse at the Enter/commit moment, not applied to editorTitle (see
// parseQuickAdd's home in model.js) - editorTitle is two-way bound and saves on every keystroke,
// so there's no safe commit point to strip a "#tag" out from under someone still mid-word typing
// it. Both inputs below share this same clean commit moment (Enter), same as the desktop
// QuickAddWindow. Parametrized over (inputEl, previewEl) rather than closing over quickAddInput/
// quickAddPreview directly, since #quick-add-popup-input/#quick-add-popup-preview need the exact
// same parse-preview-commit behavior.
function commitQuickAdd(inputEl, previewEl) {
  if (!inputEl.value.trim()) return;
  createQuickTask(inputEl.value);
  inputEl.value = '';
  updateQuickAddPreview(inputEl, previewEl);
}
quickAddInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') commitQuickAdd(quickAddInput, quickAddPreview);
});
// Same Android/Gboard "insertLineBreak" fallback as editorTagInput above - some on-screen
// keyboards never fire a real keydown for Enter/Done, only this input event.
quickAddInput.addEventListener('input', (e) => {
  if (e.inputType === 'insertLineBreak') commitQuickAdd(quickAddInput, quickAddPreview);
  else updateQuickAddPreview(quickAddInput, quickAddPreview);
});

// ROADMAP.md #135: mirrors desktop's QuickAddWindow.TitleBox_TextChanged - a pure, side-effect-free
// re-parse on every keystroke (parseQuickAdd never mutates appState), so nothing here needs the
// same "wait for a commit point" caution the comment above explains for editorTitle.
function updateQuickAddPreview(inputEl, previewEl) {
  const raw = inputEl.value;
  if (!raw.trim()) {
    previewEl.classList.add('hidden');
    return;
  }
  const parsed = parseQuickAdd(raw);
  const parts = [escapeHtml(parsed.text || '(untitled)')];
  if (parsed.dueDate) {
    const due = parseDotNetDate(parsed.dueDate);
    const dueLabel = due.getHours() === 0 && due.getMinutes() === 0
      ? due.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })
      : due.toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
    parts.push(`due ${dueLabel}`);
  }
  if (parsed.tags.length > 0) {
    parts.push(parsed.tags.map((t) => `<span class="qa-tag">#${escapeHtml(t)}</span>`).join(' '));
  }
  previewEl.innerHTML = parts.join('  ·  ');
  previewEl.classList.remove('hidden');
}

// --- Quick-add popup: reachable from anywhere, not just the list pane --------------------------
// The row above only exists inside #list-pane (hidden on Done/Trash too - see renderList), so
// there's no way to capture a task from it while looking at the Dashboard, Recurring, an editor on
// mobile, etc. Mirrors the desktop app's tray-icon "New Task" menu item and Ctrl+Alt+T global
// hotkey: both open the same lightweight QuickAddWindow without disturbing whatever else is on
// screen. createQuickTask() already adds straight to "All Tasks" in the background on its own
// (see its own comment) without switching the visible section or mobile view, so this popup gets
// that "capture without interrupting" behavior for free.
function openQuickAddPopup(anchorBtn) {
  openAnchoredPopup(quickAddPopup, anchorBtn);
  quickAddPopupInput.value = '';
  updateQuickAddPreview(quickAddPopupInput, quickAddPopupPreview);
  quickAddPopupInput.focus();
}
quickAddPopupInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') commitQuickAdd(quickAddPopupInput, quickAddPopupPreview);
  else if (e.key === 'Escape') quickAddPopup.classList.add('hidden');
});
quickAddPopupInput.addEventListener('input', (e) => {
  if (e.inputType === 'insertLineBreak') commitQuickAdd(quickAddPopupInput, quickAddPopupPreview);
  else updateQuickAddPreview(quickAddPopupInput, quickAddPopupPreview);
});

// A touch-and-hold (~500ms - standard long-press timing, matches the touch UX research this was
// built against) or a right-click on a "New Task" button opens the popup above instead of that
// button's normal action (onClick, i.e. createTask - which opens the full editor) - same idea as
// right-clicking the desktop tray icon for a quick-capture menu instead of a plain double-click to
// open the main window. A plain tap/click still calls onClick unchanged; the two only diverge once
// a press has actually been held past the threshold.
const LONG_PRESS_MS = 500;
function attachQuickAddTrigger(btn, onClick) {
  let pressTimer = null;
  let longPressed = false;
  let startX = 0;
  let startY = 0;
  // A real finger never holds perfectly still - a few px of jitter during a genuine long-press is
  // normal and fires touchmove regardless. Cancelling on any touchmove at all (as this originally
  // did) killed the pending timer almost immediately on real touch hardware, so the popup could
  // never actually open on a phone - reported live as "the long click to add task doesn't work on
  // mobile" despite passing in synthetic (perfectly-still) touchstart/touchend testing. 10px matches
  // the touch-slop constants native long-press gesture recognizers use for the same reason.
  const MOVE_CANCEL_PX = 10;
  const start = (e) => {
    longPressed = false;
    clearTimeout(pressTimer);
    const touch = e.touches && e.touches[0];
    startX = touch ? touch.clientX : 0;
    startY = touch ? touch.clientY : 0;
    pressTimer = setTimeout(() => {
      longPressed = true;
      haptic();
      openQuickAddPopup(btn);
    }, LONG_PRESS_MS);
  };
  const cancel = () => clearTimeout(pressTimer);
  // A finger dragging off the button reads as a scroll/gesture, not a hold-in-place - same
  // cancel-on-move convention as the swipe-to-complete/trash gesture elsewhere in this file, just
  // past the jitter threshold above instead of on the first pixel of movement.
  const handleTouchMove = (e) => {
    const touch = e.touches && e.touches[0];
    if (!touch) { cancel(); return; }
    const dx = touch.clientX - startX;
    const dy = touch.clientY - startY;
    if (Math.hypot(dx, dy) > MOVE_CANCEL_PX) cancel();
  };
  btn.addEventListener('touchstart', start, { passive: true });
  btn.addEventListener('touchend', cancel);
  btn.addEventListener('touchcancel', cancel);
  btn.addEventListener('touchmove', handleTouchMove, { passive: true });
  btn.addEventListener('mousedown', start);
  btn.addEventListener('mouseup', cancel);
  btn.addEventListener('mouseleave', cancel);
  btn.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    cancel();
    openQuickAddPopup(btn);
  });
  btn.addEventListener('click', (e) => {
    if (longPressed) {
      // Stops this same click from reaching the document-level listener that closes
      // quick-add-popup on any outside click (see its containment guard near listFilterRow) -
      // without this, the popup would open and immediately close itself, same issue that
      // listener's tagSuggestPopup exclusion exists for.
      e.stopPropagation();
      longPressed = false;
      return;
    }
    onClick(e);
  });
}

// --- Keyboard shortcuts modal --------------------------------------------------
function openShortcuts() {
  closeDropdowns({});
  shortcutsModal.classList.remove('hidden');
}
function closeShortcuts() {
  shortcutsModal.classList.add('hidden');
}
shortcutsBtn.addEventListener('click', openShortcuts);
emptyShortcutsBtn.addEventListener('click', openShortcuts);
shortcutsCloseBtn.addEventListener('click', closeShortcuts);
shortcutsModal.addEventListener('click', (e) => {
  if (e.target === shortcutsModal) closeShortcuts();
});

// --- First-run onboarding -------------------------------------------------------
const ONBOARDED_KEY = 'tasky-onboarded';
function closeOnboarding() {
  onboardingModal.classList.add('hidden');
  localStorage.setItem(ONBOARDED_KEY, '1');
}
// Only fires from loadFromDriveWithRetry() the first time it finds a genuinely new Drive account
// (no Tasky file yet) - not on every empty state, so it won't reappear once someone's actually
// used the app, even if they later trash every task.
function maybeShowOnboarding() {
  if (localStorage.getItem(ONBOARDED_KEY)) return;
  closeDropdowns({});
  onboardingModal.classList.remove('hidden');
}
// Each sample task pairs with one line of the tour's bullet list above (see onboarding-list) and
// stays behind as a standing, revisitable reminder of it after the tour itself is dismissed - the
// modal only shows once, but a task sitting in "All Tasks" doesn't go away until you delete it.
// Two of the four are worded from primaryInputIsMouse (see its own comment) rather than mentioning
// both gestures every time, same reasoning as the tip above: "swipe" is nonsense on a desktop mouse
// and "right-click" doesn't work with a finger, so each device only sees the one that actually
// applies to it.
onboardingDoneBtn.addEventListener('click', () => {
  if (onboardingAddSamplesCheck.checked) {
    createQuickTask('Welcome to Tasky! Tap a task to open the editor #getting-started');
    createDemoTask('This due date came from typing !due:today @9am - try that syntax in Quick Add below', '!due:today @9am');
    createQuickTask(primaryInputIsMouse
      ? 'Right-click the + New Task button for a quick-add popup you can open from anywhere #getting-started'
      : 'Long-press the + New Task button for a quick-add popup you can open from anywhere #getting-started');
    createQuickTask(primaryInputIsMouse
      ? "Click this task's checkbox to mark it done #getting-started"
      : 'Swipe this task right to mark it done, left to trash it #getting-started');
  }
  closeOnboarding();
});
onboardingModal.addEventListener('click', (e) => {
  if (e.target === onboardingModal) closeOnboarding();
});
// Bypasses the ONBOARDED_KEY check below - an explicit replay from the About popup should always
// show it, regardless of whether it's been seen (or dismissed) before.
aboutReplayTourBtn.addEventListener('click', () => {
  closeDropdowns({});
  onboardingModal.classList.remove('hidden');
});

// --- Settings/About popups -------------------------------------------------------
settingsBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  openAnchoredPopup(settingsDropdown, settingsBtn);
});
aboutBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  openAnchoredPopup(aboutDropdown, aboutBtn);
});
moreSheetSettingsBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  openAnchoredPopup(settingsDropdown, moreSheetSettingsBtn);
});
moreSheetAboutBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  openAnchoredPopup(aboutDropdown, moreSheetAboutBtn);
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'F1' || ((e.ctrlKey || e.metaKey) && e.key === '/')) {
    e.preventDefault();
    openShortcuts();
  }
});

// --- Mobile "More" popup ------------------------------------------------------
// The bottom tab bar's More button used to jump straight to the sections+tags list - the only
// other way back to the dashboard was the header brand/logo. This popup offers both destinations
// from the one button (opened/positioned in renderMobileTabbar above; closed on outside click via
// the document listener near listFilterRow, and on Escape below).
function closeMoreSheet() {
  moreSheetPopup.classList.add('hidden');
}
moreSheetDashboardBtn.addEventListener('click', () => {
  closeMoreSheet();
  goToDashboard();
});
moreSheetSectionsBtn.addEventListener('click', () => {
  closeMoreSheet();
  showMobileView('sidebar');
});

// --- Mobile / tablet navigation ---------------------------------------------
// Pushes each sidebar/list/editor transition onto browser history so Android's hardware/gesture
// back button navigates within the app (editor -> list -> sidebar, same as the in-app back button)
// instead of falling straight through to whatever page preceded Tasky in the tab's history - which,
// without any history entries of our own to land on first, could be a stale Google sign-in page
// (see armHistoryTrap above, which now defers to this for any popstate that lands on one of these
// states, and only re-arms itself once back navigation runs past this stack).
let restoringMobileView = false;
function showMobileView(view) {
  const prevView = appEl.dataset.view;
  appEl.dataset.view = view;
  renderMobileTabbar();
  if (!restoringMobileView && view !== prevView) {
    history.pushState({ taskyView: view }, '', location.pathname + location.search);
  }
}
window.addEventListener('popstate', (e) => {
  const view = e.state && e.state.taskyView;
  if (!view) return;
  restoringMobileView = true;
  showMobileView(view);
  restoringMobileView = false;
});
navBack.addEventListener('click', () => {
  showMobileView(appEl.dataset.view === 'editor' ? 'list' : 'sidebar');
});
sidebarDrawerBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  const open = appEl.dataset.sidebarOpen === 'true';
  appEl.dataset.sidebarOpen = String(!open);
});
appEl.dataset.view = 'sidebar';
// Tags the page's own initial history entry as the base of the view stack (replaceState, not
// pushState - it's not a new navigation) so the very first back press out of the app is
// recognizable as "ran off the end of our stack" rather than landing on an untagged entry that
// looks the same as one of the sign-in redirect's own states to the popstate listeners above.
history.replaceState({ taskyView: 'sidebar' }, '', location.pathname + location.search);

// --- Helpers ------------------------------------------------------------
function formatDate(date) {
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}
function toDateInputValue(date) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
// Combines a <input type="date"> pick (a bare y-m-d string, no time) with the time-of-day already
// on the task's due date, so touching the date field doesn't silently zero out an explicit time
// set via quick-add's @3pm or on desktop - ROADMAP.md #121. No prior DueDate (or one with no time
// component - see ReminderScheduler.cs's matching desktop-side comment) just gets midnight, same
// as before.
function withDatePickerValue(existingDueDate, dateInputValue) {
  const [y, mo, d] = dateInputValue.split('-').map(Number);
  const existing = existingDueDate ? parseDotNetDate(existingDueDate) : null;
  return existing
    ? new Date(y, mo - 1, d, existing.getHours(), existing.getMinutes(), existing.getSeconds(), existing.getMilliseconds())
    : new Date(y, mo - 1, d);
}
function escapeHtml(str) {
  const d = document.createElement('div');
  d.textContent = str;
  return d.innerHTML;
}

// --- Whole-list export (ROADMAP.md #135) --------------------------------------
// Mirrors ExportService.ExportAllToMarkdown/ExportAllToHtml (Services/ExportService.cs) exactly -
// same section order, same field labels, same skip-trashed/sort-by-done rule. Reads Body's plain-
// text mirror (NoteBlock.Text/ChecklistItem.Text) directly, same as the desktop version, rather
// than any richer Rtf formatting - see that file's doc comment for why.
function escapeMarkdown(text) {
  if (!text) return text;
  let out = '';
  for (const c of text) {
    if ('\\`*_[]|'.includes(c)) out += '\\';
    out += c;
  }
  return out.startsWith('#') ? '\\' + out : out;
}

function exportedTasksInOrder() {
  return appState.Tasks
    .filter((t) => !t.IsClosed)
    .slice()
    .sort((a, b) => (a.IsDone === b.IsDone ? a.Text.localeCompare(b.Text) : a.IsDone ? 1 : -1));
}

function appendBodyAsMarkdown(lines, task) {
  for (const block of task.Body) {
    switch (block.Type) {
      case NoteBlockType.Text:
        if (block.Text && block.Text.trim()) {
          lines.push(escapeMarkdown(block.Text), '');
        }
        break;
      case NoteBlockType.Checklist:
        for (const item of block.ChecklistItems)
          lines.push(`- [${item.IsChecked ? 'x' : ' '}] ${escapeMarkdown(item.Text)}`);
        lines.push('');
        break;
      case NoteBlockType.Link:
        lines.push(`[${escapeMarkdown(block.LinkLabel || block.Url)}](${block.Url})`, '');
        break;
      case NoteBlockType.Photo:
      case NoteBlockType.File:
        lines.push(`*Attachment: ${escapeMarkdown(block.FileName)}*`, '');
        break;
    }
  }
}

function exportAllToMarkdown() {
  const lines = ['# Tasky Export', '', `Exported ${new Date().toLocaleString()}`, ''];
  for (const task of exportedTasksInOrder()) {
    lines.push('---', '', `## ${task.IsDone ? '[x] ' : ''}${escapeMarkdown(task.Text)}`, '');
    if (task.DueDate) lines.push(`**Due Date:** ${parseDotNetDate(task.DueDate).toISOString().slice(0, 10)}  `);
    if (task.Tags.length > 0) lines.push(`**Tags:** ${task.Tags.map((t) => `\`${t}\``).join(', ')}  `);
    lines.push(`**Status:** ${task.IsDone ? 'Completed' : 'Open'}  `, '');
    appendBodyAsMarkdown(lines, task);
    lines.push('');
  }
  downloadTextFile(`Tasky Export ${new Date().toISOString().slice(0, 10)}.md`, lines.join('\n'), 'text/markdown');
}

function appendBodyAsHtml(lines, task) {
  for (const block of task.Body) {
    switch (block.Type) {
      case NoteBlockType.Text:
        if (block.Text && block.Text.trim()) lines.push(`<p>${escapeHtml(block.Text)}</p>`);
        break;
      case NoteBlockType.Checklist:
        for (const item of block.ChecklistItems) {
          lines.push(`<div class="checklist-item"><input type="checkbox" ${item.IsChecked ? 'checked disabled' : 'disabled'}/> <span>${escapeHtml(item.Text)}</span></div>`);
        }
        break;
      case NoteBlockType.Link: {
        const label = block.LinkLabel || block.Url;
        lines.push(`<p><a href="${escapeHtml(block.Url)}">${escapeHtml(label)}</a></p>`);
        break;
      }
      case NoteBlockType.Photo:
      case NoteBlockType.File:
        lines.push(`<p class="attachment-ref">Attachment: ${escapeHtml(block.FileName)}</p>`);
        break;
    }
  }
}

function exportAllToHtml() {
  const lines = [
    '<!DOCTYPE html>', '<html lang="en">', '<head>', '<meta charset="UTF-8">', '<title>Tasky Export</title>',
    '<style>',
    "body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; max-width: 800px; margin: 40px auto; padding: 0 20px; color: #2C3338; background: #FFF; }",
    'h1 { font-size: 26px; margin-bottom: 4px; color: #1E2327; }',
    '.exported-at { font-size: 13px; color: #646970; margin-bottom: 24px; }',
    'h2 { font-size: 19px; margin: 28px 0 4px; padding-top: 20px; border-top: 1px solid #E2E4E7; color: #1E2327; }',
    'h2.done { color: #8C8F94; text-decoration: line-through; }',
    '.meta { font-size: 13px; color: #646970; margin-bottom: 10px; }',
    '.tag { display: inline-block; background: #F0F0F1; padding: 2px 8px; border-radius: 12px; font-size: 11.5px; margin-right: 6px; }',
    '.content p { margin: 6px 0; }',
    '.checklist-item { display: flex; align-items: center; margin: 3px 0; }',
    '.checklist-item input { margin-right: 8px; }',
    '.attachment-ref { color: #646970; font-style: italic; }',
    '</style>', '</head>', '<body>',
    '<h1>Tasky Export</h1>',
    `<div class="exported-at">Exported ${escapeHtml(new Date().toLocaleString())}</div>`,
  ];
  for (const task of exportedTasksInOrder()) {
    lines.push(`<h2 class="${task.IsDone ? 'done' : ''}">${escapeHtml(task.Text)}</h2>`, '<div class="meta">');
    if (task.DueDate) lines.push(`<div><strong>Due Date:</strong> ${parseDotNetDate(task.DueDate).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' })}</div>`);
    if (task.Tags.length > 0) {
      lines.push(`<div style="margin-top:4px;"><strong>Tags:</strong> ${task.Tags.map((t) => `<span class="tag">${escapeHtml(t)}</span>`).join('')}</div>`);
    }
    lines.push('</div>', '<div class="content">');
    appendBodyAsHtml(lines, task);
    lines.push('</div>');
  }
  lines.push('</body>', '</html>');
  downloadTextFile(`Tasky Export ${new Date().toISOString().slice(0, 10)}.html`, lines.join('\n'), 'text/html');
}

function downloadTextFile(filename, text, mimeType) {
  const blob = new Blob([text], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

exportMarkdownBtn.addEventListener('click', () => {
  closeDropdowns({});
  exportAllToMarkdown();
});
exportHtmlBtn.addEventListener('click', () => {
  closeDropdowns({});
  exportAllToHtml();
});

// --- PWA install banner (roadmap #37) ------------------------------------------
// Already running standalone (installed and launched from the home screen/app list)? Nothing to
// offer - covers both the standard media-query check and iOS Safari's older, non-standard
// navigator.standalone flag (display-mode never reports standalone there even when installed).
const INSTALL_DISMISSED_KEY = 'tasky-install-dismissed';
const isStandalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
const isIos = /iphone|ipad|ipod/.test(navigator.userAgent.toLowerCase());
let deferredInstallPrompt = null;

function dismissInstallBanner() {
  installBanner.classList.add('hidden');
  try {
    localStorage.setItem(INSTALL_DISMISSED_KEY, '1');
  } catch {
    // Best-effort - worst case the banner can reappear next session, not worth failing over.
  }
}

function showInstallBanner() {
  if (isStandalone) return;
  try {
    if (localStorage.getItem(INSTALL_DISMISSED_KEY)) return;
  } catch {
    // If storage is blocked, fall through and show it rather than assume dismissed.
  }
  installBanner.classList.remove('hidden');
}

// Falls back to Chrome's own always-available manual path (⋮ menu > Install app / Add to Home
// screen) - used whenever the programmatic prompt() route doesn't pan out, so a stuck banner
// never leaves the user with literally no way to install.
function showManualAndroidFallback() {
  installActionBtn.classList.add('hidden');
  installBannerMsg.classList.add('hidden');
  installIosHint.textContent = 'Open Chrome’s menu (⋮), then tap Install app or Add to Home screen.';
  installIosHint.classList.remove('hidden');
}

installDismissBtn.addEventListener('click', dismissInstallBanner);
installActionBtn.addEventListener('click', async () => {
  if (!deferredInstallPrompt) return;
  const capturedPrompt = deferredInstallPrompt;
  // A captured beforeinstallprompt event can go stale in the browser's own internal state well
  // before this code has any way to know that from the outside (no error, no rejection - Chrome
  // just doesn't show anything and userChoice never settles). That's indistinguishable from "the
  // user is still looking at the real dialog" from here, so this can't wait forever: past a short
  // timeout, treat it as failed and fall back to the manual path rather than leaving the button in
  // limbo. installActionBtn.disabled guards against a double-tap re-entering this while it's
  // in flight (a second prompt() call on the same event throws "already used").
  installActionBtn.disabled = true;
  try {
    capturedPrompt.prompt();
    const outcome = await Promise.race([
      capturedPrompt.userChoice,
      new Promise((_, reject) => setTimeout(() => reject(new Error('TIMEOUT')), 4000)),
    ]);
    console.info('Tasky: install prompt outcome', outcome);
    deferredInstallPrompt = null;
    dismissInstallBanner();
  } catch (err) {
    console.warn('Tasky: install prompt failed or timed out - falling back to manual instructions', err);
    deferredInstallPrompt = null;
    showManualAndroidFallback();
  } finally {
    installActionBtn.disabled = false;
  }
});

if (isIos) {
  // No beforeinstallprompt on any iOS browser (Apple gives web apps no programmatic install API
  // at all, regardless of engine) - the only path is manual Share > Add to Home Screen, so show
  // static instructions immediately rather than waiting for an event that will never fire.
  installIosHint.classList.remove('hidden');
  installBannerMsg.classList.add('hidden');
  showInstallBanner();
} else {
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault(); // stop the browser's own mini-infobar so this custom banner is the only prompt
    deferredInstallPrompt = e;
    installActionBtn.classList.remove('hidden');
    showInstallBanner();
  });
}

window.addEventListener('appinstalled', () => {
  deferredInstallPrompt = null;
  installBanner.classList.add('hidden');
});

// A no-op service worker, registered purely because Chrome/Android's installability check
// requires *some* registered service worker with a fetch handler before beforeinstallprompt will
// fire at all - see ROADMAP.md gating decision #4. It deliberately does no caching and has no
// offline behavior of its own (that's #6/#7, both deferred): every fetch just passes straight
// through to the network, unchanged.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('./sw.js').catch((err) => {
    console.warn('Tasky: service worker registration failed (install banner may not appear)', err);
  });
}

boot();
