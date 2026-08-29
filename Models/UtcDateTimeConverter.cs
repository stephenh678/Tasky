using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoApp.Models;

/// <summary>
/// Applied to sync-relevant timestamps only (TaskItem.ModifiedAt/CreatedAt, TaskSyncRecord.
/// Timestamp) - never to user-facing wall-clock fields like DueDate, which must stay naive/local
/// on both platforms. Last-write-wins merge decisions (TaskSyncMerge.ComputeMergePlan) compare
/// these across devices that may be in different time zones, so every value has to represent the
/// same, unambiguous instant regardless of where it was written.
///
/// System.Text.Json's default DateTime handling already round-trips Kind=Local values correctly
/// (it writes the UTC offset and converts back on read), so historical desktop-written values
/// aren't actually broken by this. The real gap was Tasky Web: JS Date has no concept of Kind, so
/// values it wrote were always a bare "yyyy-MM-ddTHH:mm:ss" with no offset at all - .NET parses
/// that as DateTimeKind.Unspecified, which plain DateTime comparison then treated as if it were
/// already in this machine's own local time with no correction. Read normalizes every Kind to a
/// true UTC instant (Unspecified is treated as this machine's local time, matching what was
/// already implicitly assumed); Write always emits UTC so newly-written values carry an explicit,
/// unambiguous 'Z'.
/// </summary>
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime());
}
