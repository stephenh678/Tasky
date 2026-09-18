// Mirrors the desktop app's C# data model byte-for-byte in JSON shape, so the same .tasky file
// on Google Drive round-trips cleanly between Tasky (WPF) and Tasky Web with no conversion step.
// Source of truth: Tasky/Models/{TaskItem,NoteBlock,ChecklistItem,TaskLink}.cs,
// Tasky/Services/AppState.cs, Tasky/Models/TaskSyncRecord.cs.
//
// Two things System.Text.Json does that are easy to get wrong from JS:
// - Property names are exact-case PascalCase (no camelCase policy applied).
// - Enums serialize as their underlying int, not as strings.

export const NoteBlockType = Object.freeze({ Text: 0, Photo: 1, Link: 2, File: 3, Checklist: 4 });
export const RecurrenceRule = Object.freeze({ None: 0, Daily: 1, Weekly: 2, Monthly: 3, Yearly: 4 });
export const TaskPriority = Object.freeze({ None: 0, Low: 1, Medium: 2, High: 3 });

// ROADMAP.md #132: raised from 500 to match TaskItem.cs's MaxTextLength - keep these in sync, or
// a sync merge can silently reshape a title clamped differently on each platform.
const MAX_TASK_TEXT = 2000;
const MAX_BLOCK_TEXT = 10000;
const MAX_LINK_LABEL = 500;
// Split out from MAX_TASK_TEXT (which newChecklistItem used to share) - Models/ChecklistItem.cs
// has its own separate, still-500 MaxTextLength, so reusing the task-title constant here would
// have silently let web checklist items grow past what desktop accepts for the same field.
const MAX_CHECKLIST_ITEM_TEXT = 500;

export function newGuid() {
  return crypto.randomUUID();
}

// DueDate is a deliberately naive, local wall-clock value on both platforms (a task "due Friday
// at 5pm" means 5pm wherever the user is, not a fixed instant) - System.Text.Json writes it with
// NO trailing 'Z' or UTC offset when its Kind is Unspecified, and JS's native Date parser is
// spec-required to treat that shape as local time too, so this is a safe round-trip AS LONG AS
// the fractional-seconds part never trips up the parser. .NET can write up to 7 fractional digits
// (100ns ticks); some JS engines are strict about the 3-digit millisecond form in ISO strings, so
// rather than trust `new Date(string)` across browsers, parse by hand and build the Date from
// local-time components explicitly.
const DATE_RE = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/;

export function parseDotNetDate(value) {
  if (!value) return null;
  const m = DATE_RE.exec(value);
  if (!m) {
    // Not the plain local-time shape we expect (e.g. has a 'Z'/offset, which is what
    // nowDotNet()-produced sync timestamps carry - see its own comment) - fall back to the
    // native parser, which handles those correctly, rather than silently dropping a valid date.
    const d = new Date(value);
    return isNaN(d.getTime()) ? null : d;
  }
  const [, y, mo, d, h, mi, s, frac] = m;
  const ms = frac ? Math.round(parseInt(frac.slice(0, 3).padEnd(3, '0'), 10)) : 0;
  return new Date(Number(y), Number(mo) - 1, Number(d), Number(h), Number(mi), Number(s), ms);
}

// Always emits 7 fractional digits (padding ms out to ticks) - a form .NET's reader accepts
// even though its own writer trims trailing zeros; round-trips fine either direction. Only for
// DueDate - see parseDotNetDate's comment on why that field deliberately stays naive/local.
export function formatDotNetDate(date) {
  const pad = (n, len = 2) => String(n).padStart(len, '0');
  const y = date.getFullYear();
  const mo = pad(date.getMonth() + 1);
  const d = pad(date.getDate());
  const h = pad(date.getHours());
  const mi = pad(date.getMinutes());
  const s = pad(date.getSeconds());
  const frac = pad(date.getMilliseconds(), 3) + '0000';
  return `${y}-${mo}-${d}T${h}:${mi}:${s}.${frac}`;
}

// Used exclusively for sync-relevant timestamps (ModifiedAt, CreatedAt, tombstone Timestamp - see
// sync.js and app.js's callers), never for DueDate. Last-write-wins merge decisions compare these
// across devices that may be in different time zones, so - unlike DueDate above - they need to
// represent one unambiguous instant regardless of where they were written. A bare local-time
// string (what this used to emit) can't do that: a value written on this browser's clock, read on
// a desktop in a different time zone, was silently treated as if it were already in the desktop's
// own local time, with no correction - the cross-device counterpart to what UtcDateTimeConverter
// now fixes on the desktop side. `toISOString()`'s trailing 'Z' makes every sync timestamp this
// app writes explicit and unambiguous; parseDotNetDate's native-parser fallback already handles
// 'Z' (and legacy desktop-written UTC-offset) strings correctly.
export function nowDotNet() {
  return new Date().toISOString();
}

export function newTaskLink({ label = '', url = '' } = {}) {
  return { Id: newGuid(), Label: label, Url: url };
}

export function newChecklistItem({ text = '', isChecked = false } = {}) {
  return { Id: newGuid(), Text: clamp(text.trim(), MAX_CHECKLIST_ITEM_TEXT), IsChecked: isChecked };
}

export function newNoteBlock(type, fields = {}) {
  return {
    Id: newGuid(),
    Type: type,
    Text: clamp(fields.text ?? '', MAX_BLOCK_TEXT),
    Rtf: fields.rtf ?? '',
    PhotoPath: fields.photoPath ?? '',
    FileName: fileNameFromPath(fields.photoPath ?? ''),
    Url: fields.url ?? '',
    LinkLabel: clamp(fields.linkLabel ?? '', MAX_LINK_LABEL),
    ChecklistItems: fields.checklistItems ?? [],
  };
}

export function newTaskItem({ text = '' } = {}) {
  const now = nowDotNet();
  return {
    Id: newGuid(),
    CreatedAt: now,
    ModifiedAt: now,
    IsPinned: false,
    // Manual (drag-and-drop) position. Callers that have the current list pass the real value via
    // nextSortOrder(); the 0 here is only the standalone default. Omitting the field entirely (as
    // this did before) isn't harmless: desktop's JsonSerializer reads a missing int as 0, so every
    // task made on Web or a phone landed at the very top of desktop's manual order.
    SortOrder: 0,
    Text: clamp(text, MAX_TASK_TEXT),
    IsDone: false,
    IsClosed: false,
    DueDate: null,
    Recurrence: RecurrenceRule.None,
    RecurrenceInterval: 1,
    Priority: TaskPriority.None,
    Notes: '',
    Links: [],
    Photos: [],
    Body: [newNoteBlock(NoteBlockType.Text, {})],
    Tags: [],
  };
}

export function newAppState() {
  // TasksOrderModifiedAt mirrors AppState.cs: when any device last changed the manual ordering.
  // null means "never reordered" and loses to any real timestamp in mergeTaskOrder.
  return { Tasks: [], DeletedTasks: [], SavedViews: [], DeletedSavedViewIds: [], TasksOrderModifiedAt: null };
}

