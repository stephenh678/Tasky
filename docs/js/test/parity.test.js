// ROADMAP.md #89 (formerly review_tasks.md's one still-open Critical/High item): docs/js/sync.js
// and model.js are hand-ported line-by-line from Services/TaskSyncMerge.cs and
// Services/QuickEntryParser.cs - and that parity is load-bearing, since both platforms merge the
// same .tasky file. The C# side has full xunit coverage (SyncMergeTests.cs, QuickEntryParserTests.cs);
// this file is the JS-side counterpart, reusing the same test vectors so a silent drift between
// the two ports (e.g. a future edit to one side's tombstone logic but not the other's) fails a
// test instead of silently corrupting a merge in production.
//
// Zero-dependency by design (node:test/node:assert, built into Node - no package.json, no
// npm install, no bundler) to match Tasky Web's own build-step-free design (see ROADMAP.md #38's
// "deferred" reasoning). Run with: node --test docs/js/test/
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import {
  parseDotNetDate,
  formatDotNetDate,
  nowDotNet,
  newTaskItem,
  newAppState,
  newTaskSyncRecord,
  parseQuickAdd,
  nextDueDate,
  recurrenceAnchor,
  spawnNextOccurrence,
  RecurrenceRule,
  NoteBlockType,
  newNoteBlock,
  extractInlineImageFileNames,
  extractInlineFileNames,
  collectTaskFileNames,
  normalizeTask,
  taskHasLink,
  taskHasChecklist,
} from '../model.js';
import { deduplicateTombstones, mergeRemoteState, mergeSavedViews, reconcileLocalSnapshot } from '../sync.js';

// --- parseDotNetDate / formatDotNetDate round-trips (mirrors the .NET JSON date shape) ---------

describe('parseDotNetDate / formatDotNetDate', () => {
  test('round-trips a local-time value with no timezone info', () => {
    const original = new Date(2026, 2, 4, 15, 30, 0); // local, no offset - like DueDate
    const formatted = formatDotNetDate(original);
    const parsed = parseDotNetDate(formatted);
    assert.equal(parsed.getTime(), original.getTime());
  });

  test('formats with 7 fractional digits (ticks), matching .NET readable form', () => {
    const d = new Date(2026, 2, 4, 9, 0, 0, 250);
    assert.match(formatDotNetDate(d), /^2026-03-04T09:00:00\.2500000$/);
  });

  test('parses a UTC "Z"-suffixed value (sync timestamps) via the native-parser fallback', () => {
    const parsed = parseDotNetDate('2026-03-04T15:30:00.0000000Z');
    assert.equal(parsed.getTime(), new Date('2026-03-04T15:30:00.000Z').getTime());
  });

  test('null/empty input returns null', () => {
    assert.equal(parseDotNetDate(null), null);
    assert.equal(parseDotNetDate(''), null);
  });

  test('nowDotNet() always parses back to (approximately) itself', () => {
    const before = Date.now();
    const parsed = parseDotNetDate(nowDotNet());
    assert.ok(Math.abs(parsed.getTime() - before) < 1000);
  });
});

// --- deduplicateTombstones (mirrors TombstoneDeduplicationTests in SyncMergeTests.cs) -----------

describe('deduplicateTombstones', () => {
  // Fixed reference "now" (not real current time) so fixture timestamps sit at a known,
  // deterministic distance from the 90-day retention cutoff (ROADMAP.md #140) regardless of when
  // the suite actually runs - mirrors SyncMergeTests.cs's TombstoneDeduplicationTests.Now.
  const Now = new Date(2026, 5, 15); // June 15, 2026

  test('no duplicates: returns all records unchanged', () => {
    const a = newTaskSyncRecord('a');
    const b = newTaskSyncRecord('b');
    assert.equal(deduplicateTombstones([a, b]).length, 2);
  });

  test('duplicate TaskId: keeps only the latest timestamp', () => {
    const older = { TaskId: 'x', Timestamp: formatDotNetDate(new Date(2026, 4, 16)) }; // 30 days before Now
    const newer = { TaskId: 'x', Timestamp: formatDotNetDate(new Date(2026, 5, 14)) }; // 1 day before Now
    const result = deduplicateTombstones([older, newer], Now);
    assert.equal(result.length, 1);
    assert.equal(result[0].Timestamp, newer.Timestamp);
  });

  test('duplicate TaskId: order of input does not matter', () => {
    const older = { TaskId: 'x', Timestamp: formatDotNetDate(new Date(2026, 4, 16)) };
    const newer = { TaskId: 'x', Timestamp: formatDotNetDate(new Date(2026, 5, 14)) };
    const result = deduplicateTombstones([newer, older], Now);
    assert.equal(result.length, 1);
    assert.equal(result[0].Timestamp, newer.Timestamp);
  });

  test('empty list returns empty list', () => {
    assert.deepEqual(deduplicateTombstones([], Now), []);
  });

  // ROADMAP.md #140: tombstones older than the 90-day retention window are dropped entirely, not
  // just deduplicated - must match TaskSyncMerge.cs's DeduplicateTombstones exactly.
  test('tombstone older than retention window is dropped', () => {
    const old = { TaskId: 'x', Timestamp: formatDotNetDate(new Date(2026, 2, 16)) }; // 91 days before Now
    assert.deepEqual(deduplicateTombstones([old], Now), []);
  });

  test('tombstone just inside retention window is kept', () => {
    const recent = { TaskId: 'x', Timestamp: formatDotNetDate(new Date(2026, 2, 18)) }; // 89 days before Now
    assert.equal(deduplicateTombstones([recent], Now).length, 1);
  });

  test('mix of old and recent tombstones: only recent survive', () => {
    const old = { TaskId: 'old', Timestamp: formatDotNetDate(new Date(2025, 11, 28)) }; // well past the 90-day window
    const recent = { TaskId: 'recent', Timestamp: formatDotNetDate(new Date(2026, 5, 10)) }; // 5 days before Now
    const result = deduplicateTombstones([old, recent], Now);
    assert.equal(result.length, 1);
    assert.equal(result[0].TaskId, 'recent');
  });

  test('no `now` provided: defaults to real current time', () => {
    const justNow = newTaskSyncRecord('x'); // nowDotNet() timestamp
    assert.equal(deduplicateTombstones([justNow]).length, 1);
  });
});

