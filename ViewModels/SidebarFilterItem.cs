using System;

namespace TodoApp.ViewModels;

public enum SidebarFilterKind
{
    Today,
    All,
    Done,
    Trash,
    Recurring,
    Tag,
    View
}

public class SidebarFilterItem
{
    public SidebarFilterKind Kind { get; }
    public string Label { get; }
    public string? TagName { get; }
    public string? ViewId { get; }

    public string Icon => Kind switch
    {
        SidebarFilterKind.Today => "\U0001F4C5",
        SidebarFilterKind.All => "\U0001F4CB",
        SidebarFilterKind.Done => "✅",
        SidebarFilterKind.Trash => "\U0001F5D1",
        SidebarFilterKind.Recurring => "\U0001F501",
        SidebarFilterKind.View => "⭐",
        _ => "#"
    };

    public SidebarFilterItem(SidebarFilterKind kind, string label, string? tagName = null, string? viewId = null)
    {
        Kind = kind;
        Label = label;
        TagName = tagName;
        ViewId = viewId;
    }

    public SidebarFilterItem(string tag) : this(SidebarFilterKind.Tag, tag, tag)
    {
    }

    // Mirrors the string-tag convenience constructor above, for a saved View's sidebar entry.
    public static SidebarFilterItem ForView(Models.SavedView view)
        => new(SidebarFilterKind.View, view.Label, viewId: view.Id);

    public override bool Equals(object? obj)
        => obj is SidebarFilterItem other
           && Kind == other.Kind
           && string.Equals(TagName, other.TagName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ViewId, other.ViewId, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Kind, TagName?.ToLowerInvariant(), ViewId);
}
