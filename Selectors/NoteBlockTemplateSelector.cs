using System.Windows;
using System.Windows.Controls;
using TodoApp.Models;

namespace TodoApp.Selectors;

public class NoteBlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? PhotoTemplate { get; set; }
    public DataTemplate? LinkTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }
    public DataTemplate? ChecklistTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not NoteBlock block) return base.SelectTemplate(item, container);
        return block.Type switch
        {
            NoteBlockType.Text => TextTemplate,
            NoteBlockType.Photo => PhotoTemplate,
            NoteBlockType.Link => LinkTemplate,
            NoteBlockType.File => FileTemplate,
            NoteBlockType.Checklist => ChecklistTemplate,
            _ => base.SelectTemplate(item, container)
        };
    }
}