// --- mergeRemoteState (mirrors ComputeMergePlanTests in SyncMergeTests.cs) ----------------------
// The JS port mutates localState in place and returns counts rather than TaskSyncMerge's separate
// TasksToAdd/TasksToUpdate/TasksToRemove plan object, so assertions here check localState's
// resulting Tasks/DeletedTasks and the returned counts instead of a plan's contents directly - the
// underlying per-task decisions being asserted are identical to the C# side.

function taskWithId(id, modifiedAt, text = 'task') {
  const t = newTaskItem({ text });
  t.Id = id;
  t.ModifiedAt = modifiedAt;
  return t;
}

describe('mergeRemoteState', () => {
  test('remote-only task is added', () => {
    const local = newAppState();
    const remote = { Tasks: [newTaskItem({ text: 'remote' })], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.added, 1);
    assert.equal(local.Tasks.length, 1);
    assert.equal(local.Tasks[0].Text, 'remote');
  });

  test('remote-only task, locally deleted before remote edit, is not resurrected', () => {
    const deletedAt = new Date(2026, 0, 1);
    const remoteTask = taskWithId('id-1', formatDotNetDate(deletedAt)); // remote hasn't learned about the deletion yet
    const local = { Tasks: [], DeletedTasks: [{ TaskId: 'id-1', Timestamp: formatDotNetDate(new Date(2026, 0, 2)) }] };
    const remote = { Tasks: [remoteTask], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.added, 0);
    assert.equal(local.Tasks.length, 0);
  });

  test('remote-only task, edited after local deletion, is added back', () => {
    const deletedAt = new Date(2026, 0, 1);
    const remoteTask = taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 2))); // edited on remote after this device deleted it
    const local = { Tasks: [], DeletedTasks: [{ TaskId: 'id-1', Timestamp: formatDotNetDate(deletedAt) }] };
    const remote = { Tasks: [remoteTask], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.added, 1);
  });

  test('local-only task is left alone to upload normally', () => {
    const local = { Tasks: [newTaskItem({ text: 'local' })], DeletedTasks: [] };
    const remote = newAppState();

    const result = mergeRemoteState(local, remote);

    assert.equal(result.added, 0);
    assert.equal(result.removed, 0);
    assert.equal(result.updated, 0);
    assert.equal(local.Tasks.length, 1);
  });

  test('local-only task, deleted remotely and untouched since, is removed', () => {
    const deletedAt = new Date(2026, 0, 1);
    const localTask = taskWithId('id-1', formatDotNetDate(new Date(2025, 11, 31))); // not touched since the remote deletion
    const local = { Tasks: [localTask], DeletedTasks: [] };
    const remote = { Tasks: [], DeletedTasks: [{ TaskId: 'id-1', Timestamp: formatDotNetDate(deletedAt) }] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.removed, 1);
    assert.equal(local.Tasks.length, 0);
  });

  test('local-only task, edited after remote deletion, survives rather than being resurrected as deleted', () => {
    const deletedAt = new Date(2026, 0, 1);
    const localTask = taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 2))); // edited locally after the remote deletion
    const local = { Tasks: [localTask], DeletedTasks: [] };
    const remote = { Tasks: [], DeletedTasks: [{ TaskId: 'id-1', Timestamp: formatDotNetDate(deletedAt) }] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.removed, 0);
    assert.equal(local.Tasks.length, 1);
  });

  test('task on both sides, newer remote edit, overwrites local fields', () => {
    const local = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 1)), 'old')], DeletedTasks: [] };
    const remote = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 5, 1)), 'new')], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.updated, 1);
    assert.equal(local.Tasks[0].Text, 'new');
  });

  test('task on both sides, newer local edit, is not overwritten', () => {
    const local = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 5, 1)), 'newer')], DeletedTasks: [] };
    const remote = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 1)), 'older')], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.updated, 0);
    assert.equal(local.Tasks[0].Text, 'newer');
  });

  test('task on both sides, identical timestamp, local wins (ties do not count as "remote is newer")', () => {
    const sameTime = formatDotNetDate(new Date(2026, 2, 1));
    const local = { Tasks: [taskWithId('id-1', sameTime, 'local')], DeletedTasks: [] };
    const remote = { Tasks: [taskWithId('id-1', sameTime, 'remote')], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.updated, 0);
    assert.equal(local.Tasks[0].Text, 'local');
  });

  test('remote tombstones not already known locally are unioned in', () => {
    const local = newAppState();
    const remote = { Tasks: [], DeletedTasks: [newTaskSyncRecord('id-1')] };

    mergeRemoteState(local, remote);

    assert.equal(local.DeletedTasks.length, 1);
  });

  test('remote tombstones already known locally are not duplicated', () => {
    const local = { Tasks: [], DeletedTasks: [{ TaskId: 'id-1', Timestamp: formatDotNetDate(new Date()) }] };
    const remote = { Tasks: [], DeletedTasks: [{ TaskId: 'id-1', Timestamp: formatDotNetDate(new Date(2025, 0, 1)) }] };

    mergeRemoteState(local, remote);

    assert.equal(local.DeletedTasks.length, 1);
  });

  // ROADMAP.md #119: when both sides edited a task since they last agreed, the loser used to just
  // disappear. Now it's kept as a separate "(conflicted copy)" task - see the matching C# tests in
  // SyncMergeTests.cs (TaskOnBothSides_BothEditedSinceLastSync_*).
  test('both sides edited since last sync: losing edit kept as a conflicted copy', () => {
    const lastSync = new Date(2026, 2, 1);
    const localTask = taskWithId('id-1', formatDotNetDate(new Date(2026, 2, 1, 1)), 'local edit');
    const remoteTask = taskWithId('id-1', formatDotNetDate(new Date(2026, 2, 1, 2)), 'remote edit');
    const local = { Tasks: [localTask], DeletedTasks: [] };
    const remote = { Tasks: [remoteTask], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote, lastSync);

    assert.equal(result.updated, 1); // remote still wins the original task ID
    assert.equal(result.conflicted, 1);
    assert.equal(local.Tasks.length, 2);
    const copy = local.Tasks.find((t) => t.Id !== 'id-1');
    assert.ok(copy, 'conflicted copy should have a distinct Id');
    assert.equal(copy.Text, 'local edit (conflicted copy)');
  });

  test('local unchanged since last sync: no conflicted copy', () => {
    const lastSync = new Date(2026, 2, 1);
    const localTask = taskWithId('id-1', formatDotNetDate(new Date(2026, 1, 28)), 'old'); // before lastSync
    const remoteTask = taskWithId('id-1', formatDotNetDate(new Date(2026, 2, 2)), 'new');
    const local = { Tasks: [localTask], DeletedTasks: [] };
    const remote = { Tasks: [remoteTask], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote, lastSync);

    assert.equal(result.updated, 1);
    assert.equal(result.conflicted, 0);
    assert.equal(local.Tasks.length, 1);
  });

  test('no lastSyncTime (never synced before): no conflicted copy', () => {
    const local = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 1)), 'old')], DeletedTasks: [] };
    const remote = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 5, 1)), 'new')], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote, null);

    assert.equal(result.conflicted, 0);
    assert.equal(local.Tasks.length, 1);
  });

  // updatedIds/removedIds are a Web-only addition on top of the C# return shape - app.js needs to
  // know whether the task open in the editor was one of them (its Body objects get swapped by
  // applyTaskFields, or it vanishes from appState entirely), so the counts alone aren't enough.
  test('reports the IDs behind the updated and removed counts', () => {
    const deletedAt = new Date(2026, 0, 5);
    const local = {
      Tasks: [
        taskWithId('kept', formatDotNetDate(new Date(2026, 0, 1)), 'kept'),
        taskWithId('updated', formatDotNetDate(new Date(2026, 0, 1)), 'old'),
        taskWithId('removed', formatDotNetDate(new Date(2026, 0, 1)), 'gone'),
      ],
      DeletedTasks: [],
    };
    const remote = {
      Tasks: [
        taskWithId('kept', formatDotNetDate(new Date(2026, 0, 1)), 'kept'),
        taskWithId('updated', formatDotNetDate(new Date(2026, 0, 2)), 'new'),
      ],
      DeletedTasks: [{ TaskId: 'removed', Timestamp: formatDotNetDate(deletedAt) }],
    };

    const result = mergeRemoteState(local, remote);

    assert.equal(result.updated, 1);
    assert.deepEqual(result.updatedIds, ['updated']);
    assert.equal(result.removed, 1);
    assert.deepEqual(result.removedIds, ['removed']);
    assert.deepEqual(local.Tasks.map((t) => t.Id), ['kept', 'updated']);
  });

  test('untouched merge reports empty ID lists', () => {
    const local = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 1)), 'same')], DeletedTasks: [] };
    const remote = { Tasks: [taskWithId('id-1', formatDotNetDate(new Date(2026, 0, 1)), 'same')], DeletedTasks: [] };

    const result = mergeRemoteState(local, remote);

    assert.deepEqual(result.updatedIds, []);
    assert.deepEqual(result.removedIds, []);
  });
});

