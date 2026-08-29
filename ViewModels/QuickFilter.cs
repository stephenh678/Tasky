namespace TodoApp.ViewModels;

public enum QuickFilter
{
    None,
    Overdue,
    DueToday,
    NoDueDate,
    Recurring,
    HasLink,
    HasAttachment,
    HighPriority
}

public static class QuickFilterExtensions
{
    public static string Label(this QuickFilter filter) => filter switch
    {
        QuickFilter.Overdue => "Overdue",
        QuickFilter.DueToday => "Due Today",
        QuickFilter.NoDueDate => "No Due Date",
        QuickFilter.Recurring => "Recurring",
        QuickFilter.HasLink => "With Link",
        QuickFilter.HasAttachment => "With Attachment",
        QuickFilter.HighPriority => "High Priority",
        _ => filter.ToString()
    };
}
