using System;
using System.Text.Json;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

// Regression coverage for the last-write-wins-across-time-zones bug: TaskItem.ModifiedAt/CreatedAt
// and TaskSyncRecord.Timestamp are decorated with [JsonConverter(typeof(UtcDateTimeConverter))]
// specifically so TaskSyncMerge.ComputeMergePlan's plain DateTime comparisons stay correct
// regardless of which time zone the writing device was in. DueDate is deliberately NOT decorated
// (it must stay naive/local), so a couple of these tests confirm that field is left alone too.
public class UtcDateTimeConverterTests
{
    private static string SerializeModifiedAt(DateTime value)
    {
        var task = new TaskItem();
        task.ModifiedAt = value;
        return JsonSerializer.Serialize(task);
    }

    [Fact]
    public void Write_UtcKind_EmitsTrailingZ()
    {
        var json = SerializeModifiedAt(DateTime.UtcNow);

        Assert.Contains("\"ModifiedAt\":\"", json);
        var value = ExtractModifiedAt(json);
        Assert.EndsWith("Z", value);
    }

    [Fact]
    public void Write_LocalKind_NormalizedToUtcBeforeWriting()
    {
        // A Kind=Local value must be converted to its UTC-equivalent instant before being
        // written, not just have its offset dropped - otherwise the wall-clock number itself
        // would silently shift meaning once re-read as the (different) UTC instant.
        var local = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Local);
        var json = SerializeModifiedAt(local);
        var written = ExtractModifiedAt(json);

        var reparsed = DateTime.Parse(written, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, reparsed.Kind);
        Assert.Equal(local.ToUniversalTime(), reparsed);
    }

    [Fact]
    public void RoundTrip_UtcValue_PreservesExactInstant()
    {
        var original = DateTime.UtcNow;
        var json = SerializeModifiedAt(original);
        var task = JsonSerializer.Deserialize<TaskItem>(json)!;

        // Sub-tick rounding aside, the read-back value must be the same instant.
        Assert.True(Math.Abs((task.ModifiedAt - original).TotalMilliseconds) < 1);
        Assert.Equal(DateTimeKind.Utc, task.ModifiedAt.Kind);
    }

    [Fact]
    public void Read_LegacyOffsetSuffixedValue_ConvertsToCorrectUtcInstant()
    {
        // Shape historically written by desktop-to-desktop sync (System.Text.Json's default
        // DateTime handling includes the writer's UTC offset for Kind=Local) - must still resolve
        // to the correct instant, not be misread as if it had no offset at all.
        var json = """{"ModifiedAt":"2026-08-25T09:00:00.0000000-08:00"}""";
        var task = JsonSerializer.Deserialize<TaskItem>(json)!;

        Assert.Equal(DateTimeKind.Utc, task.ModifiedAt.Kind);
        Assert.Equal(new DateTime(2026, 8, 25, 17, 0, 0, DateTimeKind.Utc), task.ModifiedAt);
    }

    [Fact]
    public void Read_LegacyNoOffsetValue_TreatedAsThisMachinesLocalTime()
    {
        // Shape Tasky Web wrote before its own nowDotNet() fix (bare local components, no
        // offset/Z at all) - .NET parses this as DateTimeKind.Unspecified. Backward-compat
        // interpretation: treat it as this machine's local time (matching what plain DateTime
        // comparison already implicitly assumed before this converter existed), then normalize.
        var naive = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Unspecified);
        var json = $$"""{"ModifiedAt":"{{naive:yyyy-MM-ddTHH:mm:ss.fffffff}}"}""";
        var task = JsonSerializer.Deserialize<TaskItem>(json)!;

        Assert.Equal(DateTimeKind.Utc, task.ModifiedAt.Kind);
        Assert.Equal(DateTime.SpecifyKind(naive, DateTimeKind.Local).ToUniversalTime(), task.ModifiedAt);
    }

    [Fact]
    public void DueDate_IsNotAffectedByTheConverter_StaysNaiveOnRoundTrip()
    {
        // DueDate has no [JsonConverter] attribute - it must serialize/deserialize with the
        // framework's plain default DateTime handling, unaffected by this fix.
        var task = new TaskItem { DueDate = new DateTime(2026, 8, 25, 17, 0, 0, DateTimeKind.Unspecified) };
        var json = JsonSerializer.Serialize(task);

        Assert.DoesNotContain("\"DueDate\":\"2026-08-25T17:00:00.0000000Z\"", json);

        var reloaded = JsonSerializer.Deserialize<TaskItem>(json)!;
        Assert.Equal(new DateTime(2026, 8, 25, 17, 0, 0), reloaded.DueDate);
    }

    [Fact]
    public void CrossTimeZoneMerge_NewerEditWinsRegardlessOfWritingDevicesOffset()
    {
        // The actual bug this fix closes: a task edited on a UTC-8 desktop, then edited again 30
        // minutes later (by real elapsed time) on a machine using Tasky Web's old naive-local
        // format - interpreted as if that string were already in the FIRST device's local time
        // (UTC-8), a naive comparison would have picked the older edit as "newer" and silently
        // discarded the real latest edit. Round-tripping both through the converter fixes that.
        var id = Guid.NewGuid();
        var localTask = new TaskItem { Id = id, Text = "older edit" };
        var localJson = $$"""{"Id":"{{id}}","Text":"older edit","ModifiedAt":"2026-08-25T09:00:00.0000000-08:00"}""";
        localTask = JsonSerializer.Deserialize<TaskItem>(localJson)!;

        // 30 real minutes after the -08:00 edit above (17:00 UTC), expressed as a bare UTC instant.
        var remoteJson = $$"""{"Id":"{{id}}","Text":"newer edit","ModifiedAt":"2026-08-25T17:30:00.0000000Z"}""";
        var remoteTask = JsonSerializer.Deserialize<TaskItem>(remoteJson)!;

        var plan = TaskSyncMerge.ComputeMergePlan(
            new[] { localTask }, new[] { remoteTask },
            Array.Empty<TaskSyncRecord>(), Array.Empty<TaskSyncRecord>());

        var update = Assert.Single(plan.TasksToUpdate);
        Assert.Equal("newer edit", update.Remote.Text);
    }

    private static string ExtractModifiedAt(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("ModifiedAt").GetString()!;
    }
}