// --- mergeSavedViews (mirrors SavedViewSyncMergeTests.cs) ----------------------------------------
// Much simpler than mergeRemoteState above - views have no ModifiedAt/collaborative-edit concept,
// so this is a plain additive union by Id plus tombstones, not a 3-way merge.

describe('mergeSavedViews', () => {
  test('view added on one side merges into the other', () => {
    const local = { SavedViews: [{ Id: 'a', Label: 'Overdue', Query: 'is:overdue' }], DeletedSavedViewIds: [] };
    const remote = { SavedViews: [], DeletedSavedViewIds: [] };

    mergeSavedViews(local, remote);

    assert.equal(local.SavedViews.length, 1);
    assert.equal(local.SavedViews[0].Id, 'a');
  });

  test('two independently-added views both survive merge', () => {
    const local = { SavedViews: [{ Id: 'a', Label: 'Overdue', Query: 'is:overdue' }], DeletedSavedViewIds: [] };
    const remote = { SavedViews: [{ Id: 'b', Label: 'Pinned', Query: 'is:pinned' }], DeletedSavedViewIds: [] };

    mergeSavedViews(local, remote);

    assert.equal(local.SavedViews.length, 2);
    assert.ok(local.SavedViews.some((v) => v.Id === 'a'));
    assert.ok(local.SavedViews.some((v) => v.Id === 'b'));
  });

  test('view deleted locally does not resurrect from a stale remote copy', () => {
    const local = { SavedViews: [], DeletedSavedViewIds: ['a'] };
    const remote = { SavedViews: [{ Id: 'a', Label: 'Overdue', Query: 'is:overdue' }], DeletedSavedViewIds: [] };

    mergeSavedViews(local, remote);

    assert.equal(local.SavedViews.length, 0);
    assert.ok(local.DeletedSavedViewIds.includes('a'));
  });

  test('view deleted remotely removes the local copy too', () => {
    const local = { SavedViews: [{ Id: 'a', Label: 'Overdue', Query: 'is:overdue' }], DeletedSavedViewIds: [] };
    const remote = { SavedViews: [], DeletedSavedViewIds: ['a'] };

    mergeSavedViews(local, remote);

    assert.equal(local.SavedViews.length, 0);
    assert.ok(local.DeletedSavedViewIds.includes('a'));
  });

  test('same Id on both sides: local wins the collision', () => {
    const local = { SavedViews: [{ Id: 'a', Label: 'Local Label', Query: 'tag:local' }], DeletedSavedViewIds: [] };
    const remote = { SavedViews: [{ Id: 'a', Label: 'Remote Label', Query: 'tag:remote' }], DeletedSavedViewIds: [] };

    mergeSavedViews(local, remote);

    assert.equal(local.SavedViews.length, 1);
    assert.equal(local.SavedViews[0].Label, 'Local Label');
  });

  test('deleted-id sets union from both sides', () => {
    const local = { SavedViews: [], DeletedSavedViewIds: ['a'] };
    const remote = { SavedViews: [], DeletedSavedViewIds: ['b'] };

    mergeSavedViews(local, remote);

    assert.ok(local.DeletedSavedViewIds.includes('a'));
    assert.ok(local.DeletedSavedViewIds.includes('b'));
  });
});