// The SortOrder a newly created task should get: the end of the list, matching what desktop's
// MainViewModel does at its three creation sites (`AllTasks.Max(t => t.SortOrder) + 1`). New tasks
// belong at the bottom of a manual arrangement, not the top.
export function nextSortOrder(tasks) {
  if (!Array.isArray(tasks) || tasks.length === 0) return 0;
  // reduce, not Math.max(...spread): the spread passes one argument per task, which throws
  // RangeError once a synced file grows past the engine's argument limit.
  let max = 0;
  for (const t of tasks) {
    const order = Number(t.SortOrder) || 0;
    if (order > max) max = order;
  }
  return max + 1;
}

/**
 * The index a dragged task should land on, given its current index, the drop target's index and
 * which half of the target row was dropped on. Split out of app.js's reorderTask so the arithmetic
 * - the piece most likely to drift from MainViewModel.ReorderTask - is reachable from the parity
 * tests; app.js still owns the DOM and appState side of the drag.
 *
 * The returned index is interpreted AFTER the source has been removed from the list, matching
 * ObservableCollection.Move's contract on the desktop side.
 */
export function reorderTargetIndex(sourceIndex, targetIndex, insertAfter, length) {
  let newIndex = targetIndex;
  if (insertAfter) {
    if (sourceIndex > targetIndex) newIndex = targetIndex + 1;
  } else if (sourceIndex < targetIndex) {
    newIndex = targetIndex - 1;
  }
  return Math.max(0, Math.min(newIndex, length - 1));
}

/**
 * Pin changes implied by dropping `source` onto `target` - pinned tasks always sort first, so
 * dropping above the pinned block's floor has to mean "pin me" and dropping below it "unpin me",
 * or the task visibly snaps back to where it was. Mirrors the two branches at the top of
 * MainViewModel.ReorderTask. Returns the pin state the source should end up with.
 */
export function pinStateAfterReorder(sourcePinned, targetPinned, insertAfter) {
  if (targetPinned && !sourcePinned && !insertAfter) return true;
  if (!targetPinned && sourcePinned && insertAfter) return false;
  return sourcePinned;
}

export function newTaskSyncRecord(taskId, timestamp = nowDotNet()) {
  return { TaskId: taskId, Timestamp: timestamp };
}

// Mirrors MainViewModel.cs's NextDueDate switch exactly - ROADMAP.md #31's interval multiplies the
// step (Weekly + interval 2 = every 2 weeks) instead of recurrence being fixed at "every 1".
export function nextDueDate(from, rule, interval = 1) {
  const d = new Date(from);
  switch (rule) {
    case RecurrenceRule.Daily:
      d.setDate(d.getDate() + interval);
      return d;
    case RecurrenceRule.Weekly:
      d.setDate(d.getDate() + 7 * interval);
      return d;
    case RecurrenceRule.Monthly:
      return addMonthsClamped(d, interval);
    case RecurrenceRule.Yearly:
      return addMonthsClamped(d, 12 * interval);
    default:
      return d;
  }
}

// DateTime.AddMonths/AddYears clamp to the target month's last day (Jan 31 + 1 month = Feb 28);
// Date.setMonth/setFullYear roll over instead (Mar 3), which skipped a month and shifted the
// series' day-of-month for good.
function addMonthsClamped(date, months) {
  const d = new Date(date);
  const day = d.getDate();
  d.setDate(1);
  d.setMonth(d.getMonth() + months);
  d.setDate(Math.min(day, new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate()));
  return d;
}

// Mirrors MainViewModel.cs's RecurrenceAnchor exactly (ROADMAP.md #31): advancing straight from a
// stale DueDate meant completing a long-overdue recurring task (e.g. a daily task overdue by 2
// weeks) spawned a next occurrence that was still overdue, rather than one due tomorrow. Clamp the
// anchor date to today when the task was already overdue, but keep its time-of-day (e.g. a "@5pm"
// reminder stays at 5pm) - only the date component was stale, not the time.
export function recurrenceAnchor(dueDate) {
  const anchor = dueDate ? parseDotNetDate(dueDate) : new Date();
  const today = new Date();
  const anchorDateOnly = new Date(anchor.getFullYear(), anchor.getMonth(), anchor.getDate());
  const todayDateOnly = new Date(today.getFullYear(), today.getMonth(), today.getDate());
  if (anchorDateOnly < todayDateOnly) {
    const clamped = new Date(anchor);
    clamped.setFullYear(today.getFullYear(), today.getMonth(), today.getDate());
    return clamped;
  }
  return anchor;
}

// Mirrors MainViewModel.cs's SpawnNextOccurrence: same title/recurrence/interval/tags, due date advanced.
export function spawnNextOccurrence(completed) {
  const next = newTaskItem({ text: completed.Text });
  const base = recurrenceAnchor(completed.DueDate);
  next.DueDate = formatDotNetDate(nextDueDate(base, completed.Recurrence, completed.RecurrenceInterval));
  next.Recurrence = completed.Recurrence;
  // Old data synced from before ROADMAP.md #31 has no RecurrenceInterval at all - default to 1
  // (the prior fixed behavior) rather than propagating `undefined` onto the spawned task.
  next.RecurrenceInterval = completed.RecurrenceInterval ?? 1;
  next.Tags = [...completed.Tags];
  return next;
}

function clamp(str, max) {
  return str.length > max ? str.slice(0, max) : str;
}

// Pasting an image directly into desktop's rich-text editor embeds it inline in a Text block's
// Rtf (<Image UriSource="...">) rather than creating a separate Photo block - see editor.js's
// extractInlineImageFileNames for the same detection used to actually render one. Shared here so
// the task list's row indicators and quick filters count these as "has a photo" too, matching
// what a user actually sees when they open the task.
export function blockHasInlineImage(block) {
  return block.Type === NoteBlockType.Text && !!block.Rtf && block.Rtf.includes('UriSource=');
}

// Same idea for a non-image file attached via desktop's Insert File toolbar button - embedded
// inline as a custom "file card" Grid widget rather than a separate File block. Grid is also used
// for the image container, tagged "ImageContainer" rather than a path, so that's excluded here
// too (see editor.js's extractInlineFileNames for the exact match this mirrors).
export function blockHasInlineFile(block) {
  if (block.Type !== NoteBlockType.Text || !block.Rtf) return false;
  return /<Grid[^>]*\sTag="(?!ImageContainer")/.test(block.Rtf);
}

