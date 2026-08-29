# Tasky Ecosystem Roadmap

Closing, fully-agreed roadmap for Tasky Desktop (WPF/.NET 9) and Tasky Web (PWA at `docs/`), produced
by reconciling an independent Claude code review against a parallel Gemini/Antigravity review across
several rounds. Every item was checked against the actual source; items that turned out to already be
implemented were removed, and several were re-scoped to match what the code actually needed.

Live, filterable version (search, status/platform/priority filters, expandable context): https://claude.ai/code/artifact/d1cad8b5-5463-485b-9a6c-9e8f8f3f3584 — regenerated from this file by `roadmap-artifact/build.js` and republished whenever the table below changes (Claude does this automatically after editing the Status column; see `roadmap-artifact/build.js`'s header for the manual steps).

**How to hand this to a new session (Claude, a local model, or Antigravity):** paste this whole file, or
just the relevant row(s) by rank number, plus the specific ask (e.g. "implement #43 from ROADMAP.md").
Numbering is stable — don't renumber this file, other documents and prior conversations reference these
ranks directly.

## Status

150 total items. 77 active, 12 deferred/resolved, 61 done (see table — `Status` column).

**2026-08-29, v1.9.5 live-reported bug batch:** eight issues reported live in one session, all
fixed and shipped together. Desktop: filter-chip row overlapping the empty-state message when a
filter had no matches (a `Grid.Row` mistake); "Save current search as a view" required typed
search text, so a tag-only or quick-filter-only scope couldn't be saved (`SaveViewCommand` now
composes an equivalent `tag:`/`is:`/`has:`/`due:` query from whatever's active, matching Tasky
Web's own operator syntax, plus a new `is:highpriority` operator added to both platforms so it
round-trips); the quick-filter dropdown's items only reacted to hover on the tiny checkbox glyph,
not the full row like every other menu; right-clicking a text selection to apply Bold/Italic
cleared the selection first (`RichTextBox.CaretPosition`'s side effect); About Tasky had no path to
replay the welcome tour (Tasky Web's About popup does); no Settings toggle to hide the row/editor
"Mark Done" checkbox (Tasky Web has one). Web/mobile: the row and editor "Done" checkbox rendered
as the browser's native square control instead of matching desktop's filled-circle shape; a race
in `saveToDrive()` where the very first save on a device (nothing found on initial load) could
create a second `Tasky.tasky` if another device's file appeared in the gap before that first save
fired, since nothing re-checked immediately before creating - closed with a re-check mirroring what
desktop's `SyncCoordinator` already does on every sync. The trickiest one took three follow-up
rounds after live reports of regressions: three separate sidebar `ListBox`es (Sidebar/Tags/Views)
sharing one bound `SelectedSidebarItem` turned out to have both a stale-highlight problem (WPF
didn't reliably clear a list's `IsSelected` once the shared value pointed elsewhere - fixed by
rendering the highlight from a `MultiBinding`/`ObjectsEqualConverter` comparison instead of
`Selector.IsSelected` at all) and, independently, a can't-click problem (re-clicking an item a list
still privately believed was selected fired no `SelectionChanged` at all - fixed with a
`MouseBinding` → `SelectSidebarItemCommand` that reaches the view model directly on every real
click, regardless of the `Selector`'s own internal state). Verified: desktop build clean, 225/225
C# tests, 75/75 JS parity tests, `check-cache-version.js` clean; sidebar-selection fix confirmed
live by the user after the third round (couldn't be verified with automation - the dev build isn't
Start-Menu-registered, so this session's computer-use tooling couldn't attach to it).

**2026-08-28, Check for Updates self-update (#150):** user asked for a lighter alternative to a
full installer/auto-updater specifically to avoid repeat SmartScreen prompts and a code-signing
cert. New `Services/UpdateService.cs` checks GitHub Releases for a newer `-win-x64.zip` than the
running version; `Help → Check for Updates` triggers it manually, and an opt-out
(`Settings.AutoCheckForUpdates`, default on) once-a-day silent background check runs after
`MainWindow.Loaded`. `UpdateAvailableWindow` (non-modal) shows the release notes and, on "Download
& Install," stages the zip in `%LocalAppData%\Tasky\update-staging` - fetched via `HttpClient` and
extracted by this already-running, already-trusted process rather than a browser download, so no
Mark-of-the-Web is applied and no fresh SmartScreen prompt appears. Since a running `Tasky.exe`
can't overwrite its own file, "Relaunch Now" launches a small generated PowerShell script (same "a
running .ps1 can delete its own file" trick `Uninstall-Tasky.ps1` already relies on) that waits for
the process to exit, retries copying the staged files over the install folder for up to 20s,
relaunches `Tasky.exe`, then deletes the staging folder and itself - the app's normal
`MainWindow.Close()` path (autosave flush, Drive sync) still runs first, unchanged. A staged-but-
not-yet-applied download ("Later") is picked back up on the next launch with no re-download.
Verified: the swap/relaunch mechanics end-to-end against decoy executables in an isolated test
(notably, a running exe's file content turned out *not* to be reliably lock-protected against
`Copy-Item` on this Windows build - confirmed empirically rather than assumed - so the design's
real safety margin is waiting for actual process exit before copying, not relying on the OS to
block a premature write); the GitHub API's JSON field names and the real `v1.9.3` release zip's
flat file layout against the live repo; the dialog's three states rendered and visually checked in
dark theme via the same offline WPF-rendering harness used earlier this session for a layout fix.
225/225 existing tests still pass (no new automated tests - this is OS process/file-timing and
live-network behavior, not app logic a unit test can exercise meaningfully).

**2026-08-28, Sign-in flash fix (#149):** fixed a real reported bug on both Web and the mobile
PWA - right after completing a real Google sign-in, the sign-in screen stayed visible (with an
active-looking "Sign in with Google" button) for however long the code-for-token exchange took,
before flipping to the app. Now shows a distinct disabled "Completing sign-in…" state during that
window instead. Web-only change (`app.js`'s `boot()`), verified live via a mocked slow token
exchange. Cache-bust bumped to v77.

**2026-08-28, Web Views sync (#148 part 2):** after the Desktop port below, user asked whether
Views now sync with Web/mobile - they didn't, Web's #82 implementation was still fully
localStorage-only. Moved Web's saved views onto `appState.SavedViews`/`DeletedSavedViewIds`
(PascalCase, matching Desktop's C# field names exactly - was lowercase `{id,label,query}` before),
added `mergeSavedViews` to `sync.js` (mirrors `SavedViewSyncMerge.Merge`), wired into
`mergeFromRemote`/`loadFromDriveWithRetry`, and a one-time migration for existing users' old
localStorage views. No Desktop changes needed - the wire format already matched. 6 new JS tests
(75/75 passing), cache-bust bumped to v75 and verified clean, and - unlike the Desktop-only
port below - **this half was live-verified in a real browser** (local static server, mocked
auth/Drive fetch): confirmed the legacy-localStorage migration actually ran and rendered a
migrated view, saved a new view through the real UI and watched it appear in the sidebar,
selected it and confirmed it's marked active, deleted it and confirmed it's gone. What's still
unverified is a real two-device Drive round-trip (needs real Google credentials + two live
clients, not possible here) and the Desktop-side manual check noted in the entry below. No
version bump or release yet - separate confirmations per established workflow.

**2026-08-28, Desktop Views parity port (#148):** ported Web's saved-search "Views" sidebar
section (#82) to Desktop, synced via Google Drive (`AppState.SavedViews`, not a device-local
`Settings` copy - user's explicit call) rather than Web's per-browser `localStorage`. Build clean,
full `TodoApp.Tests` suite green (225/225, including 6 new `SavedViewSyncMergeTests` covering
add/delete/tombstone/collision merge cases) and a thorough code self-review, but **not yet
interactively verified in a running instance** - I couldn't drive the native WPF UI here. Still to
manually confirm: save a search as a view from the filter popup, it shows up under a new "Views"
sidebar section and survives an app restart; selecting it loads its query into the search box and
filters correctly; deleting it via the sidebar context menu removes it and falls back to "All
Tasks". No version bump or release yet - separate confirmations per established workflow.

**2026-08-28, Desktop quick-fixes batch:** shipped 3 of 5 picked P1/P2, S/M-effort Desktop items -
#127 (non-blocking `AppLogger` via `Channel<string>` + background writer, flush-on-exit, demoted
hot-path log lines, `OpenLogFile` UI-dialog concern moved to the caller), #126 (batch
`SettingsStore` writes during a sync pass into one), #63 (150ms debounce on the search box's list
refresh, reusing the existing save-debounce `DispatcherTimer` pattern), #62 (cache/compile regexes
that were rebuilt on every call in `TodoStore` backup listing, quick-add parsing, and tag
sanitization). #118 (remove the hardcoded default OAuth client secret, rely on PKCE per RFC 8252) was also tried -
`DefaultClientSecret` removed, default auth sending an empty secret - but a live sign-in test came
back `invalid_request: client_secret is missing` from Google's token endpoint, so the secret was
restored and #118 stays Active/not-done pending a real fix (a gitignored file baked in at
release-build time, bigger scope than the original estimate - see the row's own note). The other
four build clean and the full `TodoApp.Tests` suite (219 tests) passes unchanged. No version bump
or release yet - separate confirmations per established workflow.

**2026-08-28, Web quick-polish batch:** shipped the four S/M-effort Web/mobile items picked over
the bigger architecture (#136) and test-infra (#71) bets - #52 (last remaining emoji glyphs → SVG
icons), #56 (first-run onboarding modal + sample tasks), #72 (manual PWA/Lighthouse-criteria audit:
description/apple-mobile-web-app meta tags, sized icon links), #82 (saved smart filters as pinned
sidebar "Views", reusing the existing search-operator engine). All four Web-only, no desktop touch.
Verified live via a local static server (`.claude/launch.json`'s `tasky-web` config) with a
temporary, fully-removed-afterward fetch mock simulating a brand-new Drive account (empty
folder/file list) to exercise the real `noRemoteFileYet` → onboarding trigger end-to-end, not just
code review: onboarding modal fired once, created 3 real tasks via `createQuickTask`, didn't
reappear on a later dismiss with the checkbox unchecked, and replayed correctly from the About
popup; saved a `tag:` search as a view, confirmed it appears under a new sidebar "Views" section,
reproduces the filtered list without touching the live search box, and deletes cleanly with
fallback to All Tasks; confirmed the emoji→icon swap via `renderEditableBody` directly (not-found/
error states render the real `image`/`paperclip` SVGs, no emoji left). 69 JS tests still passing
(`applySearch`'s signature change is additive; nothing in `parity.test.js` calls it directly). Cache-
bust bumped `?v=73` → `?v=74`.

**2026-08-25, Web M-effort batch:** shipped the four M-effort Core Feature/Performance/Storage items on the
Tasky Web list - #138 (non-photo file attachments), #142 (bulk multi-select actions), #67 (keyed-diff
renderList + single-pass section counts), #70 (persistent IndexedDB photo thumbnail cache). All four
verified live in-browser (a fake-but-valid cached auth token was seeded into localStorage to get past the
sign-in gate without real Google credentials, letting the app's real local-only code paths run end-to-end):
created/edited/searched/filtered tasks and confirmed the same `<li>` DOM node survives a title keystroke
(#67); ran the full multi-select → mark done → trash → restore → delete flow including the confirm modal
(#142); confirmed the "+ File" toolbar button and File-block rendering appear correctly (#138, upload/
download itself untestable without real Drive access); and exercised the actual IndexedDB
open/put/get/prune/delete functions verbatim in-browser, including forcing the 200-entry cap to prune the
oldest entry (#70). Desktop/tests untouched - `dotnet test`/`dotnet build` not needed. 55 JS tests still
passing. Web cache-bust bumped `?v=52` → `?v=53` (`check-cache-version.js` clean) since this touches
`app.js`/`editor.js`/`icons.js`/`index.html`/`styles.css`.

**2026-08-25, same-day fix:** user caught the #142 selection checkbox rendering permanently visible on
every row, multi-select on or off. Root cause: `.select-checkbox-wrap { display: none; }`'s default-hide
rule tied in CSS specificity with a later, unrelated `.checkbox-tap-target { display: inline-flex }` rule
(both one-class selectors - the later one in file order wins a tie), so the "hide by default" half silently
lost. A gap in the original verification pass let it through: it entered selection mode and confirmed the
*active* state was correct (higher-specificity rules there did win), but never checked the *default,
not-selecting* state's checkbox visibility - exactly where the bug lived. Fixed by scoping the hide rule to
`.task-list .select-checkbox-wrap` (two classes), which reliably beats the one-class rule regardless of
source order. Re-verified live, both states explicitly this time: default shows only the "mark done"
checkbox, selection mode shows only the select checkbox - never both. Cache-bust bumped again,
`?v=54` → `?v=55`.

**2026-08-25, same-day fix #2:** follow-up report - once the checkbox bug above was fixed, selection mode's
on/off state was still hard to tell apart at a glance on mobile (one small icon-background tint was the only
signal). Added two more reinforcing signals: the select-toggle button's icon swaps between the checklist icon
and an X ("Cancel selection") depending on mode, and both `.bulk-actions-row` and the whole `.task-list` pick
up an accent-tinted background while selecting. Verified live in both directions (entering/exiting selection
mode): icon, title/aria-label, and both backgrounds all flip correctly. Cache-bust bumped `?v=55` → `?v=56`.

**2026-08-25, Web Settings section:** discussing whether the per-row "Mark done" checkbox is still needed
now that mobile has swipe-to-done surfaced a real answer: keep it (it's the one-tap path for the common
single-task case, and the only discoverable/accessible option for anyone who hasn't learned or can't perform
the swipe gesture), but let it be turned off by anyone who has learned to swipe and wants the row space back.
Added a proper Settings modal (`#settings-modal`, opened via a new "Settings" item in the menu dropdown,
reuses the existing bottom-sheet-on-mobile `.modal-card` treatment) and moved the Theme and Text Size
controls into it out of the cramped dropdown, alongside a new "Show 'Mark done' checkbox on task rows" toggle
(default on, persisted to `localStorage['tasky-show-done-checkbox']`, applied via a `hide-done-checkbox` class
on `<html>`). Web/mobile-only by design - desktop has no swipe-to-done, so its row checkbox is never
redundant there, and this is Tasky Web's own settings surface, not a synced preference. Verified live:
toggle hides/shows the row checkbox correctly in both directions, survives a reload, doesn't affect the
separate bulk-select checkbox, and the moved Theme/Text Size controls still work from inside the new modal.
Cache-bust bumped `?v=56` → `?v=57`.

**2026-08-25, Settings/About: modal → dropdown:** discussing the new Settings modal's mobile bottom
sheet, asked whether swipe-down-to-dismiss should work - it didn't (pure CSS positioning, no gesture
wired up). Rather than build swipe-to-dismiss, converted Settings (and About, same reasoning) from
`.modal-overlay`/`.modal-card` into anchored `.dropdown` popups nested in `.menu-wrap`, the same idiom
already used for `menu-dropdown`/`account-dropdown` - consistent with the earlier decision to revert
`account-dropdown` away from the bottom-sheet treatment. Dismiss is "free": the existing
`closeDropdowns()`/outside-click/Escape plumbing already manages this, just added both new dropdowns to
its list and `stopPropagation()` on their trigger buttons so opening one doesn't immediately close
itself via bubbling. No swipe-gesture code needed. Verified live in the mobile viewport: Settings
(260px) and About (240px) both open anchored under the menu button and fit fully within a 375px
viewport, content renders correctly, outside-click and Escape both close them, and the moved
Theme/Text-Size controls still apply correctly from inside the dropdown. Cache-bust bumped
`?v=57` → `?v=58`.

**2026-08-25, same-day fix #3:** user caught the hamburger button needing two clicks to close once
Settings/About had been opened - clicking Settings/About hides `menu-dropdown` and shows itself, but
the hamburger button's own click handler only ever toggled `menu-dropdown` specifically, so with it
already hidden a click meant to close everything instead toggled it back open (the top-level list
reappeared, "asking me to select settings again"). Fixed by having the hamburger button treat
`menu-dropdown`/`settings-dropdown`/`about-dropdown` as one open/closed family: if any of the three is
open, a click closes all of them; only opens `menu-dropdown` if none were open. Verified live: hamburger
→ Settings → hamburger now closes in one click; the plain open/close toggle with nothing drilled into
still works too. Cache-bust bumped `?v=58` → `?v=59`.

**2026-08-25, one mobile overflow menu, not two:** user pointed out the header hamburger and the
bottom tab bar's "More" button were two separate overflow menus on phones doing overlapping jobs
("I feel like I have two menus doing the same thing") - the hamburger was never actually hidden on
mobile (only `sidebar-drawer-btn` was breakpoint-gated). Folded Settings/About into the More popup
(a thin `.dropdown-divider` separates the existing navigation items - Dashboard/Sections & Tags -
from the app items - Settings/About) and hid `.menu-wrap` entirely below the 767px breakpoint, so
mobile has exactly one overflow menu; desktop/tablet keep the header hamburger since they have no
tab bar. Since Settings/About are now opened from two different anchor points depending on viewport,
they moved out of being CSS-anchored under `.menu-wrap` into JS-positioned popups (new
`openAnchoredPopup()`, same `position:fixed` idiom as `#list-filter-row`/`#more-sheet-popup`) -
below the trigger if it's in the screen's top half (header), above-and-right if it's in the bottom
half (tab bar). Caught and fixed one bug before shipping: the anchor button's rect was being read
*after* `closeMoreSheet()` hid its parent popup, so the button had already collapsed to a
zero-size rect - reordered to capture the rect first. Verified live at both breakpoints: mobile
shows one More popup with the divider and all four items, Settings/About open positioned near the
tab bar and stay fully within the viewport; desktop still opens them from the hamburger, positioned
under it as before. Cache-bust bumped `?v=59` → `?v=60`.

**2026-08-25, lighter confirm dialogs:** user compared Delete Permanently/Empty Trash's confirm dialog
to the small toast completing a task gives and called the former "a big popup," asking whether they
could match. Asked whether to actually replace the confirm gate with an act-then-Undo flow like
completing a task, or just restyle it lighter - user chose to keep the confirm gate (deleting from
Trash is genuinely unrecoverable, unlike completing/trashing a task, which already get their own
undo-toast treatment) but wanted it visually lighter. New `.confirm-overlay`/`.confirm-card` (used
only by `confirmModal()` in app.js - About/Shortcuts/Add Link keep the heavier `.modal-overlay`/
`.modal-card` unchanged): backdrop opacity 0.5 → 0.25, card max-width 360px → 300px, padding
32px 28px → 16px 18px. Confirmed live the card's background color is now literally identical to the
undo-toast's (`rgb(38, 38, 41)` both) - they already shared the same `var(--pane-bg)` token, so
what actually changed is size/weight, not color. Stays a centered card at every width (not a mobile
bottom sheet like About/Shortcuts) - a yes/no decision reads better as a brief alert than a drawer.
Cancel/outside-click/Escape all still verified working. Cache-bust bumped `?v=60` → `?v=61`.

**2026-08-25, same-day fix #4:** user caught Settings closing on every click inside it - theme,
text size, and the toggle all bubble up as regular clicks, and the document-level listener that
closes dropdowns on outside-click doesn't distinguish "outside" from "inside" for anything not
explicitly excepted (matches how the old combined menu-dropdown behaved before this session, which
was apparently more tolerable there since selecting a theme reads as "done" - it stopped being fine
once Settings grew a toggle meant to be flipped mid-browse). Fixed with one `stopPropagation()`
listener on `#settings-dropdown` itself, catching every click inside it before it bubbles to
document, rather than patching each control individually. Verified live: theme, text size, and the
toggle all keep it open now; an actual outside click still closes it. Cache-bust bumped
`?v=61` → `?v=62`.

**2026-08-25, four-item mobile batch:** user reported four separate issues in one message.
(1) The header's back button showed up when viewing the Dashboard, which is a top-level destination
(reached from the brand logo/tab bar), not a drill-down with anywhere to go "back" to - root cause:
Dashboard reuses the editor pane's empty-state slot, and `data-view="editor"` alone couldn't
distinguish "viewing a task" from "on the dashboard." Fixed by toggling `navBack`'s own hidden state
directly in `showEmptyEditor()`/`renderEditor()` rather than relying on `data-view` alone.
(2) Restoring a task from Trash gave no undo toast (only trashing did, matching desktop on purpose) -
diverged from desktop here since restoring is reachable by the same accidental-swipe risk as
trashing and deserves the same safety net; `toggleTrash()` now pushes an undo entry both directions.
(3) Swipe direction meaning flipped between sections - in Trash, right was already the away/negative
action (Delete) and left positive (Restore), but everywhere else right was positive (Done) and left
negative (Trash) - asked the user to confirm the intended universal rule and implemented "right =
away/negative everywhere": swapped the active/completed sections' swipe mapping (right now trashes,
left now completes/undoes) and swapped the "always .danger on the left slot, always .safe on the
right slot" logic in `updateTaskRow` to match - Trash section itself needed no change, it already
followed this rule. (4) The swipe color scheme didn't fit the theme in dark mode: `--danger` (used
for the swipe-to-trash background) never got its own dark-mode value, unlike `--success` which
correctly brightened - `--danger` stayed exactly the light-mode red (`#d64545`) in dark mode while
`--success` properly shifted to `#6ac789`, an unmatched pair. Added `--danger: #e06666` to both dark
blocks, tuned to roughly the same lightness as dark `--success` so the two read as a matched pair
again. All four verified live (mobile viewport, fake-auth technique): back button correctly
hidden/shown, restore toast fires and undoes correctly, swipe panels show Trash(red)/Done(green) in
active sections and Delete(red)/Restore(green) in Trash unchanged, and the dark-mode `--danger` value
now resolves to `#e06666` instead of the light-mode `#d64545`. Cache-bust bumped `?v=63` → `?v=64`.

**2026-08-25/26, swipe mapping correction:** the previous batch's "right = away/negative everywhere"
rule (chosen from two offered options) turned out not to be what was wanted once seen live - user
pushed back with a screenshot and the exact desired mapping per section, stated as swipe *right*:
Today/All/Recurring → Done, Completed → Trash, Trash → Delete. This isn't either of the two options
originally offered - it's a third, per-section rule: right is always the *forward* step along the
task's lifecycle (Active → Done → Trash → gone), not a fixed direction-to-polarity rule. Reworked
both `onCommit` (in `buildTaskRow`) and `updateTaskRow` to branch on `currentSection.kind` three ways
instead of the two-way `inTrash` check: Today/All/Recurring keep the original, unmodified colors
(left slot=green/Done, right slot=red/Trash, no `.danger`/`.safe` needed); Completed and Trash both
now invert the defaults via `.danger`/`.safe` (same mechanism, different labels: Trash/Undo vs
Delete/Restore). `updateTaskRow`'s signature changed from a boolean `inTrash` to the actual
`sectionKind` string, since a two-state boolean can't express a three-way branch. Verified live via
actual simulated touch-swipe gestures (not just reading code) in all three groups: Today/All swipe
right → "Completed" toast (green, unmodified colors); Completed swipe right → "Moved to Trash" toast
(red slot showing "Trash"/green slot showing "Undo"); Trash panel labels unchanged (Delete/Restore).
Also worth noting for next time: confirmed via `gh api repos/.../pages/builds/latest` that GitHub
Pages was NOT lagging - the previous (wrong) mapping really was the live, deployed behavior, not a
caching artifact as first suspected. Cache-bust bumped `?v=64` → `?v=65`.

**2026-08-26, tag picker dropdown on Web:** reported live as "tag drop down is not working" - it
wasn't broken so much as never built: the web tag input (`editor-tag-input`) was always just a bare
text box, Enter-to-add only, no way to browse or click an existing tag. Desktop already has this
(`TaskDetailViewModel`'s `IsTagPopupOpen`/`FilteredAvailableTags`/`CanCreateNewTag`/
`SelectExistingTagCommand` - a "Select tags ▾" button opens a popup with a search box, a filtered
list of every tag used elsewhere that isn't already on this task, and a "+ Create "x"" row once the
typed text doesn't match anything). Built the same shape on web, adapted to not need a separate
toggle button: a new `#tag-suggest-popup` opens on focus (so tapping into an empty box browses
every available tag), narrows live as you type (substring match via the existing `allTags()`),
lets you click any suggestion to add it, and shows "+ Create "x"" once nothing matches - closes on
selection, Escape, or an outside click (added to the same containment-guarded document click
listener `list-filter-row`/`more-sheet-popup` already use, with the input itself excluded so
focusing it doesn't immediately re-close the popup it just opened). Also brought `addTag()` in line
with desktop's cleanup (`TrimStart('#')`) - typing "#urgent" now cleans to "urgent" same as desktop,
previously the literal "#urgent" text with a redundant leading `#` would've been stored (chips
already prepend their own `#` when rendering). Verified live end to end: focus shows every unused
tag, typing filters correctly, clicking an existing tag adds it, "+ Create" adds a brand-new one,
an already-added tag never reappears in its own task's suggestions, and the popup stays fully
within the mobile viewport. Cache-bust bumped `?v=65` → `?v=66`.

**2026-08-26, task editor layout redesign:** user flagged the mobile task editor as "feels busy and
a bit unorganized" (screenshot: checkbox+label, a bordered icon chip, and a bordered text button all
crammed into one row with three different visual styles; Due and Repeat each a full-width
labeled+bordered form row; an oversized-feeling empty content card below). Researched task-editor
UX patterns before touching anything (Todoist's task-view redesign, Material chip-vs-form-field
guidance, Google Tasks/Apple Reminders' compact icon-led due/repeat controls - see chat for sources)
and converged on the same actionable pattern from every source: group related metadata into one
scannable strip of compact pills instead of separate labeled form rows, and keep action buttons
visually consistent rather than mixing styles. Changes: (1) `.editor-actions` - Pin is now a plain
borderless `.icon-btn` (matches every other icon button in the app) and Trash/Delete lose their
border too (`.editor-actions .btn { border-color: transparent }`), pushed right via a spacer so only
the Done checkbox sits alone on the left; (2) Due and Repeat dropped their "Due"/"Repeats" text
labels for calendar/repeat icons and now share `.tag-chip`'s exact pill styling (same
background/radius/padding), merged into one `.editor-meta-row` alongside the tag chips and tag
input instead of three separate rows (`.editor-meta` + `.editor-tags-row`), so all task metadata
reads as one family of small scannable pills. The oversized-feeling empty content card itself
turned out to already be a normal, correctly-sized `.editor-body` (118px tall in testing) - a
legitimate "card on canvas" pattern, not a bug - so left unchanged; its size just read as
disproportionate next to the messier metadata section above it, which this fixes. Verified live via
DOM/computed-style inspection (no visual screenshot available in this session): borders gone from
Pin/Trash, icons render, Due/Repeat pills get the tag-chip background/radius, overdue still tints
red correctly, Pin's active state still visible, all in one merged row. Cache-bust bumped `?v=66` →
`?v=67`.

**2026-08-25, Web S-effort batch:** shipped the two S-effort items left Active on the Tasky Web list - #66
(haptic feedback on complete/swipe) and #141 (native `confirm()` replaced with the app's own modal style
everywhere). Both small, self-contained UX polish, no architecture changes. 55 JS tests still passing
(no assertions needed updating - these are DOM-coupled app.js changes with no pure-logic surface to test).

**2026-08-25, papercuts batch:** shipped the last 5 genuine bugs remaining in the backlog - #61, #132, #133,
#139, #140. All were minor/low-severity (leaks, cosmetic UI, storage growth), none were "critical" in the
sense of active data loss - the truly critical items were already covered by the earlier batches. Everything
still Active in this table now is either a big feature (E2EE, CRDT sync, Kanban, command palette, ...) or an
architecture refactor (#15, #136, DI container, MVVM toolkit migration, ...), not a bug. 206 C# tests + 55 JS
tests passing.

**2026-08-25 merge:** folded `review_tasks.md`'s architecture/code-quality review into this table. All of that
review's Critical/High items are resolved (5/5 desktop, 2/3 web — the remaining web one is #89, already tracked).
Its still-open Medium/Low items are now rows #118–142 below, so there's one prioritized list instead of two documents.
`review_tasks.md` itself stays as the detailed rationale/file-line backing for those rows — see each row's Context
for the short version and the cross-reference back to it.

**2026-08-25, next-10 batch, final:** shipped 8 of the 10 planned items. Batch 1 - #89, #119, #120, #121, #122 -
the data-integrity/correctness cluster (reminder and web due-date time-of-day bugs, a real sync-conflict
data-loss bug, search coverage gaps, and the JS/C# parity test suite that now guards all of it). Batch 2 -
#118 and #26 were swapped out for #58 and #124 (both credential-gated: #118 needs Google Cloud Console access
to rotate/replace a live OAuth client, #26 needs an Azure Trusted Signing subscription - neither available in
this environment, and not something to fake); shipped #58, #137, #124. #15 and #136 (the two remaining XL/L
"god object" decompositions) were explicitly deferred to a dedicated future session rather than rushed - no
clean seam, high blast radius, and no way to interactively click through either UI here to verify. All shipped
items: 202 C# tests + 51 JS tests passing, builds clean, both docs updated.

## The five gating decisions (all resolved)

1. **#12 Product positioning — Resolved: personal, single-user application.** Not a team product, at least for now. `#107` (team workspaces) deferred accordingly.
2. **#11 Monetization model — Deferred.** Focus stays on building the product first, not pricing it. `#87` (licensing/entitlement engine) deferred accordingly.
3. **#10 Platform architecture — Resolved: no framework migration.** Desktop stays WPF, Web stays vanilla JS/PWA. `#98` (native mobile apps via MAUI/Flutter) deferred accordingly — a shared compiled core was the only thing that made native apps cheap, and that's off the table now. `#51` (Mica/Acrylic theming) stays **unblocked** — WPF can get it via DWM interop, no WinUI3 needed. The C#/JS logic duplication itself is still worth managing via `#14`, `#89`–`#91`, independent of this decision.
4. **Offline PWA support — Deferred (2026-08-24): Tasky Web does not need to work offline.** Google Drive is treated as always-reachable when using the web app. `#6` (service worker) and `#7` (IndexedDB offline cache) deferred accordingly, along with the items that only make sense once local edits can happen without a connection: `#41` (offline draft conflict banner) and `#106` (Background Sync API replay). `#37` (PWA install banner) is **not** deferred outright, but note Chrome/Android's install-eligibility check wants *some* registered service worker with a fetch handler present (even a no-op one) before `beforeinstallprompt` will fire - worth revisiting if #37 gets picked up. `#70` (thumbnail caching in IndexedDB) also stays active since it's a performance optimization independent of offline availability, though it'll need to stand up its own IndexedDB usage from scratch now that #7 isn't providing it.
5. **#29 Subtask hierarchy — Deferred (2026-08-25): user decided against building subtasks.** Raised while scoping `review_tasks.md`'s "foundational feature gaps" item (which had bundled #29 with #30, priority); user explicitly declined subtasks specifically, independent of #30. No dependent items to cascade — nothing else on this table assumes task hierarchy exists.

## Master table (1–150)

| # | Recommendation | Platform | ⚡ | Pri | Effort | Category | Status | Context |
|---|---|---|---|---|---|---|---|---|
| 1 | Remove OAuth client secret from binary | Desktop | | P0 | M | Security | **Done** | PKCE was already active (confirmed in `Google.Apis.Auth` 1.75.0). Old Desktop + Web OAuth client secrets rotated and disabled/deleted in Cloud Console; new Desktop secret in `GoogleDriveService.cs`, new Web secret set as the `exchange-token` Cloud Function's env var (never in source). |
| 2 | Encrypt stored Drive OAuth token with DPAPI | Desktop | | P0 | M | Security | **Done** | `DpapiFileDataStore` replaces `FileDataStore` in `GoogleDriveService.cs`; old plaintext tokens fail closed to a one-time re-login. |
| 3 | Encrypt user-supplied Drive secret with DPAPI | Desktop | | P0 | S | Security | **Done** | `SecretProtector` + `Settings.GoogleDriveClientSecretProtected`; `SettingsStore` encrypts on save, decrypts on load, migrates legacy plaintext. |
| 4 | Fix WCAG AA contrast failures in themes | Desktop | | P0 | S | Accessibility | **Done** | `LightTheme.xaml`: `ForegroundBrush`/`PlaceholderBrush` #646970→#575B61, `TertiaryBrush` #A7AAAD→#5D5F60. Verified ≥4.5:1 against every background these are actually painted on (white, window/sidebar tint, tag chips, selection highlight), not just white. |
| 5 | Restore keyboard focus visibility on note editor | Desktop | | P0 | S | Accessibility | **Done** | `ControlStyles.xaml`: `FocusVisualStyle="{x:Null}"` replaced with a custom `RichTextBoxFocusVisual` (thin `AccentBrush` outline) instead of the suppressed default. |
| 6 | Implement Service Worker (sw.js) for PWA | Web | | P0 | M | Offline PWA | **Deferred** | Register `sw.js` to cache shell & enable offline launch. Deferred - Tasky Web doesn't need offline support (gating decision #4). |
| 7 | Add IndexedDB offline local storage cache | Web | | P0 | L | Data Engine | **Deferred** | Implement IndexedDB (idb-keyval) to persist tasks offline beyond memory. Deferred - Tasky Web doesn't need offline support (gating decision #4). |
| 8 | Dynamic 100dvh viewport & keyboard handler | Web | | P0 | M | Mobile UX | **Done** | `styles.css`: `100dvh` progressive enhancement on `html`/`body`/`.app`, `body.keyboard-open` hides the fixed tab bar + save-status bar. `app.js`: `visualViewport` resize listener (150px threshold) toggles that class and scrolls the focused field into view. Verified via simulated viewport-resize events (no real device keyboard available to test with). |
| 9 | Stand up GitHub Actions CI pipeline | Both | ⚡ | P0.5 | M | CI/CD | **Done** | `.github/workflows/build.yml`: `desktop` job (windows-latest, dotnet build+test, 118 tests) and `web` job (JS syntax-check via `node --check` + `check-cache-version.js`) - no JS test framework exists yet, so that job checks what's actually there rather than inventing test coverage; Playwright suite is separately tracked as `#71`. |
| 10 | Platform architecture: staying WPF + vanilla-JS PWA | Both | ⚡ | P0.5 | XXL | Strategy | **Resolved** | No framework migration. Desktop stays WPF, Web stays vanilla JS. #98 deferred accordingly. |
| 11 | Monetization model | Both | ⚡ | P0.5 | M | Strategy | **Deferred** | Focus stays on building the product, not pricing it, for now. |
| 12 | Product positioning: personal application | Both | ⚡ | P0.5 | M | Strategy | **Resolved** | Tasky stays a solo, single-user tool for now. #107 deferred accordingly. |
| 13 | Reconcile docs' claimed architecture with code | Desktop | | P0.5 | S | Documentation | **Done** | `CONTRIBUTING.md`'s "Dependency Injection" section described `ITodoStore`/constructor injection as an existing convention when none of it exists (`MainViewModel` has a parameterless constructor, no service interfaces, no DI container) - rewritten to describe the actual current pattern and point at `#17`/`#18` as the future direction. Dropped the PR checklist item that would've gated PRs on a pattern the codebase doesn't follow yet. |
| 14 | Automated C# to JS model code generation | Both | ⚡ | P1 | M | Parity | Active | Generate `model.js` types directly from `TaskItem.cs`. |
| 15 | Decompose MainViewModel god object | Desktop | | P1 | XL | Architecture | Active | Split 1,911-line class into SyncCoordinator, UndoManager, etc. **In progress**: the 2026-08-25 review extracted `Services/SyncCoordinator.cs` and `Services/ReminderScheduler.cs` (1,905 → 1,685 lines), the two highest-value pieces per the review's own ranking. `FileSessionManager` (Open/Save As/New/restore) deliberately left in place — no clean decision-vs-apply seam like sync/reminders had, and file-load bugs are high-blast-radius; still the natural next slice here. |
| 16 | Decompose MainViewModel command wiring | Desktop | | P1 | L | Architecture | Active | Extract command-init methods into dedicated controllers. |
| 17 | Build service interfaces (ITodoStore, etc.) | Desktop | | P1 | L | Architecture | Active | Introduce interfaces for unit-test seams. |
| 18 | Wire up a DI container (IHost) | Desktop | | P1 | L | Architecture | Active | Register services via `Microsoft.Extensions.DependencyInjection`. |
| 19 | Migrate MVVM to CommunityToolkit.Mvvm | Desktop | | P1 | L | Architecture | Active | `[ObservableProperty]` / `[RelayCommand]` source generators. |
| 20 | Extract IFileDialogService & IMessageBoxService | Desktop | | P1 | M | Architecture | Active | Decouple WPF dialogs from ViewModels. |
| 21 | Introduce injectable clock (IClock) | Desktop | | P1 | S | Architecture | Active | Abstract `DateTime.Now` calls in `MainViewModel`. |
| 22 | Replace closure undo with IUndoableCommand | Desktop | | P1 | M | Architecture | Active | Real Do/Undo/Redo objects instead of the closure stack. |
| 23 | Unit test MainViewModel | Desktop | | P1 | M | Testing | Active | ViewModel orchestration tests after the DI refactor. |
| 24 | Unit test GoogleDriveService | Desktop | | P1 | M | Testing | **Done** | No fake-Drive-client seam exists yet (would need a larger SDK-abstraction refactor, deliberately out of scope here given the class's history of subtle sync bugs) - scoped down to unit-testing the already-pure, already-decoupled helper methods instead. 14 new tests in `GoogleDriveServiceTests.cs`. |
| 25 | Fail loudly on corrupted settings file | Desktop | | P1 | M | Data Integrity | **Done** | `SettingsStore`: `LastLoadWarning` property + `BackupCorruptFile()` back up a corrupt `settings.json` before it gets overwritten; `MainViewModel` shows a startup warning dialog when that happens. Atomic `Save()` (temp-file-then-`File.Replace`, mirroring `TodoStore.SaveAsync`) added too. 9 new tests in `SettingsStoreTests.cs`. |
| 26 | Code-sign application binary | Desktop | | P1 | M | Packaging | Active | Azure Trusted Signing in build scripts, removes SmartScreen warning. |
| 27 | Ship a real installer (MSIX / WiX / Inno) | Desktop | | P1 | L | Packaging | Active | Replace manual zip releases. |
| 28 | Add auto-update engine (Velopack / MSIX) | Desktop | | P1 | L | Packaging | Active | Silent background updates. |
| 29 | Add subtasks & subtask hierarchy | Both | ⚡ | P1 | XL | Core Feature | **Deferred** | Subtask tree arrays in `TaskItem.cs` AND `model.js`; sync merge in both. User decided against building this (gating decision #5). |
| 30 | Add priority field / flag levels | Both | ⚡ | P1 | M | Core Feature | **Done** | New `TaskPriority` enum (None/Low/Medium/High) on `TaskItem.Priority`, carried through `Clone()`/`ApplyTaskFields`/`sync.js` for sync parity. Desktop: Priority `ComboBox` in the detail panel, colored dot on list rows, `PriorityHighest` sort, "High Priority" quick filter. Web gets the field + merge parity, no web UI yet (out of scope). Covered by `TaskComparerTests`/`SyncMergeTests`; review_tasks.md's Critical/High item, checked off there 2026-08-25 but this row wasn't updated until the 2026-08-25 merge pass. |
| 31 | Expand recurrence rules beyond fixed options | Both | ⚡ | P1 | L | Core Feature | Active | **v1.9.0: custom N-unit intervals shipped** (new `TaskItem.RecurrenceInterval`/`RecurrenceInterval` in `model.js`, synced field, "every N days/weeks/months/years" UI on both platforms - `NextDueDate`/`nextDueDate` and `SpawnNextOccurrence`/`spawnNextOccurrence` all interval-aware, tested both sides). **v1.9.1: stale-DueDate-advance bug fixed** - new `RecurrenceAnchor`/`recurrenceAnchor` clamps the anchor date to today when the completed task was already overdue (keeping its time-of-day, e.g. a "@5pm" reminder stays at 5pm), so completing a daily task overdue by 2 weeks now spawns an occurrence due tomorrow instead of one still 13 days overdue; unaffected when the task was completed on time or early. Covered by `MainViewModelRecurrenceTests.cs` (6 tests) and `parity.test.js`'s `recurrenceAnchor` suite (6 tests). Still open from review_tasks.md: weekday-only patterns and end date/count - out of scope for this pass. |
| 32 | Relative date & natural language parser | Both | ⚡ | P1 | L | Productivity | **Done** | Deliberately scoped to the existing token syntax rather than full free-text NLP: `!due:<today\|tomorrow\|weekday\|date>` `@<time>` in `QuickEntryParser.cs` / `model.js`, both carrying an explicit comment on why - free-text parsing risks misreading plain title words ("Call Tuesday about the budget" isn't a due date) and pulls in a heavy localization dependency, working against this app's kept-simple design goal. Confirmed with user (2026-08-24) this satisfies #32 as-is. |
| 33 | Add synthesized "Today"/"Upcoming" view | Both | | P1 | M | Core Feature | **Done** | New "Today" sidebar section (overdue + due-today + pinned) on both platforms - `SidebarFilterKind.Today` in `MainViewModel.cs`/`SidebarFilterItem.cs` for desktop, new `SECTIONS`/`tasksForSection` case in `app.js` for web (excluded from the mobile tab bar to avoid overcrowding a 375px screen; still reachable via the sidebar drawer). |
| 34 | Build out real search UI (Ctrl+F, operators) | Both | | P1 | M | Core Feature | **Done** | Search operators (`tag:`, `is:overdue`, `has:link`, `due:`, etc.) plus a Ctrl+F/`/` shortcut to focus search, on both platforms - new `TaskSearchMatcher.cs` (12 tests) for desktop, rewritten `applySearch` in `app.js` for web. |
| 35 | Implement touch swipe gestures (complete/delete) | Web | | P1 | L | Mobile UX | **Done** | New `bindSwipeGesture` in `app.js` drags a `.task-content` layer over two revealed action panels (`.task-swipe-action.complete`/`.trash` in `styles.css`) - swipe right toggles done, swipe left toggles trash (both reuse the existing `toggleDone`/`toggleTrash`, so undo toasts still apply). Touch-only (mouse/desktop unaffected); a vertical-dominant drag is left alone for native scrolling. Verified by dispatching real `TouchEvent`s at a standalone test harness: right/left commits, sub-threshold snap-back, and vertical passthrough all behaved correctly. |
| 36 | Mobile slide-up drawers for modals | Web | | P1 | M | Mobile UX | **Done** | Pure-CSS: under 640px, `#account-dropdown` and `.modal-card` (covering About/Shortcuts/Add-Link, which all share that class) anchor to the viewport bottom, go full-width with rounded top corners and a grabber handle, and slide up via a new `sheet-slide-up` keyframe. Existing open/close JS and outside-click/Escape handling untouched. Verified visually at 375px width. |
| 37 | Add PWA install banner & iOS Safari prompt | Web | | P1 | M | Offline PWA | **Done** | New `#install-banner` (`app.js`/`index.html`/`styles.css`): listens for `beforeinstallprompt` on Chrome/Edge/Android and calls its `.prompt()` from a custom Install button; on iOS (no such event exists there at all) shows static "Tap Share, then Add to Home Screen" instructions immediately instead. Dismissal persists via localStorage. Also adds `docs/sw.js`, a deliberately no-op service worker (network passthrough, zero caching) registered solely because Chrome's installability check requires one to be present before `beforeinstallprompt` will ever fire - see gating decision #4. Verified: SW registers and activates with no console errors; the actual `beforeinstallprompt`/iOS-hint paths couldn't be triggered live in the test environment (they depend on browser engagement heuristics / a real iOS UA) so those are code-reviewed, not live-tested. |
| 38 | Bundle & minify ES modules (Vite / Rollup) | Web | | P1 | M | Web Performance | **Deferred** | Would require a real npm build step, a source/output split (Pages currently serves `docs/` directly as source), and rework of the cache-bust tooling/CI/deploy flow - real toolchain complexity, working against this app's deliberately build-step-free design and the user's stated preference (2026-08-24) for keeping Tasky simple. Revisit only if unbundled module loading becomes an actual measured perf problem. |
| 39 | Web accessibility (WCAG 2.1 AA) cleanup | Web | | P1 | S | Accessibility | **Done** | `aria-label` added to the theme/text-size switch buttons, new-task buttons, editor pin button (now also flips Pin/Unpin dynamically), and per-task done checkbox in `index.html`/`app.js`. |
| 40 | Content Security Policy (CSP) headers | Web | | P1 | S | Security | **Done** | Strict CSP `<meta>` tag in `index.html`: `script-src 'self' https://accounts.google.com`, `connect-src` scoped to the Drive/userinfo/GIS/token-exchange origins, `img-src` covers `blob:` photo attachments + Google account pictures, `object-src 'none'`. Verified against a local server - GIS loads, OAuth popup flow initiates, zero CSP console violations. |
| 41 | Offline sync conflict notification banner | Web | | P1 | M | Sync Engine | **Deferred** | Toast when an offline draft conflicts with the cloud file. Deferred - no offline drafts to conflict once #6/#7 aren't built (gating decision #4). |
| 42 | Fix event listener memory leaks in editor | Web | | P1 | M | Memory Leak | **Done** | Audited `editor.js`: per-block DOM listeners are already GC-safe (blocks are destroyed via `container.innerHTML = ''`, which drops all references) and the one persistent `document`-level listener is intentionally singleton, not per-render - so no dangling *listeners* were found. The real unbounded-growth leak was object URLs: `photoUrlCache`/`fileUrlCache` pinned every viewed photo/file's blob bytes in memory for the rest of the session with no release path. Fixed via `releaseBlockMedia` (revokes one block's URL the moment it's removed) and `releaseMediaCacheIfTaskChanged` (revokes everything left over when a different task opens). Verified via code review + syntax check only - CSP's `script-src` blocks the `blob:`-URL dynamic import a live white-box test would have needed. |
| 43 | Name icon-only controls (AutomationProperties) | Desktop | | P1 | S | Accessibility | **Done** | Most toolbar buttons already had `AutomationProperties.Name`; added the remaining gaps in `MainWindow.xaml` (Google Drive sync button, per-row pin glyph, block-remove button) and `PhotoViewerWindow.xaml` (close button). |
| 44 | Announce save/sync status to screen readers | Desktop | | P1 | S | Accessibility | **Done** | `AutomationProperties.LiveSetting="Polite"` on the `SaveStatusText` `TextBlock` in `MainWindow.xaml`. |
| 45 | Fix due-date converter dangling theme brushes | Desktop | | P1 | S | Visuals / A11y | **Done** | `DueDateColorConverter` looked up `OverdueBrush`/`WarningBrush` keys that no theme dictionary defined, so it silently always used its hardcoded fallback colors regardless of theme. Overdue now resolves the existing `DangerBrush`; added a real `WarningBrush` to `LightTheme.xaml`/`DarkTheme.xaml` (WCAG AA-audited per theme, ~5.0:1 and ~9.7:1 against their backgrounds respectively). |
| 46 | Global Command Palette (Ctrl+K / Ctrl+P) | Both | | P2 | L | UX / Interaction | Active | Fuzzy search across tasks, views, commands, Desktop & Web. |
| 47 | Connect IUndoableCommand to Command Palette | Desktop | | P2 | S | Architecture | Active | Wire the command registry into the desktop palette. |
| 48 | Drag-and-drop Kanban board view | Both | | P2 | XL | Visual UX | Active | Column board (To Do/In Progress/Done) with drag-and-drop. |
| 49 | Interactive drag-to-reschedule calendar view | Both | | P2 | L | UX / Calendar | Active | Drop tasks on different calendar days, WPF & Web. |
| 50 | Custom Theme Studio & accent color builder | Both | | P2 | L | Visual Design | Active | Nord/Catppuccin/Gruvbox generator with live preview. |
| 51 | Adopt Windows 11 Mica & Acrylic backdrops | Desktop | | P2 | M | Visual Design | Active | Native DWM semi-translucent chrome. |
| 52 | Replace emoji glyphs with monochrome icon set | Web | | P2 | S | Visual Design | **Done** | Scope turned out much smaller than originally described - a prior pass (#146) had already converted the app's UI chrome to `icon()` SVGs. The only remaining emoji were 5 attachment-placeholder strings in `editor.js` (`renderPhotoByFileName`/`renderFileByFileName`), replaced with the existing `image`/`paperclip` icons via a new `setIconText` helper (text node, no `escapeHtml` import needed). Web only - a handful of emoji-labeled buttons remain in Desktop's `GoogleDriveSettingsControl.xaml`, left alone since this pass was scoped to Web/mobile. |
| 53 | Add small set of purposeful micro-animations | Both | | P2 | M | Visual Design | Active | Checkmark, pin ease, view-switch transitions. |
| 54 | Consolidate duplicated colors & tighten type scale | Desktop | | P2 | S | Visual Design | Active | Unify theme tokens in `MainWindow.xaml`, standardize font steps. |
| 55 | Make undo visible after destructive actions | Both | | P2 | S | UX / Feedback | Active | 'Deleted X tasks — Undo' toast, Desktop & Web. |
| 56 | First-run onboarding experience | Web | | P2 | M | UX / Onboarding | **Done** | Web only. New `#onboarding-modal` (static HTML + show/hide, same idiom as the Shortcuts modal) fires once from `loadFromDriveWithRetry()` the first time it finds a genuinely new Drive account (`noRemoteFileYet`), gated by a new `tasky-onboarded` localStorage flag - not on every empty state, so it won't reappear after real use even if every task gets trashed. Shows 4 feature callouts (Quick Add syntax, search operators, mobile swipe, Drive sync) and an opt-in "Add 3 sample tasks" checkbox that calls the real `createQuickTask()` (same parser as the quick-add row, not a hand-rolled task shape). Replayable anytime via a new "Replay welcome tour" button in the About popup. |
| 57 | Visual progress bar during Google Drive sync | Both | | P2 | S | UX / Feedback | **Done** | v1.9.0. A real determinate bar next to the status text on both platforms (desktop: `ProgressBar` bound to new `MainViewModel.SyncProgressPercent`/`IsSyncing`; web: `#save-progress` fill width) - stage-based percent reported at each real pipeline step (`SyncCoordinator.PerformSyncAsync`'s `Progress()` / `setSyncProgress` in `app.js`), not a fabricated animation, since neither platform's Drive client exposes true byte-level transfer progress cheaply. |
| 58 | Escape filenames in Drive query strings | Desktop | | P2 | S | Reliability | **Done** | New `EscapeDriveQueryValue` (`GoogleDriveService.cs`, `internal` + `InternalsVisibleTo` for testability) escapes `'` and `\` per Drive's `q`-parameter syntax, applied to `folderName`/`fileName`/`parentFolderId` in `GetOrCreateFolderAsync` and `FindExistingFileIdAsync` - a Windows filename can legally contain either character (e.g. "Steve's Tasks.tasky"), which previously broke the query. 4 new tests. |
| 59 | Propagate CancellationToken through I/O | Desktop | | P2 | M | Reliability | Active | Allow shutdown to cancel long-running cloud uploads. |
| 60 | Error classification & exponential backoff | Desktop | | P2 | M | Reliability | Active | Differentiate transient glitches from auth errors. |
| 61 | Dispose DriveService & credentials on re-auth | Desktop | | P2 | S | Memory Leak | **Done** | New `DisposeCurrentDriveSession()` disposes `_driveService` (`IDisposable` via `BaseClientService`) and `_credential` (if disposable) before every reassignment - `AuthenticateAsync`, `TrySilentAuthenticateAsync`, and `SignOutAsync` all used to just overwrite/null the fields, leaking the previous session's `HttpClient` on every re-auth. Also disposes an abandoned local `credential` on `TrySilentAuthenticateAsync`'s early-return-on-failed-refresh path. |
| 62 | Cache compiled regexes in attachment cleanup | Desktop | | P2 | S | Performance | Done | `TodoStore.ListBackups`/`BackupExistingFile` now share one compiled, structural static `Regex` (name/timestamp/extension as named groups) instead of building a per-file-name pattern on every call. `QuickEntryParser`'s multi-space collapse and `TaskDetailViewModel.SanitizeTag` (runs on every tag-box keystroke) got their own compiled static fields. `SettingsWindow.DigitsOnly` gained `RegexOptions.Compiled`. `GoogleDriveService.AttachmentFilenameRegex` no longer exists (removed by an earlier refactor) - nothing to do there. `ExtractTaskMediaFilenames`'s pattern (`TaskMediaHelper.cs`) was already compiled+cached. |
| 63 | Batch rapid changes into single list refresh | Desktop | | P2 | M | Performance | Done | `MainViewModel.SearchText`'s setter no longer calls `FilteredTasksView.Refresh()` synchronously on every keystroke - it now starts a 150ms `_searchDebounceTimer` (same `DispatcherTimer` shape as the existing 700ms `_saveDebounceTimer`), and the actual refresh + `EmptyStateMessage` notification happen once on the timer's `Tick`. The bound text itself still updates immediately; only the expensive predicate re-scan is deferred. Scoped to the search box only - `SelectedSidebarItem`/`CurrentQuickFilter` are discrete click-driven changes, not rapid keystrokes. |
| 64 | Stop re-scanning save file on every sync | Desktop | | P2 | M | Performance | Active | Track referenced attachments incrementally. |
| 65 | Cache decoded bitmaps in photo converters | Desktop | | P2 | M | Performance | Active | Avoid re-decoding images on every re-render. |
| 66 | Mobile haptic feedback (navigator.vibrate) | Web | | P2 | S | Micro-UX | **Done** | New `haptic()` helper (`navigator.vibrate?.(15)`, a silent no-op on iOS Safari which never implemented the Vibration API) fires on task completion (`toggleDone`, only on the completing edge, not un-completing) and on a committed swipe (either direction, in `bindSwipeGesture`'s `finish()`). No drag-to-reorder feature exists in the app to hook a third case into. |
| 67 | DOM list mutation optimization | Web | | P2 | M | Performance | **Done** | `renderList()` now does a keyed diff: `taskRowRefs` (taskId → row refs) lets a task already on screen get patched in place (`updateTaskRow`) and moved to its new position via `appendChild` (which moves an existing node rather than cloning/rebinding) instead of every row being torn down and rebuilt from scratch - so a title-field or search keystroke, which previously rebuilt every row's DOM/listeners each time, now only touches rows that actually changed. Click/swipe/checkbox listeners close over the stable `task` object reference (mutated in place elsewhere, never replaced) and read `currentSection`/`selectionMode` fresh at event time, so a reused row never needs its listeners rebound. Also added `sectionCounts()`: one pass over `appState.Tasks` computes every sidebar section's badge count, replacing 5 separate `tasksForSection()` calls (O(sections) instead of O(sections × tasks)). The bigger app.js-module-split this was flagged alongside (#136) is unrelated scope and remains open. |
| 68 | Web Push Notifications API | Web | | P2 | XL | Notifications | Active | Service Worker Web Push for background due-date alerts. |
| 69 | Client-side Web Crypto API E2EE integration | Both | ⚡ | P2 | XL | Security | Active | `window.crypto.subtle` AES-256-GCM matching desktop E2EE. |
| 70 | Cache decoded photo thumbnails in storage | Web | | P2 | M | Storage | **Done** | New IndexedDB store (`tasky-thumbnails`, `editor.js`) keyed by filename (every attachment filename is a fresh random UUID, so entries never go stale/need invalidating). `loadPhotoBlob` checks it before hitting Drive; a miss downloads the full blob, downscales it to at most 1024px on its longest side via `createImageBitmap`+canvas (re-encoded as JPEG), caches that, and displays it - so a photo already viewed once loads instantly from local storage on every later visit, no repeat network round-trip. Capped at 200 entries, oldest pruned first on overflow (same bounded-not-unbounded spirit as the tombstone retention window). Entry deleted from cache when its block is removed (`deleteRemoteAttachmentIfAny`). |
| 71 | Playwright Safari & Chrome automated test suite | Web | | P2 | XL | Testing | Active | E2E runner for GIS auth, IndexedDB, swipe gestures. |
| 72 | Lighthouse PWA re-audit optimization | Web | | P2 | M | PWA Audit | **Done** | No Lighthouse CLI available in this environment, so this was a manual audit against Lighthouse's actual PWA/SEO/best-practices criteria rather than a numeric score - stated plainly, not oversold. Fixed real gaps needing no new artwork: added `<meta name="description">`, `apple-mobile-web-app-capable`/`-status-bar-style`/`-title` (iOS doesn't read `manifest.json`'s `display: standalone`), and explicit sized `<link rel="icon" sizes="192x192"/"512x512">` entries. Explicitly not touched: a maskable icon variant (existing PNGs have no safe-zone padding - would need new artwork) and real performance numbers (no CLI here; `#116` already tracks that separately). A real Lighthouse run in the user's own Chrome DevTools is still the way to get an actual score. |
| 73 | Upgrade storage engine to SQLite (as cache) | Desktop | | P2 | XL | Data Engine | Active | Local indexed query cache, keeping per-task JSON diff sync. |
| 74 | Add crash reporting & opt-in telemetry | Both | ⚡ | P2 | M | Telemetry | Active | Structured telemetry (Sentry/Serilog) across both platforms. |
| 75 | Smart toast notifications with inline actions | Desktop | | P2 | L | UX / Toasts | Active | Complete/snooze directly from Windows toasts (partially present via `TrayIconService`). See #120 for the reminder-accuracy bug (time-of-day + re-notify-on-launch) this sits on top of. |
| 76 | Customizable quick-add floating bar (Win+Shift+T) | Desktop | | P2 | M | Quick Entry | Active | Redesign the existing global hotkey into a spotlight bar. |
| 77 | Built-in Pomodoro & focus time tracker | Both | | P2 | M | Productivity | Active | 25m/5m cycles with time-tracking stats. |
| 78 | Eisenhower Matrix priority view (2x2) | Both | | P2 | M | Productivity | Active | Do First / Schedule / Delegate / Eliminate. |
| 79 | Markdown & syntax-highlighted code blocks | Both | | P2 | M | Rich Notes | Active | Highlighting for C#/Python/SQL/JSON in notes. |
| 80 | Inline interactive widgets (Mermaid & LaTeX) | Both | | P2 | XL | Rich Notes | Active | Render diagrams/math inside task notes. |
| 81 | Task templates & custom automation rules | Both | ⚡ | P2 | M | Productivity | Active | Workflow templates and conditional rules. |
| 82 | Saved smart filters & custom sidebar views | Web | | P2 | M | Core Feature | **Done** | Originally Web-only, localStorage-backed (see the git history for that version's details). **Superseded by #148 (2026-08-28)**: views now live in `appState.SavedViews`/`DeletedSavedViewIds` and sync via Drive like tasks - the localStorage-only description this row used to have is stale, kept only as a historical note. A saved view is still just a named, persisted search-box query string, not a second filter system - reuses the existing `tag:`/`is:`/`has:`/`due:` operator engine in `applySearch` almost entirely as-is. Selecting a view still evaluates a hidden `queryOverride` without touching the visible search box (`case 'view':` in `tasksForSection`) - deliberately NOT changed to match Desktop's "fill the search box" mechanism when #148 ported this, since that's a Desktop-specific UX call, not a cross-platform requirement. |
| 83 | Multi-provider cloud sync (OneDrive, Dropbox) | Both | ⚡ | P2 | XXL | Cloud Engine | Active | Abstract `ISyncProvider` for OneDrive/Dropbox/WebDAV. |
| 84 | Two-way calendar sync (Google, Outlook) | Both | ⚡ | P2 | XXL | Calendar Sync | Active | 2-way conflict handling to work calendars. |
| 85 | Zero-Knowledge End-to-End Encryption (E2EE) | Both | ⚡ | P2 | XL | Security | Active | Client-side AES-256-GCM compatible with the merge protocol. |
| 86 | Multi-format data migration wizard | Both | | P2 | M | Onboarding | Active | Import wizard from Todoist, TickTick, Notion. |
| 87 | Monetization engine & licensing model | Both | ⚡ | P2 | XL | Monetization | **Deferred** | Depends on #11, which is on hold while the product gets built first. |
| 88 | FlaUI / Appium automated UI test suite | Desktop | | P2 | XL | Testing | Active | Automated WPF UI interaction testing. |
| 89 | Cross-language C# vs JS parity test suite | Both | ⚡ | P1 | M | Testing / Parity | **Done** | New `docs/js/test/parity.test.js` (51 tests, zero-dependency `node:test`/`node:assert`, no npm install - matches Web's build-step-free design): reuses `SyncMergeTests.cs`'s and `QuickEntryParserTests.cs`'s exact test vectors for `mergeRemoteState`, `deduplicateTombstones`, `parseDotNetDate`/`formatDotNetDate` round-trips, and `parseQuickAdd`. Wired into `.github/workflows/build.yml`'s `web` job as its own step; the JS syntax-check loop now also covers `docs/js/test/*.js`. |
| 90 | Single source of truth for C# vs JS logic | Both | ⚡ | P2 | L | Desktop/Web Parity | Active | Code generator or shared schema deriving JS from C#. |
| 91 | Enforce sync-merge parity between C# and JS | Both | ⚡ | P2 | L | Desktop/Web Parity | Active | Tests verifying `TaskSyncMerge.cs` matches `sync.js`. |
| 92 | Local AI task assistant (Ollama / ONNX) | Desktop | | P3 | XXL | AI / Innovation | Active | On-device decomposition and note summarization. |
| 93 | Audio memos & local voice task recording | Both | | P3 | XL | Quick Entry | Active | Local transcription via Whisper ONNX / Web Audio API. |
| 94 | Global web & desktop clipper extension | Both | ⚡ | P3 | XL | Ecosystem | Active | Chrome/Edge extension to clip into Tasky. |
| 95 | Split-screen dual task editor & multi-window | Desktop | | P3 | L | UX / Notes | Active | Edit two notes side-by-side (requires state extraction). |
| 96 | Contextual audio & haptic sound feedback | Both | | P3 | M | Micro-UX | Active | Completion chimes, sound packs. |
| 97 | Conflict-Free Replicated Data Types (CRDT) sync | Both | ⚡ | P3 | XXL | Sync Engine | Active | Field-level real-time conflict-free merging. |
| 98 | Companion mobile apps (MAUI / Flutter) | Both | ⚡ | P3 | XXL | Mobile | **Deferred** | #10 ruled out a shared compiled core; native apps would mean a third hand-ported codebase. |
| 99 | Public REST/gRPC API & webhooks ecosystem | Both | ⚡ | P3 | XL | Ecosystem | Active | Local REST API for Zapier, Make, n8n. |
| 100 | Email-to-task forwarding integration | Both | ⚡ | P3 | XL | Ecosystem | Active | Convert inbound emails directly into tasks. |
| 101 | Bi-directional task backlinks ([[Task Title]]) | Both | ⚡ | P3 | M | Rich Notes | Active | Wiki-style task linking / knowledge graph. |
| 102 | Git repository & issue sync (GitHub/GitLab) | Both | ⚡ | P3 | XL | Dev Tools | Active | Bring assigned issues into Tasky. |
| 103 | PDF & print summary report generator | Both | | P3 | M | Reporting | Active | Export project summaries via QuestPDF / HTML print. |
| 104 | Offline-first P2P local network sync | Both | ⚡ | P3 | XL | Sync Engine | Active | TLS-encrypted peer sync via mDNS/Bonjour. |
| 105 | Windows Hello biometric lock & secure vault | Desktop | | P3 | M | Security | Active | Protect notes with fingerprint/PIN. |
| 106 | Web App Background Sync API | Web | | P3 | M | Sync Engine | **Deferred** | Replay pending edits when network returns. Deferred - no offline edit queue to replay once #6/#7 aren't built (gating decision #4). |
| 107 | Build Team Workspaces & E2EE shared task lists | Both | ⚡ | P3 | XXL | Collaboration | **Deferred** | #12 resolved Tasky as personal-only for now; revisit if that changes. |
| 108 | Time-machine data recovery scrubber | Desktop | | P3 | M | Data Recovery | Active | Browsing/diff UI on top of existing BackupService archives. |
| 109 | Native Windows 11 desktop widgets | Desktop | | P3 | XL | Windows 11 | Active | Agenda and Quick Add on the Widgets Board. |
| 110 | White-labeling & enterprise Group Policy (GPO) | Desktop | | P3 | M | Enterprise | Active | MSIX enterprise deployment with registry GPO flags. |
| 111 | Full natural language date & recurrence parser | Both | ⚡ | P3 | XL | Productivity | Active | Full ANTLR/NLP parser for dates and times, C# and JS. |
| 112 | Zero-allocation data virtualization | Desktop | | P3 | M | Performance | Active | Container recycling if task count exceeds 5,000. |
| 113 | Async sync engine using Channels | Desktop | | P3 | M | Performance | Active | Retry policies and cancellation tokens on the existing `SemaphoreSlim`. |
| 114 | Zero-allocation data virtualization testing | Desktop | | P3 | M | Performance | Active | Benchmark 10,000+ task lists under low memory. |
| 115 | UI render profiling on large task lists | Desktop | | P3 | M | Performance | Active | Profile memory allocations while scrolling 5,000+ tasks. |
| 116 | Web PWA Lighthouse performance profiling | Web | | P3 | M | Performance | Active | Chrome Lighthouse profiling to optimize PWA boot performance. |
| 117 | Silent access-token refresh for Tasky Web | Web | | P1 | M | Auth | **Done** | Shipped and verified live 2026-08-24/25. `signIn()` (`auth.js`) builds the Google authorization URL by hand (GIS's `initCodeClient` has no `access_type`/`prompt` fields) requesting `access_type=offline`+`prompt=consent`; `exchange-token` stores the resulting `refresh_token` in a new Firestore `sessions` collection and hands the browser only an opaque `session_id`; `functions/refresh-token/` exchanges that session for fresh access tokens (`POST`) and revokes+deletes it on sign-out (`DELETE`); `auth.js` schedules a silent background refresh ~5min before expiry, retries on network failure, catches up on tab-visibility-change, and `getAccessToken()` tries one silent refresh before ever throwing `NOT_SIGNED_IN`. Decided against a cross-site cookie for the refresh token (fails under Safari's third-party-cookie blocking) in favor of Firestore + a first-party-localStorage session id. Deployed both Cloud Functions (2nd gen) via the Cloud Run console's inline-editor "Write a function" flow, granted Firestore access (already covered by the existing Editor role on the runtime service account - no extra IAM grant needed), and live-tested end to end by forcing an expired cached token and confirming a reload silently refreshed with zero redirect. **Two deploy-config bugs found and fixed during that live test:** (1) the `refresh-token` function was initially given a stale `GOOGLE_CLIENT_SECRET` from an old downloaded credentials JSON predating the #1 secret rotation - fixed by copying the live value directly from `exchange-token`'s config instead; (2) `index.html`'s CSP `connect-src` never included the new `refresh-token` origin, so the browser silently blocked every refresh attempt with no error surfaced anywhere except the console - fixed by adding it. Shipped as v1.7.8. **A third, more serious bug surfaced the next day** (2026-08-25) when a real task load hit `Drive API 403 insufficientPermissions`: the OAuth consent screen's Data Access page had zero scopes registered (not something version-controlled - a Cloud Console setting), so Google silently stripped `drive.file` from every grant regardless of what was requested - fixed by registering it there. But even after that fix, signing in from *within* the app still produced only an identity-only grant (no Drive line at all), while pasting the exact same authorization URL into a fresh tab correctly showed the full consent screen - isolating the difference to `index.html` loading Google Identity Services' script (`accounts.google.com/gsi/client`). Merely having that script present was enough for Chrome to intercept the redirect and substitute its own streamlined "Sign in with Google" identity-only flow, silently dropping any non-identity scope no matter what `auth.js`'s hand-built authorization URL actually requested. Fixed by removing the script entirely (`signIn()` hasn't called any GIS API since this rewrite; the one remaining use, `signOut()`'s client-side `revoke()` call, is now covered by `refresh-token`'s existing server-side revoke) along with the CSP/frame-src/`waitForGoogleIdentity()` plumbing that existed only to support it. Also hardened the failure mode this bug exposed: Google's consent screen shows sensitive scopes as an opt-in checkbox separate from "Continue," unchecked by default, so a user can sign in successfully while still declining Drive access by mistake (confirmed by reproducing it firsthand) - `driveFetch()` now recognizes that specific 403 and throws a `DRIVE_SCOPE_MISSING` sentinel that every `NOT_SIGNED_IN` handling site (load errors, autosave/Sync Now, inline attachment uploads) surfaces as a clear "Tasky needs Google Drive access..." message with a one-click fix, instead of a raw JSON dump. Verified fixed end to end: fresh sign-in now shows the real Drive consent screen in-app, and a direct server-side refresh confirmed `drive.file` present in the granted scope. Raised by user 2026-08-24 after hitting the hourly re-login firsthand; the scope-stripping bug self-reported by the user 2026-08-25 the first time they actually relied on this in daily use. |
| 118 | Move Desktop OAuth secret fully out of source (loopback PKCE) | Desktop | | P1 | S | Security | Active | **Tried and reverted, 2026-08-28**: removed `DefaultClientSecret` and sent an empty secret for the default client, on the theory that Desktop/installed-app OAuth clients rely on PKCE alone (RFC 8252). A live sign-in test came back `invalid_request: client_secret is missing` straight from Google's token endpoint - this specific registered client's type does require it, so the PKCE-only approach doesn't work as-is and the secret was restored to get sign-in working again. The actual fix still needed is keeping the secret out of *tracked source* without changing what gets sent at runtime - e.g. a gitignored local file baked in at release-build time - which is bigger than the original "S" estimate since it touches the release/build process, not just this file. Re-scope or re-estimate before picking this up again. |
| 119 | Surface sync conflicts instead of silent last-write-wins | Both | | P1 | M | Sync Engine | **Done** | `TaskSyncMerge.ComputeMergePlan` takes a new `lastSyncTimeUtc` param; when both sides edited a task since that baseline, the losing edit is kept as a new `"(conflicted copy)"` task (`ConflictedCopiesToAdd`/`CreateConflictedCopy`) instead of just vanishing - remote still wins the original ID. `MainViewModel.MergeRemoteState` passes `_settings.LastGoogleDriveSyncTime.ToUniversalTime()` and applies the copies like any other new task; `SyncCoordinator`'s final status line reports the count instead of the plain "Successfully synced" message when any occurred. Mirrored in `sync.js`'s `mergeRemoteState`/`createConflictedCopy` (reads the existing `tasky-last-synced` localStorage key) with the matching status message in `app.js`. 6 new tests total (3 C#, 3 JS via #89's new suite). |
| 120 | Honor time-of-day in reminders + persist notified state | Desktop | | P1 | S | Reliability | **Done** | `ReminderScheduler.GetDueTasks` now takes `now` (not `today`) and only fires at the exact timestamp when a due date has a real time component - `DueDate.TimeOfDay == TimeSpan.Zero` (the WPF DatePicker's own signature for "no time picked") still fires from the first poll of the day, matching the old behavior for date-only due dates. `_notifiedTaskIds` is persisted to a new `Settings.NotifiedTaskIds`, seeded back in on construction and saved on every change (notify/snooze/file-switch-clear) via a new `Action<IEnumerable<Guid>>` callback - extracted `ITrayNotifier` (the one member `ReminderScheduler` needs from `TrayIconService`) so this is all unit-testable without a real WinForms tray icon. 10 new tests. Sits under #75. |
| 121 | Preserve time-of-day when editing due dates on Web | Web | | P1 | S | Bug Fix | **Done** | New `withDatePickerValue` helper in `app.js`: the due-date `change` handler now merges the picked y-m-d with the existing `DueDate`'s time-of-day (via `parseDotNetDate`) instead of hardcoding `T00:00:00`, so touching the date field no longer silently zeroes an explicit `@3pm`-style time. No prior due date still defaults to midnight, matching desktop's own DatePicker behavior. Pairs with #120. |
| 122 | Extend search to checklist/attachment/link text + more due: operators | Both | | P2 | S | Core Feature | **Done** | `TaskSearchMatcher.BlockMatches` (desktop) and `blockMatchesSearch` (web's `applySearch`) now also check `ChecklistItems[].Text`, attachment `FileName`, and link `LinkLabel`/`Url`, not just block text. Added `due:week` (due within the next 7 days) and `due:none` (no due date) operators on both platforms; updated the in-app search hints (`index.html`) to match. 6 new desktop tests. |
| 123 | De-duplicate MainWindow.xaml.cs hit-testing & drag-drop code | Desktop | | P2 | M | Code Quality | Active | Four near-identical `BlockUIContainer` hit-test loops (`TransformToAncestor` bounds checks across mouse-down/right-click/cursor-query/move handlers) and duplicated `Body_*`/`NoteBody_*` drag-enter/over/leave/drop handler pairs in the 1,293-line code-behind; extract one shared hit-test helper and one shared image-insert sequence. From review_tasks.md. |
| 124 | Async-ify remaining blocking saves/loads on UI thread | Desktop | | P2 | M | Performance | **Done** | Scoped to the review's three named call sites: `SaveFileAsCommand` and `CreateNewLocalFileForSync` (now `CreateNewLocalFileForSyncAsync`, cascading its `Func<bool>` delegate through `GoogleDriveSettingsControl` to `Func<Task<bool>>`) now `await _store.SaveAsync(...)` instead of the blocking `Save()`/`GetResult()` bridge; `ImportBackupCommand`'s `_store.Load` similarly became `await LoadAsync` (already inside an async handler). **Deliberately not touched**: `MainViewModel.LoadFile`'s own `_store.Load(path)` (and the migration-save nested inside `TodoStore.Load`) - `LoadFile` is called from 6 places including the constructor's synchronous startup path, and async-ifying it means either a visible empty-window flash on launch or a larger restructure. Same "high-blast-radius, needs its own pass" call as #15's FileSessionManager deferral - documented inline at the call site rather than silently left. |
| 125 | Speed up TodoStore.ListBackups | Desktop | | P3 | S | Performance | Active | Fully deserializes every backup file (up to the 500-file cap) just to show a task count on dialog open. Parse the count lazily via `Utf8JsonReader`, or compute it on selection only. `Services/TodoStore.cs:219-250`. From review_tasks.md. |
| 126 | Batch settings.json writes during sync | Desktop | | P2 | S | Performance | Done | `SettingsStore` gained a depth-counted `BeginBatch()` scope: while active, `Save()` just records the pending settings instead of writing, and the real DPAPI-protect + serialize + atomic-rewrite happens once when the outermost scope disposes. `SyncCoordinator.PerformSyncAsync` wraps its whole body in one `using var _ = _settingsStore.BeginBatch();`, so folder resolution, media bookkeeping, file-ID cache, and the final timestamp write all collapse into a single disk write per sync pass. Every other call site (17 in `MainViewModel` - window state, theme, etc.) is outside the batch scope and still writes immediately, unchanged. |
| 127 | Make AppLogger non-blocking | Desktop | | P2 | S | Performance | Done | `AppLogger` now queues lines through an unbounded `Channel<string>`, drained by a single background consumer task that does the actual `File.AppendAllText` - `Log()` calls are now a non-blocking enqueue instead of a locked, synchronous file write on the calling (often UI) thread. `App.OnExit` calls a new `AppLogger.Flush()` (completes the channel, waits up to 2s) so shutdown-time log lines aren't lost. The three clearest "fires on every single save/sync" Info lines (`TodoStore.SaveAsync`, `GoogleDriveService` upload/download) were demoted to Debug; branch-specific/notable sync events (created file, reused existing, 3-way-diff attachment changes) stayed at Info. `AppLogger.OpenLogFile` no longer shows `ThemedMessageBox` dialogs itself - it returns an `OpenLogFileResult`, and `MainViewModel`'s `OpenDebugLogCommand` handles presentation. |
| 128 | Restructure the nested TodoApp.Tests project | Desktop | | P3 | M | Tooling | Active | `TodoApp.Tests` lives inside the app project folder, forcing the `Compile Remove="TodoApp.Tests\**"` glob workaround and risking WPF temp-project leaks. Move to conventional `src/`+`tests/` siblings with a `.sln` and `Directory.Build.props`; update `.github/workflows/build.yml` paths. From review_tasks.md. |
| 129 | Cache JsonSerializerOptions / source-generated serialization | Desktop | | P3 | S | Performance | Active | `TodoStore.SaveAsync`/`SettingsStore.Save` allocate a fresh `JsonSerializerOptions` per call; hoist to a static. A `JsonSerializerContext` for `AppState`/`Settings` also cuts first-save latency and enables trimming. From review_tasks.md. |
| 130 | Stream Google Drive downloads to disk | Desktop | | P3 | S | Performance | Active | `DownloadFileAsync`/`DownloadMediaDirectoryAsync` buffer whole files into a `MemoryStream` then `ToArray()` (double allocation, LOH pressure for big attachments). Download directly into a `FileStream` (temp file + rename for the data file, to keep the atomic-replace property). From review_tasks.md. |
| 131 | Trim legacy TaskItem fields from the wire format | Desktop | | P3 | S | Data Engine | Active | Post-migration `Notes`/`Links`/`Photos` are always empty but still serialize into every save/sync payload. Add ignore-when-empty conditional serialization while keeping deserialization for old files. `Models/TaskItem.cs`, `TodoStore.MigrateToBody`. From review_tasks.md. |
| 132 | Don't silently truncate long titles | Both | | P3 | S | Data Integrity | **Done** | Raised `TaskItem.MaxTextLength` (and web's matching `MAX_TASK_TEXT`) from 500 to 2000 - a title anywhere near the old limit was rare but not impossible, and a sync merge landing an over-limit remote value could silently reshape it; 2000 chars is well past any real title, without adding UI validation or logging complexity for a cap nothing should hit in practice. Caught and fixed a related latent mismatch in the same pass: web's `newChecklistItem` was reusing the task-title constant, which would have let web checklist items grow past `ChecklistItem.cs`'s own still-500 cap - split into a separate `MAX_CHECKLIST_ITEM_TEXT`. |
| 133 | Fix global cursor-override leak in RichTextBox hover | Desktop | | P3 | S | Bug Fix | **Done** | Deleted `RichTextBox_PreviewMouseMove` (and its XAML wiring) outright rather than patching it - `RichTextBox_QueryCursor` already sets a proper per-element `Cursor` via `e.Cursor`, WPF's own mechanism for this, with none of the global-`Mouse.OverrideCursor` leak risk. (A second, unrelated `Mouse.OverrideCursor` usage in `Behaviors/RichTextBoxBehavior.cs`, for inline-image containers, already has a more defensive `MouseEnter`/`MouseLeave`/`Unloaded` pairing and wasn't part of this item - left alone.) |
| 134 | Reconsider the WinForms dependency (NotifyIcon) | Desktop | | P3 | S | Tech Debt | Active | `UseWindowsForms` exists solely for `TrayIconService`'s `NotifyIcon`, requiring global-using suppression hacks in the csproj. A WPF-native tray package (e.g. H.NotifyIcon) or shell interop would drop the second UI framework. From review_tasks.md. |
| 135 | Small nice-to-haves: auto-empty Trash, Start-with-Windows, full-list export, Quick Add live preview | Both | | P3 | M | Productivity | **Done** | v1.9.0, and extended to Web/mobile beyond the original Desktop-only scope for feature parity, except Start-with-Windows which is desktop-only by nature (no browser equivalent to launching at OS boot). Auto-empty Trash after N days (opt-in, per-device setting on both platforms, using `ModifiedAt` as a "trashed at" proxy since a closed task is read-only); Start Tasky with Windows (registry Run key, desktop Settings only); whole-list Markdown/HTML export (`ExportService.ExportAllToMarkdown/Html` + File menu on desktop, menu buttons + client-side download on web); Quick Add live preview of the parsed title/due date/tags before Enter commits (desktop `QuickAddWindow`, web's inline quick-add row). |
| 136 | Split app.js monolith into modules + patch renders instead of full re-renders | Web | | P1 | L | Architecture | Active | Web's twin of #15 (MainViewModel god object): `app.js` is 1,843 lines of module-global state and DOM wiring, and every keystroke in the title field or search calls `renderList()`, rebuilding the entire task list DOM (rows, icons, swipe listeners) from scratch. Split into state/list-view/editor-view/navigation/install-banner modules; debounce or patch-only the affected row. From review_tasks.md. |
| 137 | Verify hardening of the token-exchange/refresh Cloud Run functions | Web | | P1 | S | Security | **Done** | Audited `functions/exchange-token/`/`functions/refresh-token/` (both live in-repo, not just deployed). CORS allowlist was already real and correctly scoped to the GitHub Pages origin. Two genuine gaps fixed: (1) sessions lived in Firestore forever until an explicit sign-out - `refresh-token` now checks `created_at` age against a new `SESSION_MAX_AGE_DAYS` env var (default 90d) and 401s+deletes past it, same fallback path the frontend already handles for `invalid_grant`; (2) no rate limiting at all - added a documented best-effort in-memory per-instance limiter (`rateLimit.js`, duplicated in both function directories since each deploys independently) as defense-in-depth, with the limitation (not distributed, resets on cold start) stated plainly rather than oversold. |
| 138 | Support non-photo file attachments on Web | Web | | P2 | M | Core Feature | **Done** | Added a "+ File" button to the insert toolbar (`editor.js`) with a generic, unrestricted `<input type="file">`, wired to a new `handleFilePick` that mirrors `handlePhotoPick` exactly (random-UUID filename, optimistic local preview, roll back the block on upload failure) but creates a `NoteBlockType.File` block via `uploadAttachmentBlob` (already content-agnostic, no changes needed). `renderBlock`'s File case now calls `renderFileByFileName` (download/cache/click-to-retry, same as the pre-existing inline-file-chip rendering) instead of showing static read-only text - block removal, remote-attachment deletion, and search/filter (`has:attachment`) already handled File blocks generically, so no other code needed to change. From review_tasks.md. |
| 139 | Prune web-created attachment orphans on delete | Web | | P2 | S | Data Engine | **Done** | New `deleteAttachmentBlob` in `drive.js` (mirrors `GoogleDriveService.cs`'s 3-way-diff prune - a permanent `Files.Delete`, not trash) is called from the block-remove button's click handler in `editor.js` for Photo/File blocks, fire-and-forget so a slow/unreachable Drive doesn't block the (otherwise instant) block removal. No-ops silently if the file isn't found - already gone, or never finished uploading. |
| 140 | Prune old tombstones (retention window) | Both | | P3 | S | Data Engine | **Done** | Both `TaskSyncMerge.DeduplicateTombstones` (C#) and `sync.js`'s `deduplicateTombstones` now drop tombstones older than a 90-day window (an optional `now`/`nowUtc` param for deterministic tests, defaulting to real current time in production) - applied at the same point both already deduplicate, so local (on load) and remote (right after download) tombstones both get pruned before every merge. Accepted tradeoff spelled out in both comments: a device offline longer than 90 days can resurrect an old deletion instead of finding a tombstone that says not to. 6 new C# tests, 4 new JS tests. |
| 141 | Replace remaining native confirm() dialogs with app modal style | Web | | P3 | S | Visual Design | **Done** | New `confirmModal(message, {title, confirmLabel, danger})` in `app.js` (promise-based, same `.modal-overlay`/`.modal-card` idiom as `promptForLink`) replaces all four native `confirm()` calls - `deleteForever`/`emptyTrash`/`confirmSignInIfDirty` (all `danger: true`, initial focus on Cancel rather than the destructive action) and `moveAllDoneToTrash` (reversible, so not styled as danger). All four enclosing functions became `async`; no caller needed changes since none used the old synchronous return value. Verified visually in both themes. |
| 142 | Add bulk actions (multi-select) on Web | Web | | P2 | M | Core Feature | **Done** | New "Select multiple" toggle (`select-toggle-btn`) puts the task list into a selection mode: each row shows a selection checkbox (CSS-only visibility flip via `.task-list.selecting`, no re-render needed to toggle it) and a bulk-actions bar appears with Select All / Mark Done / Trash / Restore / Delete, mirroring desktop's `SelectedTasks`/`Bulk*` commands (`MainViewModel.cs`'s `InitializeBulkCommands`) - all four actions are always available and each filters its own targets (e.g. Restore only touches already-trashed selections), same as desktop's CanExecute-gated toolbar buttons. Delete confirms via the app's own modal (`confirmModal`, matching #141) rather than a native `confirm()`. Selection resets on section navigation (Trash's semantics differ from every other section) and after any bulk action completes. Cross-referenced from review_tasks.md's Web feature-parity notes. |
| 143 | Add a Settings section on Web, with a "Show Mark done checkbox" toggle | Web | | P3 | S | Visual Design | **Done** | Raised discussing whether swipe-to-done makes the per-row "Mark done" checkbox redundant on mobile - decided to keep it (still the discoverable/accessible path) but let it be turned off by anyone who's learned to swipe. New `#settings-dropdown` (opened from a new "Settings" item in the menu dropdown; anchored `.dropdown` popup under the menu button, same idiom as `menu-dropdown`/`account-dropdown` - not a modal, see the follow-up note below) now holds the Theme and Text Size controls (moved out of the cramped original dropdown) plus the new toggle, default on, persisted to `localStorage['tasky-show-done-checkbox']`, applied via a `hide-done-checkbox` class on `<html>`. Web/mobile-only by design - desktop has no swipe-to-done, so its row checkbox is never redundant there. |
| 144 | Add a tag picker dropdown on Web (browse/click existing tags, create new ones) | Web | | P2 | M | Feature Parity | **Done** | Reported live as "tag drop down is not working" - it had never existed on web, only a bare Enter-to-add text box, unlike desktop's real tag popup (`TaskDetailViewModel`'s `IsTagPopupOpen`/`FilteredAvailableTags`/`CanCreateNewTag`/`SelectExistingTagCommand`). New `#tag-suggest-popup` opens on focus, filters live as you type (substring match against `allTags()`), lets you click any suggestion to add it, and shows a "+ Create "x"" row once nothing matches - closes on selection, Escape, or an outside click. `addTag()` also now strips a leading `#` same as desktop's `TrimStart('#')`. |
| 145 | Redesign the task editor's metadata layout on Web (busy/unorganized on mobile) | Web | | P3 | S | Visual Design | **Done** | Researched task-editor UX patterns (Todoist's task-view redesign, Material chip-vs-form-field guidance, Google Tasks/Apple Reminders' compact icon-led due/repeat) before changing anything - every source converged on grouping metadata into one strip of compact pills instead of separate labeled form rows. Pin/Trash/Delete lost their borders (plain `.icon-btn`/borderless ghost, matching the rest of the app) and moved right of a spacer, leaving Done alone on the left. Due/Repeat dropped their text labels for calendar/repeat icons and now share `.tag-chip`'s pill styling, merged with the tags into one `.editor-meta-row` instead of three separate rows. |
| 146 | Fix mobile header: empty circle, undersized back button/logo | Web | | P3 | S | Visual Design | **Done** | Reported live from a phone screenshot: an unlabeled circle sitting between the back button and the brand logo, plus the back button/logo feeling too small. Root cause of the circle: `.toolbar-group-leading` only ever holds `sidebar-drawer-btn` (tablet-only) and the menu button (desktop-only) - both hidden below 768px, so the pill rendered empty. Hidden outright on mobile. Sizing brought in line with Material 3/iOS HIG mobile top-app-bar conventions: icon glyphs 18px→23px, brand logo 22px→30px, back button pulled toward the leading edge. Verified via computed-style inspection at mobile/tablet viewport widths (no real device available). |
| 147 | Add a "Mark Done" action to the task editor's overflow menu on Web | Web | | P3 | S | UX / Interaction | **Done** | Reported live: "there is not a way to close/done the task" on the mobile task-detail view - the Done checkbox lives in the metadata pill row (`#editor-done-field`, see #145), easy to miss when a task's just been opened from the list. New "Mark Done"/"Mark Not Done" entry in `#editor-more-dropdown` above "Move to Trash" gives the same `toggleDone`/`renderEditor` action a second, more discoverable home; doesn't touch desktop, whose detail panel already shows Done directly rather than behind a menu. |
| 148 | Sync saved smart filters (Views) across Desktop/Web/mobile via Drive | Both | | P2 | M | Core Feature | Done | User-requested parity pass after #82 shipped Web-only. Two parts, both now shipped: **(1) Desktop port** - views live in `AppState` (`SavedViews`/`DeletedSavedViewIds`, mirroring `DeletedTasks`'s tombstone pattern), synced via Drive like tasks/tags already are (user's explicit call over a `Settings`-local/unsynced copy); merge logic is `SavedViewSyncMerge.Merge` (`Services/SavedViewSyncMerge.cs`); new `SidebarFilterKind.View` + a third sidebar `ViewItems` section, `SaveViewCommand`/`DeleteViewCommand` + `SaveViewPromptWindow` (cloned from `LinkPromptWindow`). Selecting a view sets the visible `SearchText` to its query (a deliberate Desktop-only UX simplification, not a wire-format difference - see #82's row). **(2) Web wiring** - views moved off the original `tasky-saved-views` localStorage key onto `appState.SavedViews`/`DeletedSavedViewIds` (PascalCase `Id`/`Label`/`Query`, matching Desktop's C# field names exactly, replacing the old lowercase `{id,label,query}` shape), with a `mergeSavedViews` function in `sync.js` mirroring the C# merge, wired into `mergeFromRemote`/`loadFromDriveWithRetry`, plus a one-time `migrateLegacyLocalViewsIfNeeded()` that upgrades an existing user's old localStorage views into the synced file on next load. No Desktop code changes were needed for part 2 - the wire format already matched once Web adopted PascalCase. Verified: 6 new C# `SavedViewSyncMergeTests` + 6 new JS `mergeSavedViews` tests in `parity.test.js` (225/225 C# suite, 75/75 JS suite), `check-cache-version.js` clean, and live browser verification of the Web half (migration, save, select, delete all confirmed working via a mocked-auth local server run). **Not verified**: an actual live two-device Drive round-trip (real Google credentials + two real clients needed, not possible in this environment) - your own end-to-end check once this ships. |
| 149 | Fix misleading sign-in screen flash right after a real Google sign-in | Web | | P2 | S | Bug Fix | Done | Reported live: "after login the sign in still shows for a few moments then disappears... strange that this is displayed even after I signed in." Root cause: `boot()` (`app.js`) unconditionally set the sign-in button to its normal ready state (enabled, "Sign in with Google") before awaiting `handleRedirectReturn()`, which does a real network round-trip (code-for-token exchange + a userinfo fetch, possibly a Cloud Run cold start) - `#signin-screen` starts visible with no JS needed to show it, so for that whole window the screen looked completely unchanged from its pre-sign-in state right after the user had just finished Google's own consent screen. Fix: detect a redirect return up front (cheap, synchronous - just checks for `code`/`error` URL params) and show a distinct disabled "Completing sign-in…" button state during the exchange instead, reverting to the normal ready state only on an actual error. Verified live via a mocked slow (10s) token exchange: confirmed the button shows the new "Completing sign-in…" state mid-flight instead of the old misleading ready state, and still transitions correctly to the signed-in app view once the exchange resolves. |
| 150 | Add "Check for Updates" self-update to Desktop | Desktop | | P2 | M | Core Feature | **Done** | User-requested alternative to a full installer/auto-updater, specifically to avoid repeat SmartScreen prompts and a code-signing cert - see the Status section above for the full writeup. New `Services/UpdateService.cs`, `UpdateAvailableWindow`, `Help → Check for Updates`, and `Settings.AutoCheckForUpdates`/`Settings.LastUpdateCheckUtc`. |

## Provenance

Built from an independent Claude review of the Tasky codebase (architecture read via the project's
`graphify-out/` knowledge graph, then four parallel deep-dive agents each for Desktop and Web),
reconciled against a parallel Gemini/Antigravity review across several rounds. Four Gemini/Antigravity
claims about Tasky Web that didn't match the actual code — the mobile OAuth flow, `touch-action`, the
keyboard shortcuts modal, and OS theme sync — were verified false and removed before this version.
No code was changed in producing this document.

**2026-08-25 merge with review_tasks.md:** a separate architecture/code-quality pass (`review_tasks.md`)
was reconciled into this table the same day. Its 5 desktop + 2 web Critical/High items were already
resolved and are reflected in rows #15, #30, #34/#122, #85(E2EE unaffected) and the Web section's two
`[x]` items; #30 in particular had been implemented but this table still showed it Active until this
merge — fixed. Its one still-open Critical/High item (JS/C# parity tests) is row #89, bumped to P1.
Everything else still open in `review_tasks.md` became rows #118–142. `review_tasks.md` is kept as the
detailed file/line backing for those rows rather than duplicated here.