// --- parseQuickAdd (mirrors QuickEntryParserTests.cs) --------------------------------------------

describe('parseQuickAdd', () => {
  const Reference = new Date(2026, 2, 4); // Wednesday, March 4 2026 - matches QuickEntryParserTests.cs's Reference

  function expectDueDate(dueDate, y, mo, d, h = 0, mi = 0) {
    assert.ok(dueDate, 'expected a DueDate to be set');
    assert.equal(parseDotNetDate(dueDate).getTime(), new Date(y, mo - 1, d, h, mi, 0).getTime());
  }

  test('plain title with no tokens is returned unchanged', () => {
    const result = parseQuickAdd('Buy milk', Reference);
    assert.equal(result.text, 'Buy milk');
    assert.equal(result.dueDate, null);
    assert.deepEqual(result.tags, []);
  });

  test('single tag is extracted and stripped from text', () => {
    const result = parseQuickAdd('Submit report #finance', Reference);
    assert.equal(result.text, 'Submit report');
    assert.deepEqual(result.tags, ['finance']);
  });

  test('multiple tags are all extracted', () => {
    const result = parseQuickAdd('Plan trip #travel #personal', Reference);
    assert.equal(result.text, 'Plan trip');
    assert.deepEqual(result.tags, ['travel', 'personal']);
  });

  test('duplicate tags are deduplicated case-insensitively', () => {
    const result = parseQuickAdd('Task #Work #work #WORK', Reference);
    assert.deepEqual(result.tags, ['Work']);
  });

  test('tag inside a word is not extracted', () => {
    const result = parseQuickAdd('Research C#programming foo#bar', Reference);
    assert.equal(result.text, 'Research C#programming foo#bar');
    assert.deepEqual(result.tags, []);
  });

  test('hyphenated tag is extracted', () => {
    const result = parseQuickAdd('Fix bug #high-priority', Reference);
    assert.deepEqual(result.tags, ['high-priority']);
  });

  test('due token: today and tomorrow resolve relative to reference date', () => {
    expectDueDate(parseQuickAdd('Task !due:today', Reference).dueDate, 2026, 3, 4, 9, 0);
    expectDueDate(parseQuickAdd('Task !due:tomorrow', Reference).dueDate, 2026, 3, 5, 9, 0);
  });

  test('due token: same weekday as reference resolves to today', () => {
    // Reference is a Wednesday.
    expectDueDate(parseQuickAdd('Task !due:wed', Reference).dueDate, 2026, 3, 4, 9, 0);
  });

  test('due token: future weekday resolves to nearest upcoming occurrence', () => {
    // Reference is Wednesday March 4; the next Friday is March 6.
    expectDueDate(parseQuickAdd('Task !due:fri', Reference).dueDate, 2026, 3, 6, 9, 0);
  });

  test('due token: past weekday wraps to next week', () => {
    // Reference is Wednesday March 4; the next Monday is March 9, not March 2.
    expectDueDate(parseQuickAdd('Task !due:mon', Reference).dueDate, 2026, 3, 9, 9, 0);
  });

  test('due token: full weekday name is also recognized', () => {
    expectDueDate(parseQuickAdd('Task !due:friday', Reference).dueDate, 2026, 3, 6, 9, 0);
  });

  test('due token: literal date is parsed', () => {
    expectDueDate(parseQuickAdd('Task !due:12/25/2026', Reference).dueDate, 2026, 12, 25, 9, 0);
  });

  test('due token: unrecognized is left in title and does not set DueDate', () => {
    const result = parseQuickAdd('Task !due:whenever', Reference);
    assert.equal(result.text, 'Task !due:whenever');
    assert.equal(result.dueDate, null);
  });

  const timeTokenCases = [
    ['@3pm', 15, 0],
    ['@3:30pm', 15, 30],
    ['@9am', 9, 0],
    ['@12am', 0, 0],
    ['@12pm', 12, 0],
    ['@15:30', 15, 30],
    ['@09:05', 9, 5],
  ];
  for (const [token, hour, minute] of timeTokenCases) {
    test(`time token ${token} is parsed to hour ${hour}, minute ${minute}`, () => {
      const result = parseQuickAdd(`Task !due:today ${token}`, Reference);
      expectDueDate(result.dueDate, 2026, 3, 4, hour, minute);
    });
  }

  test('time token without due token defaults DueDate to today', () => {
    const result = parseQuickAdd('Call the bank @2pm', Reference);
    expectDueDate(result.dueDate, 2026, 3, 4, 14, 0);
  });

  test('due token without time token defaults to 9 AM', () => {
    const result = parseQuickAdd('Task !due:tomorrow', Reference);
    expectDueDate(result.dueDate, 2026, 3, 5, 9, 0);
  });

  test('email address is not mistaken for a time token', () => {
    const result = parseQuickAdd('Email john@example.com about the report', Reference);
    assert.equal(result.text, 'Email john@example.com about the report');
    assert.equal(result.dueDate, null);
  });

  test('invalid time token is left in title and does not set DueDate', () => {
    const result = parseQuickAdd('Task @25:99', Reference);
    assert.equal(result.text, 'Task @25:99');
    assert.equal(result.dueDate, null);
  });

  test('combined tags, due date, and time all parse together from one string', () => {
    const result = parseQuickAdd('Submit budget report !due:tue @3pm #finance', Reference);
    assert.equal(result.text, 'Submit budget report');
    assert.deepEqual(result.tags, ['finance']);
    // Reference is Wed Mar 4; next Tuesday is Mar 10.
    expectDueDate(result.dueDate, 2026, 3, 10, 15, 0);
  });

  test('extra whitespace left behind by removed tokens is collapsed', () => {
    const result = parseQuickAdd('Buy   milk  #groceries   !due:today', Reference);
    assert.equal(result.text, 'Buy milk');
  });

  test('tokens only leaves empty text', () => {
    const result = parseQuickAdd('#tag !due:today', Reference);
    assert.equal(result.text, '');
  });

  test('empty input does not throw and returns an empty result', () => {
    const result = parseQuickAdd('', Reference);
    assert.equal(result.text, '');
    assert.equal(result.dueDate, null);
    assert.deepEqual(result.tags, []);
  });
});