// Mirrors Models/TaskMediaHelper.cs's HasLink: a Link block, any block carrying a Url, a hyperlink
// embedded in desktop-authored Rtf, or a bare http(s)://, www. address typed into note text.
// The legacy top-level Links list (pre-Body files) counts too, same as desktop. Without the Rtf
// and Text checks, a task whose only link was typed or pasted on desktop showed no link badge
// here and never matched has:link, while the same task did both on desktop.
export function taskHasLink(task) {
  if (!task) return false;
  if (Array.isArray(task.Links) && task.Links.length > 0) return true;
  return (task.Body ?? []).some((block) => {
    if (block.Type === NoteBlockType.Link || (block.Url && block.Url.trim())) return true;
    if (block.Rtf && /<Hyperlink|NavigateUri/i.test(block.Rtf)) return true;
    return !!block.Text && /https?:\/\/|www\./i.test(block.Text);
  });
}

// Mirrors TaskMediaHelper.HasChecklist: a Checklist block, any block with checklist items, or a
// desktop-authored Rtf paragraph containing inline <CheckBox> controls.
export function taskHasChecklist(task) {
  if (!task) return false;
  return (task.Body ?? []).some((block) => {
    if (block.Type === NoteBlockType.Checklist) return true;
    if (Array.isArray(block.ChecklistItems) && block.ChecklistItems.length > 0) return true;
    return !!block.Rtf && /<CheckBox/i.test(block.Rtf);
  });
}

// Port of TaskMediaHelper.GetChecklistProgress - {completed, total} across every checklist in the
// task, counting both real Checklist blocks and the <CheckBox .../> runs desktop embeds inline in
// a Text block's Rtf, so a task authored either way reports the same numbers on both platforms.
export function checklistProgress(task) {
  if (!task) return { completed: 0, total: 0 };
  let completed = 0;
  let total = 0;
  for (const block of task.Body ?? []) {
    if (Array.isArray(block.ChecklistItems) && block.ChecklistItems.length > 0) {
      total += block.ChecklistItems.length;
      completed += block.ChecklistItems.filter((ci) => ci.IsChecked).length;
    } else if (block.Rtf && /<CheckBox/i.test(block.Rtf)) {
      for (const match of block.Rtf.matchAll(/<CheckBox\b([^>]*)>/gi)) {
        total++;
        if (/IsChecked\s*=\s*"True"/i.test(match[1])) completed++;
      }
    }
  }
  return { completed, total };
}

// Fills in whatever a task read from a .tasky file might be missing, in place, and returns it.
// A pre-Body legacy desktop file, or a hand-edited one, can lack Tags/Body/Priority entirely -
// every render path here assumes those exist (renderList's `t.Tags.some(...)` threw and blanked
// the whole app on such a file). Mirrors what the desktop model's property initializers give a
// deserialized TaskItem for free; sync.js's applyTaskFields already guards the merge-update path,
// this covers load and merge-add.
export function normalizeTask(task) {
  if (!Array.isArray(task.Tags)) task.Tags = [];
  if (!Array.isArray(task.Body)) task.Body = [];
  task.Text = typeof task.Text === 'string' ? task.Text : '';
  task.IsDone = !!task.IsDone;
  task.IsClosed = !!task.IsClosed;
  task.IsPinned = !!task.IsPinned;
  task.DueDate ??= null;
  task.Recurrence = Number(task.Recurrence) || RecurrenceRule.None;
  task.RecurrenceInterval = Math.max(1, Number(task.RecurrenceInterval) || 1);
  task.Priority = Number(task.Priority) || TaskPriority.None;
  // A file written by desktop before drag-reordering existed, or by an older Tasky Web build, has
  // no SortOrder at all - coerce to 0 so comparisons and Math.max never see undefined/NaN.
  task.SortOrder = Number(task.SortOrder) || 0;
  for (const block of task.Body) {
    block.Type = Number(block.Type) || NoteBlockType.Text;
    if (block.Type === NoteBlockType.Checklist && !Array.isArray(block.ChecklistItems)) block.ChecklistItems = [];
  }
  return task;
}

function fileNameFromPath(path) {
  if (!path) return '';
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] ?? '';
}

// Every attachment filename a Text block's Rtf references inline - the exact shape
// TaskMediaHelper.CollectReferencedFileNames matches on desktop. A pasted image is an
// <Image UriSource="..."> (the file lives in the InlineImages folder); an Insert-File chip is a
// <Grid Tag="<local path>"> (Attachments folder). The Grid Tag is also used for a handful of
// fixed internal markers rather than a path; those aren't real attachment references.
const INLINE_IMAGE_RE = /UriSource="([^"]+)"/g;
const INLINE_FILE_RE = /<Grid[^>]*\sTag="([^"]+)"/g;
const NON_FILE_TAG_MARKERS = new Set(['ImageContainer', 'CardBody', 'DeleteAttachmentBtn']);

export function extractInlineImageFileNames(rtf) {
  if (!rtf) return [];
  const names = [];
  for (const m of rtf.matchAll(INLINE_IMAGE_RE)) {
    const name = fileNameFromPath(m[1]);
    if (name) names.push(name);
  }
  return names;
}

export function extractInlineFileNames(rtf) {
  if (!rtf) return [];
  const names = [];
  for (const m of rtf.matchAll(INLINE_FILE_RE)) {
    if (NON_FILE_TAG_MARKERS.has(m[1])) continue;
    const name = fileNameFromPath(m[1]);
    if (name) names.push(name);
  }
  return names;
}

// Every attachment filename a task references anywhere - its Photo/File blocks plus anything
// embedded inline in a Text block's Rtf. Mirrors what desktop's CleanupTaskAttachments feeds
// through ExtractTaskMediaFilenames, so both platforms agree on what "this task's files" means
// when deciding what a permanent delete may remove from Drive.
export function collectTaskFileNames(task, into = new Set()) {
  for (const block of task.Body ?? []) {
    if (block.FileName) into.add(block.FileName);
    else if (block.PhotoPath) into.add(fileNameFromPath(block.PhotoPath));
    if (block.Type === NoteBlockType.Text && block.Rtf) {
      for (const name of extractInlineImageFileNames(block.Rtf)) into.add(name);
      for (const name of extractInlineFileNames(block.Rtf)) into.add(name);
    }
  }
  into.delete('');
  return into;
}

