# Tasky Web / Mobile Review (2026-09-10)

Scope: `docs/` (the shared Web + Mobile PWA) and `functions/`. Desktop code was read only to check parity;
nothing here proposes a change to the `.tasky` wire format. Where a fix touches data it uses block types
and fields Desktop already reads (Photo/File blocks, tombstones), so parity is preserved.

Severity: **P0** data loss / silent breakage · **P1** user-visible bug or parity break · **P2** UX friction ·
**P3** polish / code quality.

---

## 1. Bugs

### P0 — data loss or silent corruption

**Status (2026-09-10): B1–B4 fixed** in the working tree (cache-bust v22 → v23, 2 new parity tests, 77/77 passing).
B1 hoists inline attachments into Photo/File blocks before Rtf is cleared (`editor.js hoistInlineAttachments`);
B2 has `mergeRemoteState` return `updatedIds`/`removedIds` and `refreshEditorAfterMerge` in `app.js` re-renders
the open task with the caret restored, or closes it if it was deleted remotely; B3 tracks in-flight uploads in
`editor.js` and `saveToDrive` awaits `waitForPendingUploads()` before serializing; B4 adds a 5 s → 60 s backoff
retry in `performSave` plus an immediate flush on `online` and on tab return.

| # | Finding | Where | Fix |
|---|---------|-------|-----|
| B1 | **Editing a Desktop-authored paragraph on Web deletes its inline photos/files/checklists/tables.** The text-block `input` handler sets `block.Rtf = ''`. Desktop embeds pasted images (`UriSource=`), Insert-File chips (`<Grid Tag=…>`), checklists and tables *inside* that Rtf. Once cleared they vanish on both platforms, and Desktop's attachment-reference prune will then delete the now-unreferenced files from Drive permanently. The comment says "the actual words are never lost" - the words survive, the attachments don't. | `docs/js/editor.js:123-136` | Before clearing Rtf, hoist `extractInlineImageFileNames()` / `extractInlineFileNames()` results into real `Photo`/`File` blocks inserted after the text block (Desktop already renders those types). If the Rtf also contains `<CheckBox`, `<Table` or `<Hyperlink`, either make the block read-only with an "Edit on desktop to keep formatting" note, or confirm before the first keystroke. |
| B2 | **A background merge can orphan the open editor.** `mergeFromRemote` skips `renderEditor` while the editor has focus, but `applyTaskFields` replaces `task.Body` with fresh objects. The contentEditable listeners still write into the *old* block objects, so everything typed after that merge is silently missing from the next save. If the open task was removed by a remote tombstone, `findTask()` returns null and the editor keeps showing a dead task whose edits go nowhere. | `docs/js/app.js:1375-1378`, `docs/js/sync.js:41` | Have `mergeRemoteState` return the updated/removed IDs. If `selectedTaskId` is among them, re-render the editor (capture/restore caret offsets) or show an inline "Updated on another device - reload" banner; call `showEmptyEditor()` when it was removed. |
| B3 | **Save can upload a JSON that references an attachment that isn't on Drive yet.** Photo/File pick pushes the block and calls `markDirty()` immediately; the 4 s debounced save often beats a slow mobile upload. If the tab is killed mid-upload the reference is permanently dangling ("not found on Drive" on every device). | `docs/js/editor.js:790-798`, `826-834` | Track in-flight uploads in a Set of promises; `saveToDrive()` awaits them before stringifying. Show an upload spinner/progress on the block. |
| B4 | **Failed autosave never retries.** On a network error `performSave` restores `dirty` but schedules nothing; the `online` listener only toggles the banner. Edits sit unsaved until the next keystroke or manual Sync. On mobile the OS then kills the tab and they're gone. | `docs/js/app.js:1286-1302`, `190-191` | Schedule an exponential-backoff retry on failure; call `triggerSave()` from the `online` handler and from `visibilitychange` → visible when `dirty`. |

### P1 — user-visible bugs / parity breaks

**Status (2026-09-10): B5, B6, B13 fixed** in the working tree (7 new parity tests, 84/84 passing). B5: `renderEditor`
locks title/due/priority/repeat/tags/body when `IsDone || IsClosed` with a notice; Done stays live unless trashed, Pin
always live (matches `TaskDetailViewModel`); Trash rows hide the Done checkbox; the editor Done checkbox now re-renders
(also closes B19). B6: `spawnIfRecurring` shared by single and bulk completion, spawned tasks folded into bulk undo.
B13: `permanentlyRemoveTasks` is the single path for delete/Empty Trash/bulk delete/auto-empty and deletes attachments
from Drive unless another remaining task still references the file (`collectTaskFileNames` in `model.js` mirrors
`TaskMediaHelper.CollectReferencedFileNames`).