// --- nextDueDate / spawnNextOccurrence (ROADMAP.md #31: recurrence interval) -------------------
// Mirrors MainViewModel.cs's NextDueDate/SpawnNextOccurrence test vectors - interval multiplies
// the step instead of recurrence being fixed at "every 1".

describe('nextDueDate', () => {
  const from = new Date(2026, 0, 1); // Jan 1, 2026 (Thursday)

  test('daily with default interval (1) matches the pre-#31 fixed behavior', () => {
    assert.deepEqual(nextDueDate(from, RecurrenceRule.Daily), new Date(2026, 0, 2));
  });

  test('daily with interval 5 advances 5 days', () => {
    assert.deepEqual(nextDueDate(from, RecurrenceRule.Daily, 5), new Date(2026, 0, 6));
  });

  test('weekly with interval 2 advances 14 days', () => {
    assert.deepEqual(nextDueDate(from, RecurrenceRule.Weekly, 2), new Date(2026, 0, 15));
  });

  test('monthly with interval 3 advances 3 months', () => {
    assert.deepEqual(nextDueDate(from, RecurrenceRule.Monthly, 3), new Date(2026, 3, 1));
  });

  test('yearly with interval 2 advances 2 years', () => {
    assert.deepEqual(nextDueDate(from, RecurrenceRule.Yearly, 2), new Date(2028, 0, 1));
  });

  test('None ignores interval and returns the same date', () => {
    assert.deepEqual(nextDueDate(from, RecurrenceRule.None, 7), from);
  });
});