// Mirrors Services/QuickEntryParser.cs exactly (see its comment for the "why a fixed token
// syntax, not full NLP" rationale) - #tag, !due:<value>, @<time> parsed out of quick-add text.
// A token is only ever consumed when it actually matches one of these forms, so an unrecognized
// "!due:whenever" or an email-address-shaped "@" is left untouched in the title instead of
// silently mangled.
//
// Each token must start the string or follow whitespace. That used to be a `(?<!\S)` lookbehind,
// but regex lookbehind is a parse-time SyntaxError on Safari < 16.4 - and since this module is
// in every page's import graph, one unparseable regex literal here meant the whole app failed to
// load on those browsers with nothing but a stuck "Loading…" button (see docs/js/boot-guard.js).
// A captured `(^|\s)` prefix is the same test in universally-supported syntax; the replace
// callbacks below hand that captured prefix back so consuming a token never eats the space that
// separated it from the previous word (a lookbehind never included it in the match to begin with).
// The class below is what .NET's \w means (Unicode letters, marks, digits, connectors); JS's \w is
// ASCII-only, which split "#café" into the tag "caf" and a stray "é" left in the title.
const QUICK_ADD_TAG_RE = /(^|\s)#([\p{L}\p{Mn}\p{Nd}\p{Pc}-]+)/gu;
const QUICK_ADD_DUE_RE = /(^|\s)!due:(\S+)/gi;
const QUICK_ADD_TIME_RE = /(^|\s)@(\S+)/g;
const QUICK_ADD_TIME_TOKEN_RE = /^(\d{1,2})(?::(\d{2}))?(am|pm)$|^(\d{1,2}):(\d{2})$/i;
const QUICK_ADD_WEEKDAYS = {
  sun: 0, sunday: 0,
  mon: 1, monday: 1,
  tue: 2, tues: 2, tuesday: 2,
  wed: 3, weds: 3, wednesday: 3,
  thu: 4, thur: 4, thurs: 4, thursday: 4,
  fri: 5, friday: 5,
  sat: 6, saturday: 6,
};
// Applied when !due: is given without an @ time - e.g. "!due:tomorrow" alone due for 9 AM
// rather than midnight, so it doesn't look overdue the instant the day starts.
const QUICK_ADD_DEFAULT_DUE_HOUR = 9;

export function parseQuickAdd(input, now = new Date()) {
  let text = input ?? '';

  const tags = [];
  text = text.replace(QUICK_ADD_TAG_RE, (m, lead, tag) => {
    if (!tags.some((t) => t.toLowerCase() === tag.toLowerCase())) tags.push(tag);
    return lead;
  });

  let datePart = null;
  text = text.replace(QUICK_ADD_DUE_RE, (m, lead, token) => {
    const parsed = parseQuickAddDueToken(token, now);
    if (!parsed) return m; // unrecognized - leave it in the title rather than silently eating it
    datePart = parsed;
    return lead;
  });

  let timePart = null;
  text = text.replace(QUICK_ADD_TIME_RE, (m, lead, token) => {
    const parsed = parseQuickAddTimeToken(token);
    if (!parsed) return m;
    timePart = parsed;
    return lead;
  });

  text = text.replace(/\s{2,}/g, ' ').trim();

  // Plain-language phrases at the END of the title ("... tomorrow 3pm", "... every monday").
  const natural = parseNaturalTail(text, now, { needDate: !datePart, needTime: !timePart });
  text = natural.text;
  datePart ??= natural.date;
  timePart ??= natural.time;

  let dueDate = null;
  if (datePart) {
    const d = new Date(datePart);
    d.setHours(timePart ? timePart.hour : natural.defaultHour, timePart ? timePart.minute : 0, 0, 0);
    dueDate = formatDotNetDate(d);
  } else if (timePart) {
    const d = new Date(now);
    d.setHours(timePart.hour, timePart.minute, 0, 0);
    dueDate = formatDotNetDate(d);
  }

  return { text, dueDate, tags, recurrence: natural.recurrence, recurrenceInterval: natural.recurrenceInterval };
}

// Upcoming (agenda) view buckets. Overdue means an earlier calendar day (a task due at 9 AM today
// is still "Today" at noon, matching the Today section and isTaskOverdue in app.js).
export const AgendaGroup = Object.freeze({ Overdue: 'overdue', Today: 'today', Tomorrow: 'tomorrow', Week: 'week', Later: 'later' });
export const AGENDA_GROUP_LABELS = Object.freeze({
  overdue: 'Overdue', today: 'Today', tomorrow: 'Tomorrow', week: 'Next 7 days', later: 'Later',
});
export function agendaGroup(due, now = new Date()) {
  const dayMs = 24 * 60 * 60 * 1000;
  const days = Math.round((startOfDay(due) - startOfDay(now)) / dayMs);
  if (days < 0) return AgendaGroup.Overdue;
  if (days === 0) return AgendaGroup.Today;
  if (days === 1) return AgendaGroup.Tomorrow;
  if (days <= 7) return AgendaGroup.Week;
  return AgendaGroup.Later;
}

// Preview wording for a parsed repeat - mirrors QuickEntryParser.DescribeRecurrence.
export function describeRecurrence(rule, interval = 1) {
  const unit = { [RecurrenceRule.Daily]: 'day', [RecurrenceRule.Weekly]: 'week', [RecurrenceRule.Monthly]: 'month', [RecurrenceRule.Yearly]: 'year' }[rule];
  if (!unit) return null;
  if (interval > 1) return `repeats every ${interval} ${unit}s`;
  return rule === RecurrenceRule.Daily ? 'repeats daily' : `repeats ${unit}ly`;
}

// Plain-language scheduling, mirrored by Services/QuickEntryParser.cs ParseNaturalTail. Only a
// phrase at the very END of the title is read - "Call mom tomorrow 3pm" is due tomorrow at 3, but
// "Call Tuesday about the budget" is left alone, which is what keeps this from misreading ordinary
// words the way free-text date parsing does. Phrases may stack in any order ("every monday 10am"),
// and a phrase is never consumed if it would leave the title empty ("Tomorrow" stays a title).
//   time:   3pm · 3:30 pm · 15:00 · at 9am
//   date:   today · tonight (8 PM unless a time is given) · tomorrow/tmrw · [on|due] fri ·
//           next fri (never today) · in 3 days · in 2 weeks
//   repeat: daily · weekly · monthly · yearly · every day|week|month|year · every 2 weeks ·
//           every monday (weekly, starting that day)
// A repeat with no date starts today, so the series has an anchor.
const NATURAL_WEEKDAYS = 'sunday|monday|tuesday|tues|tue|wednesday|weds|wed|thursday|thurs|thur|thu|friday|fri|saturday|sat|sun|mon';
const NATURAL_TIME_TAIL_RE = /\s(?:at\s+)?([0-9]{1,2}(?::[0-9]{2})?\s?(?:am|pm)|[0-9]{1,2}:[0-9]{2})$/i;
const NATURAL_REPEAT_TAIL_RE = new RegExp(String.raw`\s(?:every\s+(?:([0-9]{1,2})\s+)?(days?|weeks?|months?|years?|${NATURAL_WEEKDAYS})|(daily|weekly|monthly|yearly))$`, 'i');
const NATURAL_DATE_TAIL_RE = new RegExp(String.raw`\s(?:(?:on|due)\s+)?(today|tonight|tomorrow|tmrw|next\s+(${NATURAL_WEEKDAYS})|(${NATURAL_WEEKDAYS})|in\s+([0-9]{1,3})\s+(days?|weeks?))$`, 'i');
const NATURAL_REPEAT_UNITS = { day: RecurrenceRule.Daily, week: RecurrenceRule.Weekly, month: RecurrenceRule.Monthly, year: RecurrenceRule.Yearly };
const NATURAL_TONIGHT_HOUR = 20;