**Status (2026-09-10): B7, B8, B16, B17 fixed** in the working tree (4 new parity tests, 88/88 passing). B7:
`refreshAccessToken` clears the session on 401 only; 429/5xx/network failures keep it, retry once (honouring
`Retry-After`), then fall back to the 60 s loop, and concurrent callers share one in-flight request; `getAccessToken`
throws `AUTH_UNAVAILABLE` (shown as a temporary outage, not "Signed out") when the session survived a failed refresh.
B8: `driveFetch` on 401 calls `invalidateAccessToken()` and replays the request once through a silent refresh before
throwing `NOT_SIGNED_IN`; Sync Now no longer redirects when a refresh session exists. B16: the three quick-add regexes
use a captured `(^|\s)` prefix instead of lookbehind; new classic-script `docs/js/boot-guard.js` turns a module parse
failure (or 8 s without boot) into a "Tasky couldn't start… needs Safari/iOS 16.4+" message, plus a `<noscript>`; minimum
browser documented in README. B17: new `docs/js/storage.js` (`storage` / `sessionStore` guarded helpers) used by
`app.js` and `auth.js` for every localStorage/sessionStorage access.

**Status (2026-09-10): B9–B26 fixed** (B13/B16/B17/B19 earlier; 9 new parity tests, 97/97 passing). B9: top
safe-area inset on `.app-header`, `.offline-banner`, `.signin-screen`. B10: `plaintext-only` detected by reading the
property back; plain-text paste fallback via `execCommand('insertText')` where unsupported. B11: `!due:` accepts only
`yyyy-mm-dd`, `yyyy/mm/dd`, `m/d/yyyy`, `d.m.yyyy` with real-day validation. B12: `captureBodyFocus`/`restoreBodyFocus`
keep focus + caret on the checklist add-row (and item inputs) across body re-renders. B14: `handlePhotoPick` seeds the
IndexedDB thumbnail cache from the picked file. B15: `normalizeTask` on load and on remote merge input;
`deduplicateTombstones` skips records without TaskId/Timestamp. B18: `showMobileView` only pushes history under 767 px.
B20: quick-add popup pinned to the top of the viewport on phones (`.quick-add-popup.at-top`). B21: new tasks inherit
the tag section's tag or Today's due date and stay in the section; switch to All Tasks (with a status note) only when
not visible there. B22: `buildEffectiveSearchQuery` mirrors desktop's `BuildEffectiveSearchQuery` (chip + tag section +
text); Save-view button enabled whenever any scope is active. B23: `taskHasLink`/`taskHasChecklist` in `model.js`
mirror `TaskMediaHelper` for the row badges, the Has Link chip and `has:link`. B24: sign-out uses `confirmModal` when
dirty. B25: two `theme-color` metas keyed to `--pane-bg`, kept in step by `updateThemeColorMeta()`; manifest colours
`#f3f4f7`/`#ffffff`. B26: Escape also closes Sort/Filter, bulk Add Tag, tag suggestions and the quick-add popup.

**Status (2026-09-10): U1, U2 fixed** (2 new parity tests, 99/99 passing). U1: `docs/sw.js` now precaches the app
shell (cache name keyed to the shared `?v=NN` literal so the bump/check scripts cover it; network-first for the page
and unversioned files, cache-first for `?v=` assets, cross-origin untouched); new `docs/js/snapshot.js` keeps one
IndexedDB copy of AppState + file/folder ids + `dirty`, written 500 ms after each edit, after each sync and on
`visibilitychange: hidden`; boot falls back to it when Drive/sign-in are unreachable (`restoreFromSnapshot`, also when
the token cache expired offline but a refresh session exists) and a dirty copy from a killed session is reconciled
against the fresh download via `reconcileLocalSnapshot` (3-way merge, `(conflicted copy)` on collisions) and uploaded;
reconnect triggers a forced sync; sign-out clears the copy. U2: `savePlace`/`restorePlace` remember section, task and
mobile view in localStorage and mirror the open task as `#task=<id>`, which is also honoured as a deep link at boot.