// Anchored to "today" rather than a fixed calendar date, since recurrenceAnchor's clamping (below)
// makes any *past* fixed date behave differently depending on when the suite happens to run.
function daysFromToday(n) {
  const d = new Date();
  d.setDate(d.getDate() + n);
  return d;
}
function dateOnly(d) {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}

describe('spawnNextOccurrence', () => {
  test('carries the interval forward onto the spawned task', () => {
    const completed = newTaskItem({ text: 'Water plants' });
    completed.DueDate = formatDotNetDate(daysFromToday(30)); // in the future - not clamped
    completed.Recurrence = RecurrenceRule.Weekly;
    completed.RecurrenceInterval = 2;

    const next = spawnNextOccurrence(completed);

    assert.equal(next.RecurrenceInterval, 2);
    assert.equal(next.Recurrence, RecurrenceRule.Weekly);
    assert.deepEqual(dateOnly(parseDotNetDate(next.DueDate)), dateOnly(daysFromToday(44)));
  });

  test('old data with no RecurrenceInterval defaults to 1, not undefined', () => {
    const completed = newTaskItem({ text: 'Legacy task' });
    completed.DueDate = formatDotNetDate(daysFromToday(30)); // in the future - not clamped
    completed.Recurrence = RecurrenceRule.Daily;
    delete completed.RecurrenceInterval; // simulates data synced before #31 added the field

    const next = spawnNextOccurrence(completed);

    assert.equal(next.RecurrenceInterval, 1);
    assert.deepEqual(dateOnly(parseDotNetDate(next.DueDate)), dateOnly(daysFromToday(31)));
  });
});

// ROADMAP.md #31 follow-up: advancing straight from a stale DueDate meant completing a
// long-overdue recurring task spawned a next occurrence that was still overdue. Mirrors
// MainViewModelRecurrenceTests.cs's RecurrenceAnchor coverage exactly.
describe('recurrenceAnchor', () => {
  test('overdue due date clamps to today but keeps the time-of-day', () => {
    const overdue = daysFromToday(-14);
    overdue.setHours(17, 0, 0, 0); // 5pm reminder
    const anchor = recurrenceAnchor(formatDotNetDate(overdue));
    assert.deepEqual(dateOnly(anchor), dateOnly(new Date()));
    assert.equal(anchor.getHours(), 17);
  });

  test('due today is unchanged', () => {
    const dueToday = new Date();
    dueToday.setHours(9, 0, 0, 0);
    const anchor = recurrenceAnchor(formatDotNetDate(dueToday));
    assert.deepEqual(anchor, dueToday);
  });

  test('due in the future is unchanged', () => {
    const dueNextWeek = daysFromToday(7);
    const anchor = recurrenceAnchor(formatDotNetDate(dueNextWeek));
    assert.deepEqual(anchor, dueNextWeek);
  });

  test('no due date defaults to today', () => {
    const anchor = recurrenceAnchor(null);
    assert.deepEqual(dateOnly(anchor), dateOnly(new Date()));
  });
});

describe('spawnNextOccurrence with a stale DueDate (ROADMAP.md #31 fix)', () => {
  test('completing a long-overdue daily task spawns an occurrence due tomorrow, not still overdue', () => {
    const completed = newTaskItem({ text: 'Take out trash' });
    completed.DueDate = formatDotNetDate(daysFromToday(-14));
    completed.Recurrence = RecurrenceRule.Daily;
    completed.RecurrenceInterval = 1;

    const next = spawnNextOccurrence(completed);

    assert.deepEqual(dateOnly(parseDotNetDate(next.DueDate)), dateOnly(daysFromToday(1)));
  });

  test('completing an overdue weekly task with an interval advances from today, not the stale date', () => {
    const completed = newTaskItem({ text: 'Team sync' });
    completed.DueDate = formatDotNetDate(daysFromToday(-30));
    completed.Recurrence = RecurrenceRule.Weekly;
    completed.RecurrenceInterval = 2;

    const next = spawnNextOccurrence(completed);

    assert.deepEqual(dateOnly(parseDotNetDate(next.DueDate)), dateOnly(daysFromToday(14)));
  });
});

// --- Attachment references (mirrors TaskMediaHelper.CollectReferencedFileNames) ----------------
// Feeds app.js's permanentlyRemoveTasks, which deletes a removed task's attachments from Drive
// unless another remaining task still references the same filename - so the "what does this task
// reference" answer has to match desktop's ExtractTaskMediaFilenames exactly. Same test vectors as
// GoogleDriveServiceTests / RichTextBoxMediaPathRewriteTests on the C# side.