function weekdayOffset(today, name, { excludeToday = false } = {}) {
  const offset = (QUICK_ADD_WEEKDAYS[name.toLowerCase()] - today.getDay() + 7) % 7;
  return offset === 0 && excludeToday ? 7 : offset;
}

function parseNaturalTail(input, now, { needDate, needTime }) {
  let text = input;
  let date = null;
  let time = null;
  let recurrence = RecurrenceRule.None;
  let recurrenceInterval = 1;
  let defaultHour = QUICK_ADD_DEFAULT_DUE_HOUR;
  let repeatWeekday = null;
  const today = startOfDay(now);

  const take = (re) => {
    const m = re.exec(text);
    if (!m || !text.slice(0, m.index).trim()) return null;
    return m;
  };

  for (let changed = true, guard = 0; changed && guard < 4; guard++) {
    changed = false;
    let m;
    if (needTime && !time && (m = take(NATURAL_TIME_TAIL_RE))) {
      const parsed = parseQuickAddTimeToken(m[1].replace(/\s+/g, ''));
      if (parsed) {
        time = parsed;
        text = text.slice(0, m.index).trimEnd();
        changed = true;
        continue;
      }
    }
    if (recurrence === RecurrenceRule.None && (m = take(NATURAL_REPEAT_TAIL_RE))) {
      const [, count, unit, adverb] = m;
      const word = (unit ?? adverb).toLowerCase();
      const isWeekday = word in QUICK_ADD_WEEKDAYS;
      if (!(isWeekday && count)) {
        const base = adverb ? word.replace(/ly$/, '').replace(/^dai$/, 'day') : word.replace(/s$/, '');
        recurrence = isWeekday ? RecurrenceRule.Weekly : NATURAL_REPEAT_UNITS[base];
        recurrenceInterval = count ? Math.min(Math.max(Number(count), 1), 30) : 1;
        if (isWeekday) repeatWeekday = word;
        text = text.slice(0, m.index).trimEnd();
        changed = true;
        continue;
      }
    }
    if (needDate && !date && (m = take(NATURAL_DATE_TAIL_RE))) {
      const [, phrase, nextDay, day, count, unit] = m;
      const lower = phrase.toLowerCase();
      if (lower === 'today') date = today;
      else if (lower === 'tonight') { date = today; defaultHour = NATURAL_TONIGHT_HOUR; }
      else if (lower === 'tomorrow' || lower === 'tmrw') date = addDays(today, 1);
      else if (nextDay) date = addDays(today, weekdayOffset(today, nextDay, { excludeToday: true }));
      else if (day) date = addDays(today, weekdayOffset(today, day));
      else date = addDays(today, Number(count) * (unit.toLowerCase().startsWith('week') ? 7 : 1));
      text = text.slice(0, m.index).trimEnd();
      changed = true;
    }
  }

  if (recurrence !== RecurrenceRule.None && needDate && !date) {
    date = repeatWeekday ? addDays(today, weekdayOffset(today, repeatWeekday)) : today;
  }
  return { text, date, time, recurrence, recurrenceInterval, defaultHour };
}

function parseQuickAddDueToken(token, reference) {
  const lower = token.toLowerCase();
  if (lower === 'today') return startOfDay(reference);
  if (lower === 'tomorrow') return startOfDay(addDays(reference, 1));
  if (lower in QUICK_ADD_WEEKDAYS) {
    const target = QUICK_ADD_WEEKDAYS[lower];
    const today = startOfDay(reference);
    // Nearest occurrence of that weekday, counting today as valid (so "!due:tue" typed on a
    // Tuesday means today, not a week out).
    const offset = (target - today.getDay() + 7) % 7;
    return addDays(today, offset);
  }
  // Explicit numeric dates only: yyyy-mm-dd (also with slashes), m/d/yyyy and d.m.yyyy - all
  // built from parts rather than `new Date(token)`. The ISO form is parsed as UTC midnight by
  // spec (wrong local day near a timezone boundary), and the native parser's fallback for
  // anything else was far too generous: Chrome read "!due:march" as 1 Mar 2001 and "!due:5" as
  // 1 Jan 2005, silently attaching a nonsense due date where desktop's DateOnly.TryParse
  // (invariant culture) rejects both. An unrecognized token is now left in the title instead,
  // exactly like desktop.
  const numeric =
    /^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$/.exec(token) // y-m-d
    ?? swapToYmd(/^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(token), 3, 1, 2) // m/d/yyyy
    ?? swapToYmd(/^(\d{1,2})\.(\d{1,2})\.(\d{4})$/.exec(token), 3, 2, 1); // d.m.yyyy
  if (!numeric) return null;
  const [, y, mo, d] = numeric.map(Number);
  if (mo < 1 || mo > 12 || d < 1 || d > 31) return null;
  const date = new Date(y, mo - 1, d);
  // new Date() rolls an impossible day forward (31 Feb -> 3 Mar); reject that rather than guess.
  return date.getMonth() === mo - 1 && date.getDate() === d ? date : null;
}

function swapToYmd(match, yi, mi, di) {
  return match ? [match[0], match[yi], match[mi], match[di]] : null;
}

function parseQuickAddTimeToken(token) {
  const m = QUICK_ADD_TIME_TOKEN_RE.exec(token);
  if (!m) return null;
  if (m[3]) {
    const hour12 = Number(m[1]);
    const minute = m[2] ? Number(m[2]) : 0;
    if (hour12 < 1 || hour12 > 12 || minute < 0 || minute > 59) return null;
    const isPm = m[3].toLowerCase() === 'pm';
    return { hour: (hour12 % 12) + (isPm ? 12 : 0), minute };
  }
  const hour = Number(m[4]);
  const minute = Number(m[5]);
  if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return null;
  return { hour, minute };
}

function startOfDay(d) {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}
function addDays(d, n) {
  const r = new Date(d);
  r.setDate(r.getDate() + n);
  return r;
}