**Status (2026-09-10): U3–U8 fixed** (99/99 parity tests passing; UI-only, no new unit tests). U3: first launch on a
phone opens Today (`restorePlace` default when nothing is remembered). U4: tab bar is Today · All · Completed · More,
with Recurring and Trash moved into the More sheet. U5: due pill shows a relative label (`formatDueLabel`: Today /
Tomorrow / Yesterday / weekday / "Sep 24", year only when different) over an invisible native date input, a time pill
appears once a date is set, × clears the date; task rows use the same label. U6: tap-to-open lightbox that fetches the
original (`openPhotoLightbox`), "+ Take Photo" (`capture="environment"`, touch only), and Settings → Photos → shrink to
2048 px before upload (`prepareUploadImage`, default on for touch). U7: exports and file attachments go through
`navigator.share({ files })` on touch devices (download elsewhere); new More → Share Task sends title + Markdown body,
copying to the clipboard where there's no share sheet. U8: header `#sync-indicator` (synced / pending / saving / error /
offline / local) driven by `setSyncState`; routine status text is `.save-status-quiet` and hidden from the mobile
bottom bar, which is now errors and reconnect prompts only (tap the icon to peek).

| # | Finding | Where | Fix |
|---|---------|-------|-----|
| B5 | **No read-only lock for Completed / Trashed tasks.** Desktop: `IsEditable => !IsDone && !IsClosed`. Web's `renderEditor` never disables anything, and the comment at 672 refers to "the editor's IsClosed gating" that doesn't exist. Editing a trashed task bumps `ModifiedAt`, which both platforms use as the "trashed at" proxy for auto-empty-trash, so the 30-day clock silently resets. Trash rows also still show the Done checkbox. | `docs/js/app.js:2329-2372`, `672` | Mirror Desktop: title/body/pills read-only when `IsDone || IsClosed`; keep Done toggle live for merely-Done tasks, lock it in Trash (`CanToggleComplete`). Hide the row Done checkbox in Trash. |
| B6 | **Bulk "Mark Done" skips recurrence.** It sets `IsDone = true` directly, so recurring tasks completed via multi-select never spawn the next occurrence. Desktop spawns from `Task_PropertyChanged` on any `IsDone=true`. | `docs/js/app.js:2640-2657` vs `ViewModels/MainViewModel.cs:2101-2107` | Route through a shared `completeTask(task)` that spawns, and include spawned tasks in the undo closure. |
| B7 | **Silent token refresh treats *any* non-2xx as a dead session.** A 429 from the rate limiter or a 500/cold-start from the Cloud Function clears `sessionId`, forcing a full re-consent. Only 401 means the session is gone. | `docs/js/auth.js:155-160` | Clear session on 401 only; on 429/5xx keep it and retry with backoff. |
| B8 | **Drive 401 goes straight to "Signed out" without trying a refresh.** A token Google rejected early (clock skew, early invalidation) shows the Signed Out modal even though a refresh session exists that would fix it in one call. | `docs/js/drive.js:16-23` | On 401: set `tokenExpiresAt = 0`, `await refreshAccessToken()`, retry the request once, then throw `NOT_SIGNED_IN`. |
| B9 | **iOS installed PWA: header sits under the status bar / notch.** `black-translucent` + `viewport-fit=cover` are set but no rule uses `env(safe-area-inset-top)` (only `-bottom`). | `docs/index.html:5,32`, `docs/css/styles.css` (no top inset anywhere) | `.app-header { padding-top: calc(8px + env(safe-area-inset-top)) }`, same for `.offline-banner` and `.signin-screen`. |
| B10 | **`plaintext-only` detection is always false.** `'plaintext-only' in div.style` tests a CSS property name, so every browser falls back to `contentEditable='true'`. Pasting from Gmail/Docs inserts styled HTML into notes; Enter creates nested `<div>`s. | `docs/js/editor.js:115-120` | Set it, then check `div.contentEditable === 'plaintext-only'`. Add a generic paste handler that inserts `text/plain` via `insertText`. |
| B11 | **Quick-add `!due:` fallback parses garbage.** `new Date(token)` in Chrome turns `!due:march` into Mar 1 2001 and `!due:5` into Jan 1 2005 (Firefox rejects both; Desktop's `DateOnly.TryParse(Invariant)` rejects both). | `docs/js/model.js:297-298` | Only accept explicit numeric forms (`yyyy-mm-dd`, `m/d/yyyy`, `d.m.yyyy`); otherwise leave the token in the title like Desktop. |
| B12 | **Adding a checklist item closes the mobile keyboard.** Commit triggers a full body re-render, recreating the "+ Add item" input and dropping focus. Every item = tap the field again. | `docs/js/editor.js:272-277`, `66-91` | Remember which input had focus (data attribute) and refocus after re-render, or patch the checklist DOM in place. |
| B13 | **Permanent delete / Empty Trash / bulk delete / auto-empty leave attachments on Drive.** Desktop's `AutoEmptyTrashIfNeeded` calls `CleanupTaskAttachments`; Web only cleans up on single block removal (#139). Web-only users accumulate orphans forever. | `docs/js/app.js:1616-1668`, `2852-2866` | On any permanent removal, call `deleteAttachmentBlob` for each Photo/File block filename and inline `UriSource`/`Tag` references (fire-and-forget, like #139). |
| B14 | **Freshly picked photo isn't cached, so it can show "not found on Drive".** Only `loadPhotoBlob` writes to IndexedDB. Switch task and back within Drive's ~2 s index lag → `findFileByName` misses → error caption. Also re-downloads the full-res file you just uploaded. | `docs/js/editor.js:782-798` vs `520-528` | At pick time run the file through `downscaleToThumbnail` + `putCachedThumbnail`. |
| B15 | **Load doesn't normalize task shape.** A pre-`Body` legacy Desktop file or hand-edited one with missing `Tags`/`Body` throws in `renderList` (`t.Tags.some`) and blanks the whole app; `deduplicateTombstones` throws on a record missing `Timestamp`. `applyTaskFields` guards, load doesn't. | `docs/js/app.js:886-890`, `docs/js/sync.js:19` | `normalizeTask()` on load: `Tags ??= []`, `Body ??= []`, `RecurrenceInterval ??= 1`, `Priority ??= 0`; skip tombstones without a Timestamp. |
| B16 | **Web can't launch on Safari < 16.4 and gives no error.** `model.js` uses regex lookbehind `(?<!\S)` at module scope - a parse-time SyntaxError there, which fails the whole import graph. The sign-in button's static text is "Loading Google Sign-In…" so the user sees that forever. Also relies on `crypto.randomUUID` (15.4+), `color-mix()` (16.2+), `:has()`. | `docs/js/model.js:220-222`, `docs/index.html:45` | Replace lookbehind with `(?:^|\s)` capture (identical semantics). Add a `<noscript>` and a 5 s JS timeout message ("Your browser is too old…"). Decide and document a minimum iOS version. |
| B17 | **Unguarded `localStorage.setItem` at module init.** `applyTheme()` runs at import; if storage throws (Chrome "block all cookies", some private modes) the app never boots. `auth.js` already wraps every access; `app.js` doesn't. | `docs/js/app.js:631,646,663,680,692,705,796,843` | One `storage.get/set` helper with try/catch. |
| B18 | **Browser Back cycles invisible states on desktop/tablet.** `showMobileView` pushes history on every view change at every width, but `data-view` only has a visual effect under 768 px. | `docs/js/app.js:3168-3175` | Only `pushState` when `matchMedia('(max-width: 767px)').matches`. |
| B19 | **Editor Done checkbox doesn't refresh the More menu.** Checking the Done pill leaves the overflow item reading "Mark Done". | `docs/js/app.js:2558-2563` | Call `renderEditor(task)` after `toggleDone` (as `editorDoneBtn` already does). |
| B20 | **Quick-add popup is probably hidden behind the mobile keyboard.** It's `position:fixed` anchored *above* the FAB near the bottom; the keyboard shrinks only the visual viewport, so a `bottom:` anchored element ends up under it. `scrollIntoView` can't move a fixed element. Not device-verified. | `docs/js/app.js:2977-2982`, `601-619` | On mobile anchor the popup to the top of the viewport, or reposition from `visualViewport.height` in the existing resize listener. |
| B21 | **Quick-add / New Task bounce you to All Tasks.** Adding from Today or a tag view switches the section. | `docs/js/app.js:1437,1458,1484` | Stay in the current section. In a tag section, auto-apply that tag; in Today, default `!due:today`. Toast "Added to All Tasks" only when the new task isn't visible here. |
| B22 | **Views can only be saved from typed search text.** Desktop v1.9.5 composes `tag:`/`is:`/`due:` from the active tag section and quick-filter chip; Web's `saveCurrentSearchAsView` requires `searchQuery`, so a tag scope or "Overdue" chip can't be saved. | `docs/js/app.js:1212-1220`, `1817` | Compose the query the same way Desktop does; enable the button whenever any scope is active. |
| B23 | **Link/checklist indicators miss Desktop content.** `has:link` and the row link icon only look at `Link` blocks; Desktop's `HasLink` also checks Rtf `<Hyperlink`/`NavigateUri` and `http`/`www.` in Text. Checklist icon ignores Rtf `<CheckBox`. Desktop-authored tasks show fewer badges and don't match the same searches on Web. | `docs/js/app.js:995,1041,2123-2126` vs `Models/TaskMediaHelper.cs` | Add `blockHasInlineLink`/`blockHasInlineChecklist` helpers in `model.js` mirroring `TaskMediaHelper`. |
| B24 | **Sign-out discards unsaved edits with only the generic browser prompt.** | `docs/js/app.js:518-521` | Reuse `confirmModal` like `confirmSignInIfDirty` does. |
| B25 | **`theme-color` / splash colours match neither theme.** Meta is fixed `#3861d6`; manifest `background_color` is `#1e293b` (neither `--bg`). Android's title bar stays blue in dark mode; splash flashes slate. | `docs/index.html:27`, `docs/manifest.json:8` | Two `<meta name="theme-color" media="(prefers-color-scheme: …)">`, updated from `applyTheme()`; set `background_color` to `#1f1f1f`/`#f3f4f7`. |
| B26 | **Escape doesn't close the Sort/Filter popup, bulk-tag popup or tag suggestions.** | `docs/js/app.js:579-586` | Add them to the Escape handler. |
| B27 | Stale comment: "the 10s autosave debounce" - it's 4 s. | `docs/js/app.js:1315` | - |

---

## 2. Mobile & UX improvements

Ordered by impact.

| # | Improvement | Notes |
|---|-------------|-------|
| U1 | **Offline launch + local snapshot (biggest mobile gap).** The pass-through service worker means the installed app shows a browser error page with no signal, and `appState` lives only in memory, so a killed tab loses unsaved edits. Middle ground with **zero wire-format change**: precache the app shell in `sw.js` (network-first, fall back to cache), snapshot `appState` to IndexedDB on every `markDirty`, boot from the snapshot when Drive is unreachable, and let the existing `mergeRemoteState` + `lastSyncTime` reconcile on reconnect. That is exactly Desktop's local-file-plus-sync model. Roadmap #6/#7 are deferred, but this alone would fix B4's data-loss tail and most "it just shows an error" mobile reports. | `docs/sw.js`, `docs/js/app.js` |
| U2 | **Restore your place after reload.** Mobile OSes reload PWAs constantly; today you land on the Sections list. Persist `selectedTaskId` / `currentSection` / view (sessionStorage or `#task=<id>` hash) and restore after `onSignedIn`. Also gives shareable/deep-linkable task URLs. | `docs/js/app.js:3191` |
| U3 | **Mobile opens on the wrong screen.** First view after sign-in is the sidebar (sections + tags). Open to Today (or the last-used section). | `docs/js/app.js:3191` |
| U4 | **Tab bar has Recurring and Trash but not Today.** Today is the highest-frequency mobile destination; Recurring/Trash are rare. Suggest Today · All · Completed · More (Recurring, Trash, Sections, Dashboard, Settings in More). | `docs/js/app.js:1924-1959` |
| U5 | **Due dates: no time, no relative labels, no way to clear on iPhone.** The model, quick-add `@3pm` and Desktop reminders all carry a time, but Web shows only "Sep 10, 2026" and the pill is a bare `<input type=date>`. iOS's date wheel has no Clear, so iPhone users can't remove a due date at all. Show relative labels (Today 3:00 PM · Tomorrow · Mon · Sep 24; drop the year when current), add a time pill when a date is set, and an explicit × on the pill. | `docs/js/app.js:3199`, `docs/index.html:262-266` |
| U6 | **Photos: no full-size viewer, no camera, no compression.** Desktop has `PhotoViewerWindow`; Web renders a 1024 px thumbnail and taps do nothing. Add a tap-to-open lightbox that fetches the original. On mobile add a "Take photo" entry (`<input capture="environment">`). A 12 MP phone JPEG is 3–6 MB; offer client-side downscale (e.g. 2048 px / q0.85) as a setting - default on for mobile, off for parity with Desktop's full-res behaviour. | `docs/js/editor.js:472-536`, `748-760` |
| U7 | **Share instead of download on mobile.** Exports and file attachments use `<a download>`, which is unreliable inside an iOS standalone PWA. Use `navigator.share({ files })` when available. Add a per-task "Share" (title + markdown body) via Web Share - the mobile equivalent of Desktop's Export/Print Note. | `docs/js/app.js:3340-3350`, `docs/js/editor.js:554-580` |
| U8 | **Save-status churn on mobile.** "Unsaved changes… → Saving… → Saved" flips in a fixed bottom bar on every keystroke. Replace with a small header cloud icon (synced / pending / error) and reserve the bar for errors and reconnect prompts. | `docs/css/styles.css:1327-1347`, `docs/js/app.js:1242-1247` |
| U9 | **Long-press a row to start multi-select** (standard on Android/iOS) instead of only the header toggle. Add `user-select:none; -webkit-touch-callout:none` to long-press targets (`.new-task-btn`, rows) so iOS doesn't show a text callout. | `docs/js/app.js:1967-2084`, `2999-3058` |
| U10 | **Checklist ergonomics.** Enter inside an item inserts a new item below and focuses it; Backspace on an empty item removes it; up/down (or a drag handle) to reorder. Pairs with B12. | `docs/js/editor.js:224-290` |
| U11 | **Declutter the meta row.** Priority "None" and Repeat "Never" always render as pills, and an empty due pill shows the browser's "mm/dd/yyyy". Show icon-only pills when unset (with a tooltip/label on tap). | `docs/index.html:253-303` |
| U12 | **Auto-discard untouched empty tasks.** Tapping New Task then navigating away leaves "(untitled)" tasks behind. Drop a task that's still empty and unedited when it's deselected. | `docs/js/app.js:1434-1445` |
| U13 | **Persist sort key and last section** in localStorage (Desktop persists these in Settings). | `docs/js/app.js:286-287` |
| U14 | **Accessibility.** Modals lack `role="dialog"`, `aria-modal`, focus trap and focus return; task rows are `role="button"` containing interactive checkboxes (invalid ARIA - use `role="listitem"` + a real open button, or `aria-hidden` the checkbox from the row's name); row `aria-label` omits done/pinned/tags; `prefers-reduced-motion` not honoured for the sheet/spin animations. | `docs/js/app.js:1967-2031`, `docs/css/styles.css:1519-1522` |
| U15 | **iPad detection.** `isIos` uses the UA; iPadOS 13+ reports as Mac, so iPad gets the Android/no-prompt path and never sees install instructions. Check `navigator.maxTouchPoints > 1 && /Mac/.test(platform)` too. | `docs/js/app.js:3367` |

**Status (2026-09-10): U9–U15 fixed** (99/99 parity tests passing; UI-only, no new unit tests; cache-bust still v23). U9: `bindSwipeGesture` now also runs a 500 ms hold timer (same 10 px jitter allowance as the quick-add long-press, now the shared `LONG_PRESS_MOVE_CANCEL_PX`) - a hold that neither scrolls nor swipes enters selection mode with that row checked, cancels the swipe tracking, suppresses the click iOS fires on release (cleared on the next touchstart so Android, which fires none, doesn't lose the next tap), and blocks Android's contextmenu only while a finger is down; rows and the New Task button get `user-select: none; -webkit-touch-callout: none`. U10: checklist item inputs take Enter (split at the caret into a new item below, so Enter at the end is "next item"), Backspace on an empty item (remove, caret to the end of the previous one) and Alt+↑/↓ (reorder); Gboard's `insertLineBreak` input event is treated as Enter like the add row already did; the caret is placed explicitly after the synchronous rerender (`focusChecklistItem`) since `captureBodyFocus` restores the *same* key, and the interesting input is a different row. Documented as two display-only rows in the shortcuts table. U11: Priority "None" and Repeat "Never" pills collapse to their icon (`.editor-field.unset`, the `<select>` stretched invisibly over the pill like the due fields) - the due pill keeps its "Due date" text since U5 already replaced the browser's mm/dd/yyyy and unset is its normal state. U12: `untouchedNewTaskIds` records tasks made by this session's `createTask()`; `discardUntouchedNewTasks()` drops any that are still completely empty (text, tags, due, priority, repeat, pin, body) when another row is opened, the editor view is left on a phone, a section is chosen, the dashboard is opened or New Task is pressed again - with a tombstone, since autosave may already have uploaded it. Existing empty tasks from other devices are never touched. U13: sort order persists under `tasky-sort` and the chip row reflects it at boot; the last section/task/view were already remembered by U2's `tasky-place`. U14: `dialog.js` exports `trapFocus(container, { initialFocus })` (Tab/Shift+Tab cycle inside, focus returned on release) used by `openDialog`, the Shortcuts and Onboarding modals and the photo lightbox, all now `role="dialog" aria-modal="true"` with `aria-labelledby`; task rows moved `role="button"`/`tabindex`/Enter-Space/`aria-label` from the `<li>` to the title block so the two checkboxes are siblings rather than children of a button, the saved-view sidebar item got the same split, and the row name now includes completed / in trash / pinned / priority / due (overdue) / tags; one `prefers-reduced-motion` block zeroes every animation and transition (sheet slide-up, both spinners, swipe snap-back). U15: `isIos` also matches a Mac platform with `maxTouchPoints > 1`. **Not verified on a device**: the long-press timing against iOS's own click-after-hold and Android's contextmenu, and the checklist caret placement with Gboard - both need a phone.

---

## 3. Feature parity gaps (Desktop → Web)

| Desktop feature | Web status | Suggestion |
|-----------------|-----------|------------|
| Read-only Completed/Trashed tasks | Missing (B5) | Implement |
| Bulk complete spawns recurrence | Missing (B6) | Implement |
| Save view from tag/quick-filter scope | Missing (B22) | Implement |
| Attachment cleanup on permanent delete | Missing (B13) | Implement |
| Due time-of-day (`@3pm`, reminders) | Not shown/editable (U5) | Implement |
| Photo full-size viewer | Missing (U6) | Implement |
| Export / Print single note | Missing | Web Share per task (U7) |
| Calendar view | Missing (roadmap #49) | - |
| Reminders / notifications | Missing (roadmap #68) | - |
| Rich text (bold/italic/etc.) | Missing by design | At minimum stop destroying Desktop's Rtf attachments (B1) |
| "High Priority" quick-filter chip | Deliberately omitted | Fine - `is:highpriority` covers it |

---

## 4. Code quality (P3)

- **`app.js` is a 3,461-line module-scope monolith** (roadmap #136 tracks the split). Quick wins that don't need the full refactor: move the four hand-built dialogs (`promptForLink`, `promptForViewName`, `confirmModal`, `showSignedOutModal`) onto one `openDialog({title, body, fields, actions})` helper; move bulk actions and quick-add into their own modules.
- **`escapeHtml` allocates a `<div>` per call** and runs per row per render. A regex replace is ~10x cheaper in the `updateTaskRow` hot path (`docs/js/app.js:3218-3222`).
- **`loadFromDriveWithRetry` has no retry** - rename or add one (`docs/js/app.js:874`).
- **`mergeRemoteState` returns counts only**; returning touched IDs is needed for B2 and is cheap.
- **Dark palette duplicated** in two CSS blocks (documented, `styles.css:41-95`). A tiny `node` script that generates the second block from the first (run alongside `check-cache-version.js`) would remove the drift risk without adding a build step.
- **Cloud Function CORS allows `http://localhost:5500` in production** (`functions/refresh-token/index.js:33`, same in exchange-token). Low risk given a `session_id` is still required, but gate it on an env var.
- **`accountBtn.innerHTML` interpolates the Google picture URL unescaped** (`docs/js/app.js:861`). Safe today (Google-controlled value) but use `createElement` for consistency with the rest of the file.
- **Two `document.keydown` listeners each check `Ctrl+/`/`F1`, `Ctrl+N`, `Ctrl+F`, `Esc`, `Ctrl+Z`** across five separate registrations. One dispatcher keyed by combo would make the shortcut table in the modal and the code the same source.

**Status (2026-09-10): all eight addressed** (99/99 parity tests passing, cache-bust still v23, `check-cache-version.js` and the new `check-dark-palette.js` both clean). Dialogs: new `docs/js/dialog.js` exports `openDialog({ title, message, fields, actions, variant, dismissValue })`; `promptForLink` (editor.js), `promptForViewName`, `confirmModal` and `showSignedOutModal` (app.js) are now 10–20-line calls into it, same CSS classes, same Escape / backdrop / Enter / focus behaviour. Bulk actions and quick-add were **not** moved into their own modules - both read and write a dozen module-scope bindings (`appState`, `currentTask`, `selectedIds`, `renderList`, `markDirty`, …), so that extraction is the roadmap #136 split proper, not a quick win. `escapeHtml` is a regex replace over `[&<>"']` (also escapes quotes now, which the DOM round-trip never did - relevant to the exported-HTML `href`). `loadFromDriveWithRetry` renamed `loadFromDrive` (retry lives in the U1 local-copy path). `mergeRemoteState` already returned `updatedIds`/`removedIds` since the B2 fix. `check-dark-palette.js` compares the two dark blocks property-by-property, `--fix` regenerates the forced block from the media block (verified byte-identical on the current file), wired into `.github/workflows/build.yml` and referenced from the CSS comment. Cloud Functions: `http://localhost:5500` only joins `ALLOWED_ORIGINS` when `ALLOW_LOCAL_DEV_ORIGIN=true` (documented in exchange-token's header) - **set that on any deployment you use for local dev, or local sign-in stops working**. Avatar built with `createElement`/`replaceChildren`. Keyboard: one `SHORTCUTS` table + one `keydown` dispatcher replaces the five listeners; `renderShortcutsList()` builds the modal's rows from the same table (`#shortcuts-list` in index.html). Side fix: bare `/` no longer also fires on Ctr+/ (both listeners used to claim it, so Ctrl+/ focused search *and* opened the help).

---

## 5. Suggested order of work

1. **B1, B2, B3, B4** - the four data-loss paths. Small diffs, high value.
2. **B5, B6, B13** - parity breaks that also affect Desktop users' data (auto-empty timing, recurring series, orphaned files).
3. **B7, B8, B16, B17** - sign-in robustness (spurious sign-outs, Safari boot failure).
4. **B9, B10, B11, B12, B14, B15, B18–B26** - one polish batch.
5. **U1 + U2** - offline snapshot and place-restore; the biggest mobile experience change and the one that makes the PWA feel like an app.
6. **U3–U8** - mobile navigation/date/photo/share improvements.


---

## 6. Follow-up pass (2026-09-18): recommendations and new features

Reviewed live in guest mode at desktop and 375 px phone widths. Everything below is implemented
(cache-bust v31 → v32; 172 parity tests and 350 desktop tests passing).

**Bugs**
- Web never pulled other devices' changes unless it had its own edit to save → `pullRemoteChanges`
  (app.js): every 3 min while visible, on returning to the app, and on pull-to-refresh. One
  `files.get?fields=modifiedTime` request; downloads + merges only when the file changed since this
  device last read or wrote it (`uploadFileText` now returns `modifiedTime`).
- Long checklist items were cut off on phones (single-line `<input>`) → auto-growing `<textarea>`,
  re-fitted by a ResizeObserver.
- "Loaded N task(s) (Local Mode)" / "Saved locally" were missing from `QUIET_STATUS_RE`, so they
  showed in the phone's bottom bar.
- Search placeholder was truncated on phones → "Search tasks" (operators stay in the tooltip / F1).
- Status text had no live region → `role="status" aria-live="polite"`.
- **Web-edited notes lost their formatting on desktop.** The regex `htmlToXaml` emitted bare text
  directly inside `<FlowDocument>` (Chrome's "line<div>line</div>" after Enter), passed `&nbsp;`
  through and kept unknown tags - all rejected by `XamlReader.Parse`. Replaced by a small tolerant
  HTML tree parser (model.js `parseHtmlFragment`) that always emits well-formed XAML (escaped
  `<Run Text=…/>` runs, every inline inside a Paragraph). `xamlToHtml` output is now allow-list
  sanitised. Desktop tests (`NoteFormattingTests.WebEditorXaml_*`) parse the exact shapes the web
  emits.

**UI**
- Phone task view: metadata pills are one horizontally scrolling row; tags moved to their own row.
- Remove (×) buttons hidden until hover/focus with a mouse; on touch only while editing that text
  or checklist item (photos/files/links keep theirs).
- "+ Text / + Checklist / …" row replaced by one labelled **Insert** menu; `/` in a Text block opens
  the same menu (with type-to-filter) and inserts at that spot.
- Phone list: section title + count header that shrinks on scroll; the duplicate inline add row is
  hidden (the floating + remains).
- Empty Views sidebar shows a hint instead of a bare heading.
- Command palette (`Ctrl+K`).

**Features**
- Plain-language quick add on both platforms (model.js `parseNaturalTail` / QuickEntryParser.cs
  `ParseNaturalTail`, shared test vectors): trailing `tomorrow 3pm`, `next fri`, `in 2 weeks`,
  `tonight`, `every monday`, `every 2 weeks`, `daily` … Only the end of the title is read.
- Rich text on the web: formatting bar (B/I/U, lists, link, clear), always-rich text blocks, and
  desktop's inline checklist boxes round-trip as tappable ☐/☑ tokens.
- **Upcoming** section (sidebar + tab bar: Today · Upcoming · All · More): Overdue / Today /
  Tomorrow / Next 7 days / Later (`agendaGroup`).
- Quick reschedule: "Move all to today" (Today banner, Upcoming's Overdue heading) and
  "Move to Tomorrow" (task ⋮), each one undo step.
- Reminders (Settings → Reminders): notifications while open, same due rule as desktop
  (`isReminderDue`), once per due date; tapping one opens the task (sw.js `notificationclick`).
- **Add to Calendar** (task ⋮): single-task .ics with RRULE and an alarm (`taskToICalendar`).
- Android share target (manifest `share_target` + sw.js `receiveShare`): shared text/links/photos
  become a task. App-icon badge (overdue + due today) and Today/Upcoming home-screen shortcuts.
- Checklist drag-to-reorder by grip handle; short vibration on tick (Android).

**Not done, by design**
- True push reminders when Tasky isn't running need a server (Web Push + a scheduled Cloud
  Function); Add to Calendar covers that case for now.
- Indented checklist sub-items would need a new field in the shared `.tasky` format.
