using System;

namespace TodoApp.ViewModels;

public enum SidebarFilterKind
{
    All,
    Done,
    Trash,
    Recurring,
    Tag
}

public class SidebarFilterItem
{
    public SidebarFilterKind Kind { get; }
    public string Label { get; }
    public string? TagName { get; }

    public string Icon => Kind switch
    {
        SidebarFilterKind.All => "\U0001F4CB",
        SidebarFilterKind.Done => "✅",
        SidebarFilterKind.Trash => "\U0001F5D1",
        SidebarFilterKind.Recurring => "\U0001F501",
        _ => "#"
    };

    public SidebarFilterItem(SidebarFilterKind kind, string label, string? tagName = null)
    {
        Kind = kind;
        Label = label;
        TagName = tagName;
    }

    public SidebarFilterItem(string tag) : this(SidebarFilterKind.Tag, tag, tag)
    {
    }

    public override bool Equals(object? obj)
        => obj is SidebarFilterItem other
           && Kind == other.Kind
           && string.Equals(TagName, other.TagName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Kind, TagName?.ToLowerInvariant());
}