export function escapeXml(str) {
  if (!str) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

// --- Note formatting: desktop FlowDocument XAML <-> the web editor's contentEditable HTML ---------
//
// Desktop stores a text block's formatting as FlowDocument XAML (XamlWriter.Save) and loads it with
// XamlReader.Parse, which rejects anything that isn't well-formed XAML - an HTML-only entity like
// &nbsp;, a stray <span>, or bare text sitting directly inside <FlowDocument> makes the whole
// document fail to parse, and desktop then falls back to the block's plain Text (formatting gone).
// The regex converter this replaced produced exactly those whenever a web user pressed Enter
// (Chrome writes "line one<div>line two</div>") or typed two spaces (&nbsp;). Both directions now
// go through one small, tolerant HTML tree parser instead, so the output is always well-formed:
// every text run is escaped, every inline sits inside a Paragraph, and only elements both editors
// understand survive. Pure string code (no DOMParser) so node:test can cover it.

const VOID_HTML_TAGS = new Set(['br', 'img', 'hr', 'input', 'meta', 'link', 'wbr', 'col', 'source']);
const BLOCK_HTML_TAGS = new Set([
  'p', 'div', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'blockquote', 'pre', 'section', 'article',
  'header', 'footer', 'ul', 'ol', 'li', 'table', 'thead', 'tbody', 'tfoot', 'tr', 'td', 'th',
]);
const DROP_WITH_CONTENT_TAGS = new Set(['script', 'style', 'head', 'title', 'template']);
const HTML_TOKEN_RE = /<!--[\s\S]*?-->|<(\/?)([a-zA-Z][a-zA-Z0-9]*)((?:[^>"']|"[^"]*"|'[^']*')*)>|([^<]+)|(<)/g;
const HTML_ATTR_RE = /([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g;
const NAMED_ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };
const SAFE_HREF_RE = /^(https?:|mailto:)/i;

export function decodeHtmlEntities(text) {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, body) => {
    if (body[0] === '#') {
      const code = body[1] === 'x' || body[1] === 'X' ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
      return Number.isFinite(code) && code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : m;
    }
    return NAMED_ENTITIES[body.toLowerCase()] ?? m;
  });
}

function parseHtmlAttributes(raw) {
  const attrs = {};
  for (const m of raw.matchAll(HTML_ATTR_RE)) {
    attrs[m[1].toLowerCase()] = decodeHtmlEntities(m[2] ?? m[3] ?? m[4] ?? '');
  }
  return attrs;
}

// Builds a { tag, attrs, children } tree (text nodes are { text }). Tolerant the way browsers are:
// an unmatched closing tag is ignored, an unclosed element is closed at the end.
export function parseHtmlFragment(html) {
  const root = { tag: '#root', attrs: {}, children: [] };
  const stack = [root];
  let dropDepth = 0;
  for (const m of String(html ?? '').matchAll(HTML_TOKEN_RE)) {
    const [, closing, rawTag, rawAttrs, text, strayLt] = m;
    const top = stack[stack.length - 1];
    if (text !== undefined || strayLt !== undefined) {
      if (dropDepth === 0) top.children.push({ text: decodeHtmlEntities(text ?? '<') });
      continue;
    }
    if (!rawTag) continue; // comment
    const tag = rawTag.toLowerCase();
    const selfClosing = /\/\s*$/.test(rawAttrs);
    if (DROP_WITH_CONTENT_TAGS.has(tag)) {
      if (!selfClosing) dropDepth = Math.max(0, dropDepth + (closing ? -1 : 1));
      continue;
    }
    if (dropDepth > 0) continue;
    if (closing) {
      const at = stack.map((n) => n.tag).lastIndexOf(tag);
      if (at > 0) stack.length = at;
      continue;
    }
    const node = { tag, attrs: parseHtmlAttributes(rawAttrs), children: [] };
    top.children.push(node);
    if (!VOID_HTML_TAGS.has(tag) && !selfClosing) stack.push(node);
  }
  return root;
}

function isInlineCheck(node) {
  return node.tag === 'span' && /(^|\s)inline-check(\s|$)/.test(node.attrs.class ?? '');
}

function spanStyleFlags(node) {
  const style = (node.attrs.style ?? '').toLowerCase();
  return {
    bold: /font-weight\s*:\s*(bold|[6-9]00)/.test(style),
    italic: /font-style\s*:\s*italic/.test(style),
    underline: /text-decoration(-line)?\s*:[^;]*underline/.test(style),
  };
}

function isWhitespaceText(node) {
  return node.text !== undefined && !/\S/.test(node.text.replace(/ /g, 'x'));
}

// --- HTML -> XAML ---------------------------------------------------------------------------------

function xamlRuns(text) {
  // Run's Text attribute keeps spaces exactly (element content would be whitespace-normalised by
  // XamlReader, gluing "a <b>b</b>" into "ab"); a newline becomes a real LineBreak.
  // An attribute value that starts with "{" is a markup extension to XamlReader ("{TODO} call"
  // throws and desktop drops the whole block's formatting) - "{}" is XAML's escape for a literal.
  return text
    .split('\n')
    .map((part) => (part ? `<Run Text="${part.startsWith('{') ? '{}' : ''}${escapeXml(part)}"/>` : ''))
    .join('<LineBreak/>');
}

function xamlInline(nodes, inLink) {
  return nodes.map((n) => xamlInlineNode(n, inLink)).join('');
}

function xamlInlineNode(node, inLink) {
  if (node.text !== undefined) {
    if (/\n/.test(node.text) && isWhitespaceText(node)) return ''; // source-formatting whitespace
    return xamlRuns(node.text);
  }
  const wrap = (element, inner) => (inner ? `<${element}>${inner}</${element}>` : '');
  switch (node.tag) {
    case 'br':
      return '<LineBreak/>';
    case 'b': case 'strong':
      return wrap('Bold', xamlInline(node.children, inLink));
    case 'i': case 'em':
      return wrap('Italic', xamlInline(node.children, inLink));
    case 'u':
      return wrap('Underline', xamlInline(node.children, inLink));
    case 'a': {
      const inner = xamlInline(node.children, true);
      const href = (node.attrs.href ?? '').trim();
      if (inLink || !SAFE_HREF_RE.test(href) || !inner) return inner;
      return `<Hyperlink NavigateUri="${escapeXml(href)}">${inner}</Hyperlink>`;
    }
    case 'span': {
      if (isInlineCheck(node)) {
        const checked = node.attrs['data-checked'] === 'true';
        return `<InlineUIContainer><CheckBox IsChecked="${checked ? 'True' : 'False'}" Margin="0,0,6,0" VerticalAlignment="Center" Cursor="Hand"/></InlineUIContainer>`;
      }
      let inner = xamlInline(node.children, inLink);
      const flags = spanStyleFlags(node);
      if (flags.underline) inner = wrap('Underline', inner);
      if (flags.italic) inner = wrap('Italic', inner);
      if (flags.bold) inner = wrap('Bold', inner);
      return inner;
    }
    case 'img': case 'input': case 'hr':
      return '';
    default:
      // A block element that ended up inside inline content (e.g. <b><div>x</div></b>) - keep its
      // words on their own line rather than dropping them.
      if (BLOCK_HTML_TAGS.has(node.tag)) {
        const inner = xamlInline(node.children, inLink);
        return inner ? `<LineBreak/>${inner}` : '';
      }
      return xamlInline(node.children, inLink);
  }
}

function xamlParagraph(children, attrs = '') {
  // Browsers keep a trailing <br> in a line so it stays tall - not content.
  const inner = xamlInline(children, false).replace(/(<LineBreak\/>)+$/, '');
  return inner ? `<Paragraph${attrs}>${inner}</Paragraph>` : `<Paragraph${attrs}/>`;
}

function xamlBlocks(nodes) {
  let out = '';
  let pending = [];
  const flush = () => {
    if (pending.length && !pending.every(isWhitespaceText)) out += xamlParagraph(pending);
    pending = [];
  };
  for (const node of nodes) {
    if (node.tag && BLOCK_HTML_TAGS.has(node.tag)) {
      flush();
      out += xamlBlock(node);
    } else {
      pending.push(node);
    }
  }
  flush();
  return out;
}

function xamlBlock(node) {
  switch (node.tag) {
    case 'ul': case 'ol': {
      const marker = node.tag === 'ol' ? ' MarkerStyle="Decimal"' : '';
      let items = '';
      for (const child of node.children) {
        if (isWhitespaceText(child)) continue;
        const content = child.tag === 'li' ? xamlBlocks(child.children) : xamlBlocks([child]);
        items += `<ListItem>${content || '<Paragraph/>'}</ListItem>`;
      }
      return items ? `<List${marker}>${items}</List>` : '';
    }
    case 'table': {
      const rows = [];
      const collectRows = (n) => {
        for (const c of n.children ?? []) {
          if (c.tag === 'tr') rows.push(c);
          else if (c.tag === 'thead' || c.tag === 'tbody' || c.tag === 'tfoot') collectRows(c);
        }
      };
      collectRows(node);
      const xamlRows = rows.map((tr) => {
        const cells = tr.children
          .filter((c) => c.tag === 'td' || c.tag === 'th')
          .map((c) => `<TableCell>${xamlBlocks(c.children) || '<Paragraph/>'}</TableCell>`)
          .join('');
        return cells ? `<TableRow>${cells}</TableRow>` : '';
      }).join('');
      return xamlRows ? `<Table><TableRowGroup>${xamlRows}</TableRowGroup></Table>` : '';
    }
    case 'li': case 'thead': case 'tbody': case 'tfoot': case 'tr': case 'td': case 'th':
      return xamlBlocks(node.children); // stray structure outside its container - keep the content
    default: {
      const hasBlockChild = node.children.some((c) => c.tag && BLOCK_HTML_TAGS.has(c.tag));
      if (hasBlockChild) return xamlBlocks(node.children);
      const heading = /^h[1-3]$/.test(node.tag) ? ' FontWeight="Bold"' : '';
      return xamlParagraph(node.children, heading);
    }
  }
}

export function htmlToXaml(html, fallbackText = '') {
  let blocks = html && html.trim() ? xamlBlocks(parseHtmlFragment(html).children) : '';
  // A document of nothing but empty paragraphs is no content at all.
  if (!blocks || /^(<Paragraph\/>)+$/.test(blocks)) {
    if (!fallbackText || !fallbackText.trim()) return '';
    blocks = `<Paragraph>${xamlRuns(fallbackText)}</Paragraph>`;
  }
  return `<FlowDocument xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TextAlignment="Left">${blocks}</FlowDocument>`;
}

// --- XAML -> HTML ---------------------------------------------------------------------------------

function escapeHtmlText(str) {
  return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

const SAFE_HTML_TAGS = new Set(['p', 'br', 'strong', 'em', 'u', 'a', 'ul', 'ol', 'li', 'table', 'tbody', 'tr', 'td', 'span']);

function serializeSafeHtml(nodes) {
  return nodes.map((n) => {
    if (n.text !== undefined) return escapeHtmlText(n.text);
    const inner = serializeSafeHtml(n.children);
    if (!SAFE_HTML_TAGS.has(n.tag)) return inner;
    switch (n.tag) {
      case 'br':
        return '<br>';
      case 'a': {
        const href = (n.attrs.href ?? '').trim();
        return SAFE_HREF_RE.test(href)
          ? `<a href="${escapeHtmlText(href)}" target="_blank" rel="noopener">${inner}</a>`
          : inner;
      }
      case 'table':
        return `<table class="note-table">${inner}</table>`;
      case 'span': {
        if (!isInlineCheck(n)) return inner;
        const checked = n.attrs['data-checked'] === 'true';
        return `<span class="inline-check" contenteditable="false" data-checked="${checked}" role="checkbox" aria-checked="${checked}">${checked ? '☑' : '☐'}</span>`;
      }
      default:
        return `<${n.tag}>${inner}</${n.tag}>`;
    }
  }).join('');
}

// Allow-lists whatever the XAML mapping below produced, so markup in a synced file can never inject
// script, event handlers or a javascript: link into the editor's innerHTML.
export function sanitizeNoteHtml(html) {
  return serializeSafeHtml(parseHtmlFragment(html).children);
}

export function xamlToHtml(xaml) {
  if (!xaml || typeof xaml !== 'string' || !xaml.trim()) return '';
  if (xaml.startsWith('{\\rtf')) {
    return escapeHtmlText(xaml.replace(/\\[a-z0-9-]+ ?/gi, '').replace(/[{}]/g, '').trim());
  }

  // Remove XML declaration and FlowDocument namespaces
  let clean = xaml.replace(/<\?[^>]*\?>/g, '').replace(/xmlns(:\w+)?="[^"]*"/g, '');

  // Desktop's inline checklist boxes (RichTextBoxBehavior.CreateInlineCheckBox) become a tappable
  // token the web editor round-trips back to the same CheckBox - see htmlToXaml. Anything else in
  // an InlineUIContainer, and every BlockUIContainer (images, file cards - surfaced as separate
  // attachments by editor.js), is dropped from the text.
  clean = clean.replace(/<InlineUIContainer\b[^>]*>([\s\S]*?)<\/InlineUIContainer>/gi, (m, inner) => {
    const box = /<CheckBox\b([^>]*)/i.exec(inner);
    if (!box) return '';
    const checked = /IsChecked="True"/i.test(box[1]);
    return `<span class="inline-check" data-checked="${checked}"></span>`;
  });
  clean = clean.replace(/<BlockUIContainer\b[^>]*>[\s\S]*?<\/BlockUIContainer>/gi, '');
  // Property elements (<Paragraph.TextDecorations>, <Run.Foreground> ...) hold styling objects, not text.
  clean = clean.replace(/<(\w+)\.(\w+)\b[^>]*>[\s\S]*?<\/\1\.\2>/g, '');

  clean = clean
    .replace(/<FlowDocument[^>]*>/gi, '')
    .replace(/<\/FlowDocument>/gi, '')
    .replace(/<Paragraph\b[^>]*\/>/gi, '<p><br></p>')
    .replace(/<Paragraph\b[^>]*>/gi, '<p>')
    .replace(/<\/Paragraph>/gi, '</p>')
    .replace(/<Bold\b[^>]*>/gi, '<strong>')
    .replace(/<\/Bold>/gi, '</strong>')
    .replace(/<Italic\b[^>]*>/gi, '<em>')
    .replace(/<\/Italic>/gi, '</em>')
    .replace(/<Underline\b[^>]*>/gi, '<u>')
    .replace(/<\/Underline>/gi, '</u>')
    .replace(/<LineBreak\s*\/?>/gi, '<br>')
    .replace(/<Hyperlink\b[^>]*NavigateUri="([^"]*)"[^>]*>([\s\S]*?)<\/Hyperlink>/gi, '<a href="$1">$2</a>')
    // The \b matters: without it `<List[^>]*>` also matches `<ListItem>` (the "Item" is just more
    // [^>]*), so every list item was rewritten to a second `<ul>`.
    .replace(/<List\b[^>]*MarkerStyle="Decimal"[^>]*>/gi, '<ol>')
    .replace(/<List\b[^>]*>/gi, '<ul>')
    .replace(/<\/List>/gi, '</ul>')
    .replace(/<ListItem\b[^>]*>/gi, '<li>')
    .replace(/<\/ListItem>/gi, '</li>')
    .replace(/<Table\b[^>]*>/gi, '<table>')
    .replace(/<\/Table>/gi, '</table>')
    .replace(/<TableRowGroup\b[^>]*>/gi, '<tbody>')
    .replace(/<\/TableRowGroup>/gi, '</tbody>')
    .replace(/<TableRow\b[^>]*>/gi, '<tr>')
    .replace(/<\/TableRow>/gi, '</tr>')
    .replace(/<TableCell\b[^>]*>/gi, '<td>')
    .replace(/<\/TableCell>/gi, '</td>');

  // Runs: <Run Text="..."/> and <Run ...>content</Run>, with their inline formatting attributes.
  const styleRun = (attrs, content) => {
    let res = content;
    if (/FontWeight="(Bold|SemiBold|DemiBold|ExtraBold|UltraBold|Black|Heavy)"/i.test(attrs)) res = `<strong>${res}</strong>`;
    if (/FontStyle="Italic"/i.test(attrs)) res = `<em>${res}</em>`;
    if (/TextDecorations="Underline"/i.test(attrs)) res = `<u>${res}</u>`;
    return res;
  };
  // A leading "{}" is XAML's literal-brace escape (see xamlRuns), not part of the text.
  clean = clean.replace(/<Run\b([^>]*?)\s*\/>/gi, (_, attrs) => styleRun(attrs, (/\bText="([^"]*)"/i.exec(attrs)?.[1] ?? '').replace(/^\{\}/, '')));
  clean = clean.replace(/<Run\b([^>]*)>([\s\S]*?)<\/Run>/gi, (_, attrs, content) => styleRun(attrs, content));
  // <Span FontWeight="Bold"> etc. carry formatting too; the sanitizer drops the Span tag itself.
  // Case-sensitive on purpose (XAML is): the inline-check <span> made above must survive.
  clean = clean.replace(/<Span\b([^>]*)>([\s\S]*?)<\/Span>/g, (_, attrs, content) => styleRun(attrs, content));

  return sanitizeNoteHtml(clean).trim();
}

// --- Reminders and calendar export -------------------------------------------------------------

// Mirrors ReminderScheduler.IsDueAsOf: a midnight due time means "sometime that day" (a date-only
// pick) and fires from that date on; any other time fires at that instant.
export function isReminderDue(task, now = new Date()) {
  if (!task || task.IsDone || task.IsClosed || !task.DueDate) return false;
  const due = parseDotNetDate(task.DueDate);
  if (!due) return false;
  const allDay = due.getHours() === 0 && due.getMinutes() === 0 && due.getSeconds() === 0;
  return allDay ? startOfDay(due) <= startOfDay(now) : due <= now;
}

const ICS_RRULE_FREQ = { 1: 'DAILY', 2: 'WEEKLY', 3: 'MONTHLY', 4: 'YEARLY' };

function escapeIcsText(text) {
  return String(text ?? '')
    .replace(/\\/g, '\\\\')
    .replace(/;/g, '\\;')
    .replace(/,/g, '\\,')
    .replace(/\r?\n/g, '\\n');
}

// RFC 5545 folds content lines at 75 octets, continuation lines starting with one space.
function foldIcsLine(line) {
  const bytes = new TextEncoder().encode(line);
  if (bytes.length <= 75) return line;
  const parts = [];
  let current = '';
  let currentBytes = 0;
  for (const ch of line) {
    const size = new TextEncoder().encode(ch).length;
    const limit = parts.length === 0 ? 75 : 74;
    if (currentBytes + size > limit) {
      parts.push(current);
      current = '';
      currentBytes = 0;
    }
    current += ch;
    currentBytes += size;
  }
  parts.push(current);
  return parts.join('\r\n ');
}

function icsLocal(date) {
  const p = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}T${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}
function icsDate(date) {
  const p = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}`;
}

// One task as an .ics event - same shape as desktop's ExportService.ExportToICalendar (UID, all-day
// vs 30-minute timed event, floating local time), plus the task's repeat as an RRULE and, for a
// timed task, an alarm at the due time: the phone's own calendar then reminds you even when Tasky
// isn't running. Returns null for a task with no due date.
export function taskToICalendar(task, now = new Date()) {
  if (!task?.DueDate) return null;
  const due = parseDotNetDate(task.DueDate);
  if (!due) return null;
  const allDay = due.getHours() === 0 && due.getMinutes() === 0;
  const stamp = now.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '');
  const lines = [
    'BEGIN:VCALENDAR',
    'VERSION:2.0',
    'PRODID:-//Tasky//Tasky Task Manager//EN',
    'CALSCALE:GREGORIAN',
    'BEGIN:VEVENT',
    `UID:${task.Id}@tasky.app`,
    `DTSTAMP:${stamp}`,
  ];
  if (allDay) {
    const end = new Date(due);
    end.setDate(end.getDate() + 1);
    lines.push(`DTSTART;VALUE=DATE:${icsDate(due)}`, `DTEND;VALUE=DATE:${icsDate(end)}`);
  } else {
    lines.push(`DTSTART:${icsLocal(due)}`, `DTEND:${icsLocal(new Date(due.getTime() + 30 * 60 * 1000))}`);
  }
  lines.push(`SUMMARY:${escapeIcsText(task.IsDone ? `[Done] ${task.Text}` : task.Text)}`);
  const notes = (task.Body ?? []).filter((b) => b.Type === NoteBlockType.Text && b.Text?.trim()).map((b) => b.Text.trim()).join('\n\n');
  const description = [task.Tags?.length ? `Tags: ${task.Tags.join(', ')}` : '', notes].filter(Boolean).join('\n\n');
  if (description) lines.push(`DESCRIPTION:${escapeIcsText(description)}`);
  const freq = ICS_RRULE_FREQ[task.Recurrence];
  if (freq) lines.push(`RRULE:FREQ=${freq}${task.RecurrenceInterval > 1 ? `;INTERVAL=${task.RecurrenceInterval}` : ''}`);
  if (!allDay) {
    lines.push('BEGIN:VALARM', 'ACTION:DISPLAY', `DESCRIPTION:${escapeIcsText(task.Text)}`, 'TRIGGER:PT0M', 'END:VALARM');
  }
  lines.push('END:VEVENT', 'END:VCALENDAR');
  return `${lines.map(foldIcsLine).join('\r\n')}\r\n`;
}
