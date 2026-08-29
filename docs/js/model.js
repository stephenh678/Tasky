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
  return { Tasks: [], DeletedTasks: [], SavedViews: [], DeletedSavedViewIds: [] };
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
      d.setMonth(d.getMonth() + interval);
      return d;
    case RecurrenceRule.Yearly:
      d.setFullYear(d.getFullYear() + interval);
      return d;
    default:
      return d;
  }
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

function fileNameFromPath(path) {
  if (!path) return '';
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] ?? '';
}

// Mirrors Services/QuickEntryParser.cs exactly (see its comment for the "why a fixed token
// syntax, not full NLP" rationale) - #tag, !due:<value>, @<time> parsed out of quick-add text.
// A token is only ever consumed when it actually matches one of these forms, so an unrecognized
// "!due:whenever" or an email-address-shaped "@" is left untouched in the title instead of
// silently mangled.
const QUICK_ADD_TAG_RE = /(?<!\S)#([\w-]+)/g;
const QUICK_ADD_DUE_RE = /(?<!\S)!due:(\S+)/gi;
const QUICK_ADD_TIME_RE = /(?<!\S)@(\S+)/g;
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
  text = text.replace(QUICK_ADD_TAG_RE, (m, tag) => {
    if (!tags.some((t) => t.toLowerCase() === tag.toLowerCase())) tags.push(tag);
    return '';
  });

  let datePart = null;
  text = text.replace(QUICK_ADD_DUE_RE, (m, token) => {
    const parsed = parseQuickAddDueToken(token, now);
    if (!parsed) return m; // unrecognized - leave it in the title rather than silently eating it
    datePart = parsed;
    return '';
  });

  let timePart = null;
  text = text.replace(QUICK_ADD_TIME_RE, (m, token) => {
    const parsed = parseQuickAddTimeToken(token);
    if (!parsed) return m;
    timePart = parsed;
    return '';
  });

  let dueDate = null;
  if (datePart) {
    const d = new Date(datePart);
    d.setHours(timePart ? timePart.hour : QUICK_ADD_DEFAULT_DUE_HOUR, timePart ? timePart.minute : 0, 0, 0);
    dueDate = formatDotNetDate(d);
  } else if (timePart) {
    const d = new Date(now);
    d.setHours(timePart.hour, timePart.minute, 0, 0);
    dueDate = formatDotNetDate(d);
  }

  text = text.replace(/\s{2,}/g, ' ').trim();
  return { text, dueDate, tags };
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
  // A literal yyyy-mm-dd date - built from parts rather than `new Date(token)` since that form
  // is parsed as UTC midnight by spec, which can land on the wrong local day near a timezone
  // boundary. Any other shape falls back to the native parser (e.g. "8/25/2026").
  const iso = /^(\d{4})-(\d{2})-(\d{2})$/.exec(token);
  if (iso) {
    const [, y, mo, d] = iso;
    return new Date(Number(y), Number(mo) - 1, Number(d));
  }
  const d = new Date(token);
  return isNaN(d.getTime()) ? null : startOfDay(d);
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