describe('inline attachment references in Rtf', () => {
  test('a pasted image is found via its UriSource attribute, by bare filename', () => {
    const rtf = '<Image><BitmapImage UriSource="C:\\Users\\me\\Tasky\\InlineImages\\inline_photo123.png"/></Image>';
    assert.deepEqual(extractInlineImageFileNames(rtf), ['inline_photo123.png']);
  });

  test('an inserted file chip is found via its Grid Tag, by bare filename (either slash style)', () => {
    assert.deepEqual(extractInlineFileNames('<Grid Tag="/home/me/Tasky/Attachments/report.pdf">card</Grid>'), ['report.pdf']);
    assert.deepEqual(extractInlineFileNames('<Grid Tag="C:\\Users\\me\\Documents\\Tasky\\Attachments\\report (1).xlsx" />'), ['report (1).xlsx']);
  });

  test('internal Grid Tag markers are not attachment references', () => {
    const rtf = '<Grid Tag="ImageContainer"><Border Tag="CardBody" /></Grid><Grid Tag="DeleteAttachmentBtn" />';
    assert.deepEqual(extractInlineFileNames(rtf), []);
  });

  test('empty/missing Rtf yields nothing', () => {
    assert.deepEqual(extractInlineImageFileNames(''), []);
    assert.deepEqual(extractInlineFileNames(undefined), []);
  });
});

describe('collectTaskFileNames', () => {
  test('gathers Photo/File block filenames and inline Rtf references, deduplicated', () => {
    const task = newTaskItem({ text: 'with media' });
    task.Body = [
      newNoteBlock(NoteBlockType.Photo, { photoPath: 'a1b2.jpg' }),
      newNoteBlock(NoteBlockType.File, { photoPath: 'C:\\somewhere\\notes.pdf' }),
      newNoteBlock(NoteBlockType.Text, {
        text: 'see attached',
        rtf: '<BitmapImage UriSource="C:\\x\\InlineImages\\inline.png"/><Grid Tag="C:\\x\\Attachments\\a1b2.jpg" />',
      }),
    ];
    assert.deepEqual([...collectTaskFileNames(task)].sort(), ['a1b2.jpg', 'inline.png', 'notes.pdf']);
  });

  test('a task with only text references nothing', () => {
    const task = newTaskItem({ text: 'plain' });
    assert.equal(collectTaskFileNames(task).size, 0);
  });

  test('accumulates into a caller-supplied set across tasks', () => {
    const a = newTaskItem({ text: 'a' });
    a.Body = [newNoteBlock(NoteBlockType.Photo, { photoPath: 'shared.jpg' })];
    const b = newTaskItem({ text: 'b' });
    b.Body = [newNoteBlock(NoteBlockType.Photo, { photoPath: 'shared.jpg' }), newNoteBlock(NoteBlockType.File, { photoPath: 'only-b.zip' })];
    const into = new Set();
    collectTaskFileNames(a, into);
    collectTaskFileNames(b, into);
    assert.deepEqual([...into].sort(), ['only-b.zip', 'shared.jpg']);
  });
});

// --- parseQuickAdd token boundaries ----------------------------------------------------------------
// The "token must start the string or follow whitespace" rule used to be a regex lookbehind; it is
// now a captured (^|\s) prefix (Safari < 16.4 can't parse lookbehind). These pin down the cases
// where the two could plausibly diverge: adjacent tokens sharing one separator, and non-space
// whitespace.

describe('parseQuickAdd token boundaries', () => {
  const Reference = new Date(2026, 2, 4, 10, 0, 0); // Wed 4 Mar 2026

  test('adjacent tokens separated by a single space are all consumed', () => {
    const result = parseQuickAdd('#a #b #c', Reference);
    assert.deepEqual(result.tags, ['a', 'b', 'c']);
    assert.equal(result.text, '');
  });

  test('tab and newline count as separators', () => {
    const tab = String.fromCharCode(9);
    const nl = String.fromCharCode(10);
    const result = parseQuickAdd(`Buy milk${tab}#groceries${nl}!due:today @2pm`, Reference);
    assert.deepEqual(result.tags, ['groceries']);
    assert.equal(result.text, 'Buy milk');
    assert.ok(result.dueDate);
  });

  test('consuming a token keeps the words either side of it apart', () => {
    const result = parseQuickAdd('Call #bank about @2pm the loan', Reference);
    assert.deepEqual(result.tags, ['bank']);
    assert.equal(result.text, 'Call about the loan');
  });

  test('a recognized token glued to the previous word is left in the title', () => {
    const result = parseQuickAdd('Report!due:today done@2pm', Reference);
    assert.equal(result.text, 'Report!due:today done@2pm');
    assert.equal(result.dueDate, null);
  });
});

// --- parseQuickAdd !due: strictness (mirrors DateOnly.TryParse rejecting non-dates) ----------------

describe('parseQuickAdd !due: numeric forms', () => {
  const Reference = new Date(2026, 2, 4, 10, 0, 0);

  test('rejects words and bare numbers the native Date parser used to accept', () => {
    for (const token of ['march', '5', '2026', 'next', '12/25', '25-12-2026']) {
      const result = parseQuickAdd(`Task !due:${token}`, Reference);
      assert.equal(result.dueDate, null, token);
      assert.equal(result.text, `Task !due:${token}`, token);
    }
  });

  test('accepts d.m.yyyy and yyyy/mm/dd', () => {
    assert.ok(parseQuickAdd('Task !due:25.12.2026', Reference).dueDate.startsWith('2026-12-25'));
    assert.ok(parseQuickAdd('Task !due:2026/12/25', Reference).dueDate.startsWith('2026-12-25'));
  });

  test('rejects an impossible calendar day instead of rolling it forward', () => {
    assert.equal(parseQuickAdd('Task !due:2/31/2026', Reference).dueDate, null);
    assert.equal(parseQuickAdd('Task !due:13/1/2026', Reference).dueDate, null);
  });
});

