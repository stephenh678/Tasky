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
const QUICK_ADD_TAG_RE = /(^|\s)#([\w-]+)/g;
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

export function xamlToHtml(xaml) {
  if (!xaml || typeof xaml !== 'string' || !xaml.trim()) return '';
  if (xaml.startsWith('{\\rtf')) {
    return xaml.replace(/\\[a-z0-9-]+ ?/gi, '').replace(/[{}]/g, '').trim();
  }

  // Remove XML declaration and FlowDocument namespaces
  let clean = xaml.replace(/<\?[^>]*\?>/g, '').replace(/xmlns="[^"]*"/g, '');

  // Strip complex embedded UI containers that are handled as separate attachments
  clean = clean.replace(/<BlockUIContainer>[\s\S]*?<\/BlockUIContainer>/gi, '');
  clean = clean.replace(/<InlineUIContainer>[\s\S]*?<\/InlineUIContainer>/gi, '');

  // Map FlowDocument structural tags to HTML
  clean = clean
    .replace(/<FlowDocument[^>]*>/gi, '')
    .replace(/<\/FlowDocument>/gi, '')
    .replace(/<Paragraph[^>]*>/gi, '<p>')
    .replace(/<\/Paragraph>/gi, '</p>')
    .replace(/<Bold[^>]*>/gi, '<strong>')
    .replace(/<\/Bold>/gi, '</strong>')
    .replace(/<Italic[^>]*>/gi, '<em>')
    .replace(/<\/Italic>/gi, '</em>')
    .replace(/<Underline[^>]*>/gi, '<u>')
    .replace(/<\/Underline>/gi, '</u>')
    .replace(/<LineBreak\s*\/?>/gi, '<br>')
    .replace(/<Hyperlink[^>]*NavigateUri="([^"]*)"[^>]*>([\s\S]*?)<\/Hyperlink>/gi, '<a href="$1" target="_blank" rel="noopener">$2</a>')
    // The (?=[\s>]) lookaheads matter: without them `<List[^>]*>` also matches `<ListItem>` (the
    // "Item" is just more [^>]*), so every list item was rewritten to a second `<ul>` before the
    // ListItem rule below ever saw it - a one-item list came out as the malformed
    // `<ul><ul><p>x</p></li></ul>`, which browsers then re-nested into a stray empty bullet.
    .replace(/<List(?=[\s>])[^>]*MarkerStyle="Decimal"[^>]*>/gi, '<ol>')
    .replace(/<List(?=[\s>])[^>]*>/gi, '<ul>')
    .replace(/<\/List>/gi, '</ul>')
    .replace(/<ListItem(?=[\s>])[^>]*>/gi, '<li>')
    .replace(/<\/ListItem>/gi, '</li>')
    .replace(/<Table[^>]*>/gi, '<table class="note-table">')
    .replace(/<\/Table>/gi, '</table>')
    .replace(/<TableRowGroup[^>]*>/gi, '<tbody>')
    .replace(/<\/TableRowGroup>/gi, '</tbody>')
    .replace(/<TableRow[^>]*>/gi, '<tr>')
    .replace(/<\/TableRow>/gi, '</tr>')
    .replace(/<TableCell[^>]*>/gi, '<td>')
    .replace(/<\/TableCell>/gi, '</td>');

  // Convert Run elements: <Run Text="..." FontWeight="..." /> and <Run ...>content</Run>
  clean = clean.replace(/<Run\s+([^>]*?)\s*\/>/gi, (_, attrs) => {
    const textMatch = /Text="([^"]*)"/i.exec(attrs);
    let res = textMatch ? textMatch[1] : '';
    if (/FontWeight="Bold"/i.test(attrs)) res = `<strong>${res}</strong>`;
    if (/FontStyle="Italic"/i.test(attrs)) res = `<em>${res}</em>`;
    if (/TextDecorations="Underline"/i.test(attrs)) res = `<u>${res}</u>`;
    return res;
  });
  clean = clean.replace(/<Run\s+([^>]*?)>([\s\S]*?)<\/Run>/gi, (_, attrs, content) => {
    let res = content;
    if (/FontWeight="Bold"/i.test(attrs)) res = `<strong>${res}</strong>`;
    if (/FontStyle="Italic"/i.test(attrs)) res = `<em>${res}</em>`;
    if (/TextDecorations="Underline"/i.test(attrs)) res = `<u>${res}</u>`;
    return res;
  });

  // Strip any remaining structural wrapper tags like Section, Span
  clean = clean.replace(/<\/?(Section|Span)[^>]*>/gi, '');

  return clean.trim();
}

export function htmlToXaml(html, fallbackText = '') {
  if (!html || !html.trim()) {
    if (!fallbackText || !fallbackText.trim()) return '';
    return `<FlowDocument xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TextAlignment="Left"><Paragraph>${escapeXml(fallbackText)}</Paragraph></FlowDocument>`;
  }

  let xaml = html
    .replace(/<strong[^>]*>([\s\S]*?)<\/strong>/gi, '<Bold>$1</Bold>')
    .replace(/<b[^>]*>([\s\S]*?)<\/b>/gi, '<Bold>$1</Bold>')
    .replace(/<em[^>]*>([\s\S]*?)<\/em>/gi, '<Italic>$1</Italic>')
    .replace(/<i[^>]*>([\s\S]*?)<\/i>/gi, '<Italic>$1</Italic>')
    .replace(/<u[^>]*>([\s\S]*?)<\/u>/gi, '<Underline>$1</Underline>')
    .replace(/<a\s+[^>]*href="([^"]*)"[^>]*>([\s\S]*?)<\/a>/gi, '<Hyperlink NavigateUri="$1"><Run Text="$2"/></Hyperlink>')
    .replace(/<br\s*\/?>/gi, '<LineBreak/>')
    .replace(/<ol[^>]*>/gi, '<List MarkerStyle="Decimal">')
    .replace(/<\/ol>/gi, '</List>')
    .replace(/<ul[^>]*>/gi, '<List>')
    .replace(/<\/ul>/gi, '</List>')
    .replace(/<li[^>]*>([\s\S]*?)<\/li>/gi, '<ListItem><Paragraph>$1</Paragraph></ListItem>')
    .replace(/<p[^>]*>([\s\S]*?)<\/p>/gi, '<Paragraph>$1</Paragraph>')
    .replace(/<div[^>]*>([\s\S]*?)<\/div>/gi, '<Paragraph>$1</Paragraph>')
    .replace(/<table[^>]*>/gi, '<Table><TableRowGroup>')
    .replace(/<\/table>/gi, '</TableRowGroup></Table>')
    .replace(/<tbody[^>]*>|<\/tbody>/gi, '')
    .replace(/<tr[^>]*>/gi, '<TableRow>')
    .replace(/<\/tr>/gi, '</TableRow>')
    .replace(/<td[^>]*>([\s\S]*?)<\/td>/gi, '<TableCell><Paragraph>$1</Paragraph></TableCell>');

  // Ensure content is wrapped in Paragraph if needed
  if (!xaml.includes('<Paragraph') && !xaml.includes('<List') && !xaml.includes('<Table')) {
    xaml = `<Paragraph>${xaml}</Paragraph>`;
  }

  // Clean empty paragraphs
  xaml = xaml.replace(/<Paragraph>\s*<\/Paragraph>/gi, '<Paragraph><LineBreak/></Paragraph>');

  return `<FlowDocument xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TextAlignment="Left">${xaml}</FlowDocument>`;
}
