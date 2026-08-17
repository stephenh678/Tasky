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

const MAX_TASK_TEXT = 500;
const MAX_BLOCK_TEXT = 10000;
const MAX_LINK_LABEL = 500;

export function newGuid() {
  return crypto.randomUUID();
}

// .NET's DateTime.Now has Kind=Local, and System.Text.Json writes Local/Unspecified DateTimes
// with NO trailing 'Z' or UTC offset - just "yyyy-MM-ddTHH:mm:ss[.fraction]". JS's native Date
// parser is spec-required to treat that shape as local time too, so this is a safe round-trip
// AS LONG AS the fractional-seconds part never trips up the parser. .NET can write up to 7
// fractional digits (100ns ticks); some JS engines are strict about the 3-digit millisecond form
// in ISO strings, so rather than trust `new Date(string)` across browsers, parse by hand and
// build the Date from local-time components explicitly.
const DATE_RE = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/;

export function parseDotNetDate(value) {
  if (!value) return null;
  const m = DATE_RE.exec(value);
  if (!m) {
    // Not the plain local-time shape we expect (e.g. has a 'Z'/offset) - fall back to the
    // native parser rather than silently dropping a valid date.
    const d = new Date(value);
    return isNaN(d.getTime()) ? null : d;
  }
  const [, y, mo, d, h, mi, s, frac] = m;
  const ms = frac ? Math.round(parseInt(frac.slice(0, 3).padEnd(3, '0'), 10)) : 0;
  return new Date(Number(y), Number(mo) - 1, Number(d), Number(h), Number(mi), Number(s), ms);
}

// Always emits 7 fractional digits (padding ms out to ticks) - a form .NET's reader accepts
// even though its own writer trims trailing zeros; round-trips fine either direction.
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

export function nowDotNet() {
  return formatDotNetDate(new Date());
}

export function newTaskLink({ label = '', url = '' } = {}) {
  return { Id: newGuid(), Label: label, Url: url };
}

export function newChecklistItem({ text = '', isChecked = false } = {}) {
  return { Id: newGuid(), Text: clamp(text.trim(), MAX_TASK_TEXT), IsChecked: isChecked };
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
    Notes: '',
    Links: [],
    Photos: [],
    Body: [newNoteBlock(NoteBlockType.Text, {})],
    Tags: [],
  };
}

export function newAppState() {
  return { Tasks: [], DeletedTasks: [] };
}

export function newTaskSyncRecord(taskId, timestamp = nowDotNet()) {
  return { TaskId: taskId, Timestamp: timestamp };
}

// Mirrors MainViewModel.cs's NextDueDate switch exactly.
export function nextDueDate(from, rule) {
  const d = new Date(from);
  switch (rule) {
    case RecurrenceRule.Daily:
      d.setDate(d.getDate() + 1);
      return d;
    case RecurrenceRule.Weekly:
      d.setDate(d.getDate() + 7);
      return d;
    case RecurrenceRule.Monthly:
      d.setMonth(d.getMonth() + 1);
      return d;
    case RecurrenceRule.Yearly:
      d.setFullYear(d.getFullYear() + 1);
      return d;
    default:
      return d;
  }
}

// Mirrors MainViewModel.cs's SpawnNextOccurrence: same title/recurrence/tags, due date advanced.
export function spawnNextOccurrence(completed) {
  const next = newTaskItem({ text: completed.Text });
  const base = completed.DueDate ? parseDotNetDate(completed.DueDate) : new Date();
  next.DueDate = formatDotNetDate(nextDueDate(base, completed.Recurrence));
  next.Recurrence = completed.Recurrence;
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

function fileNameFromPath(path) {
  if (!path) return '';
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] ?? '';
}