// --- normalizeTask (load-time shape guard) -------------------------------------------------------

describe('normalizeTask', () => {
  test('fills in every field a legacy or hand-edited task can lack', () => {
    const task = normalizeTask({ Id: 'x', Text: 'legacy' });
    assert.deepEqual(task.Tags, []);
    assert.deepEqual(task.Body, []);
    assert.equal(task.RecurrenceInterval, 1);
    assert.equal(task.Priority, 0);
    assert.equal(task.Recurrence, RecurrenceRule.None);
    assert.equal(task.IsDone, false);
    assert.equal(task.DueDate, null);
  });

  test('leaves a complete task untouched', () => {
    const task = newTaskItem({ text: 'ok' });
    task.Tags = ['a'];
    task.RecurrenceInterval = 3;
    const before = JSON.stringify(task);
    normalizeTask(task);
    assert.equal(JSON.stringify(task), before);
  });

  test('gives a checklist block its items array', () => {
    const task = normalizeTask({ Id: 'x', Body: [{ Id: 'b', Type: NoteBlockType.Checklist }] });
    assert.deepEqual(task.Body[0].ChecklistItems, []);
  });
});

describe('deduplicateTombstones with malformed records', () => {
  test('skips records missing TaskId or Timestamp instead of throwing', () => {
    const good = newTaskSyncRecord('t1');
    const result = deduplicateTombstones([null, {}, { TaskId: 't2' }, { Timestamp: good.Timestamp }, good]);
    assert.deepEqual(result.map((t) => t.TaskId), ['t1']);
  });
});

// --- taskHasLink / taskHasChecklist (mirror Models/TaskMediaHelper.cs) ---------------------------

describe('taskHasLink / taskHasChecklist', () => {
  test('a Link block, a block Url, an Rtf hyperlink and a typed URL all count as a link', () => {
    const withLinkBlock = newTaskItem({ text: 'a' });
    withLinkBlock.Body = [newNoteBlock(NoteBlockType.Link, { url: 'https://x.test' })];
    assert.equal(taskHasLink(withLinkBlock), true);

    const withRtf = newTaskItem({ text: 'b' });
    withRtf.Body = [{ ...newNoteBlock(NoteBlockType.Text, {}), Rtf: '<Paragraph><Hyperlink NavigateUri="https://x.test">x</Hyperlink></Paragraph>' }];
    assert.equal(taskHasLink(withRtf), true);

    const withText = newTaskItem({ text: 'c' });
    withText.Body = [newNoteBlock(NoteBlockType.Text, { text: 'see www.example.com for details' })];
    assert.equal(taskHasLink(withText), true);

    const legacy = newTaskItem({ text: 'd' });
    legacy.Links = ['https://legacy.test'];
    assert.equal(taskHasLink(legacy), true);

    const plain = newTaskItem({ text: 'e' });
    plain.Body = [newNoteBlock(NoteBlockType.Text, { text: 'nothing here' })];
    assert.equal(taskHasLink(plain), false);
  });

  test('a Checklist block or an Rtf <CheckBox> counts as a checklist', () => {
    const block = newTaskItem({ text: 'a' });
    block.Body = [newNoteBlock(NoteBlockType.Checklist, {})];
    assert.equal(taskHasChecklist(block), true);

    const rtf = newTaskItem({ text: 'b' });
    rtf.Body = [{ ...newNoteBlock(NoteBlockType.Text, {}), Rtf: '<Paragraph><InlineUIContainer><CheckBox IsChecked="True"/></InlineUIContainer>done</Paragraph>' }];
    assert.equal(taskHasChecklist(rtf), true);

    const plain = newTaskItem({ text: 'c' });
    assert.equal(taskHasChecklist(plain), false);
  });
});

// --- reconcileLocalSnapshot (boot-time recovery of a dirty local copy) ---------------------------

describe('reconcileLocalSnapshot', () => {
  test('an offline edit survives against an unchanged remote and a remote-only task is picked up', () => {
    const lastSync = new Date(2026, 0, 1, 12, 0, 0);
    const shared = newTaskItem({ text: 'shared' });
    shared.ModifiedAt = formatDotNetDate(new Date(2025, 11, 31));

    const remote = newAppState();
    remote.Tasks = [{ ...shared }, newTaskItem({ text: 'added elsewhere' })];

    const snapshot = newAppState();
    snapshot.Tasks = [{ ...shared, Text: 'shared (edited offline)', ModifiedAt: formatDotNetDate(new Date(2026, 0, 2)) }];

    const { state, added, conflicted } = reconcileLocalSnapshot(snapshot, remote, lastSync);

    assert.equal(state, snapshot);
    assert.equal(added, 1);
    assert.equal(conflicted, 0);
    assert.equal(state.Tasks.find((t) => t.Id === shared.Id).Text, 'shared (edited offline)');
    assert.equal(state.Tasks.length, 2);
  });

  test('a saved view created elsewhere is merged into the recovered state', () => {
    const remote = newAppState();
    remote.SavedViews = [{ Id: 'v1', Label: 'Overdue', Query: 'is:overdue' }];
    const snapshot = newAppState();
    reconcileLocalSnapshot(snapshot, remote, null);
    assert.deepEqual(snapshot.SavedViews.map((v) => v.Id), ['v1']);
  });
});
