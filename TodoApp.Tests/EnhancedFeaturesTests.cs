using System;
using System.Linq;
using TodoApp.Converters;
using TodoApp.Models;
using TodoApp.ViewModels;
using Xunit;

namespace TodoApp.Tests;

public class EnhancedFeaturesTests
{
    private static TaskDetailViewModel CreateDetail(TaskItem task, Action<string, Action>? pushUndo = null)
    {
        return new TaskDetailViewModel(
            task,
            () => { },
            () => Enumerable.Empty<string>(),
            () => { },
            pushUndo ?? ((_, _) => { })
        );
    }

    [Fact]
    public void SelectedDueTime_WithNoDueDate_ReturnsAllDay()
    {
        var task = new TaskItem { DueDate = null };
        var vm = CreateDetail(task);

        Assert.Equal("All Day", vm.SelectedDueTime);
        Assert.False(vm.HasDueTime);
        Assert.False(vm.HasDueDate);
    }

    [Fact]
    public void SelectedDueTime_WithDateOnly_ReturnsAllDay()
    {
        var task = new TaskItem { DueDate = new DateTime(2026, 5, 10, 0, 0, 0) };
        var vm = CreateDetail(task);

        Assert.Equal("All Day", vm.SelectedDueTime);
        Assert.False(vm.HasDueTime);
        Assert.True(vm.HasDueDate);
    }

    [Fact]
    public void SelectedDueTime_WithSpecificTime_FormatsCorrectly()
    {
        var task = new TaskItem { DueDate = new DateTime(2026, 5, 10, 14, 30, 0) };
        var vm = CreateDetail(task);

        Assert.Equal("02:30 PM", vm.SelectedDueTime);
        Assert.True(vm.HasDueTime);
        Assert.True(vm.HasDueDate);
    }

    [Fact]
    public void SelectedDueTime_SettingTime_UpdatesTaskDueDate()
    {
        var task = new TaskItem { DueDate = new DateTime(2026, 5, 10, 0, 0, 0) };
        var vm = CreateDetail(task);

        vm.SelectedDueTime = "09:15 AM";

        Assert.NotNull(task.DueDate);
        Assert.Equal(2026, task.DueDate!.Value.Year);
        Assert.Equal(5, task.DueDate!.Value.Month);
        Assert.Equal(10, task.DueDate!.Value.Day);
        Assert.Equal(9, task.DueDate!.Value.Hour);
        Assert.Equal(15, task.DueDate!.Value.Minute);
    }

    [Fact]
    public void SelectedDueTime_SettingAllDay_ResetsTimeToMidnight()
    {
        var task = new TaskItem { DueDate = new DateTime(2026, 5, 10, 14, 30, 0) };
        var vm = CreateDetail(task);

        vm.SelectedDueTime = "All Day";

        Assert.NotNull(task.DueDate);
        Assert.Equal(TimeSpan.Zero, task.DueDate!.Value.TimeOfDay);
        Assert.False(vm.HasDueTime);
    }

    [Fact]
    public void ClearDueDateCommand_ClearsDueDate()
    {
        var task = new TaskItem { DueDate = new DateTime(2026, 5, 10, 14, 30, 0) };
        var vm = CreateDetail(task);

        Assert.True(vm.ClearDueDateCommand.CanExecute(null));
        vm.ClearDueDateCommand.Execute(null);

        Assert.Null(task.DueDate);
        Assert.False(vm.HasDueDate);
        Assert.False(vm.HasDueTime);
    }

    [Fact]
    public void Subtasks_AddSubtask_CreatesChecklistBlockAndItem()
    {
        var task = new TaskItem();
        var vm = CreateDetail(task);

        vm.NewSubtaskText = "First subtask";
        vm.AddSubtaskCommand.Execute(null);

        Assert.Single(vm.Subtasks);
        Assert.Equal("First subtask", vm.Subtasks[0].Text);
        Assert.False(vm.Subtasks[0].IsChecked);
        Assert.True(vm.HasSubtasks);
        Assert.Equal(1, vm.SubtasksTotal);
        Assert.Equal(0, vm.SubtasksCompleted);
        Assert.Equal(0.0, vm.SubtaskProgressPercent);
        Assert.Equal("0 of 1 completed", vm.SubtaskProgressText);
    }

    [Fact]
    public void Subtasks_CheckingItem_UpdatesProgress()
    {
        var task = new TaskItem();
        var vm = CreateDetail(task);

        vm.AddSubtask("Subtask 1");
        vm.AddSubtask("Subtask 2");

        Assert.Equal(2, vm.SubtasksTotal);
        Assert.Equal(0, vm.SubtasksCompleted);

        vm.Subtasks[0].IsChecked = true;

        Assert.Equal(1, vm.SubtasksCompleted);
        Assert.Equal(50.0, vm.SubtaskProgressPercent);
        Assert.Equal("1 of 2 completed", vm.SubtaskProgressText);
    }

    [Fact]
    public void Subtasks_RemoveSubtask_RemovesItemAndSupportsUndo()
    {
        var task = new TaskItem();
        Action? undoAction = null;
        var vm = CreateDetail(task, (_, undo) => undoAction = undo);

        vm.AddSubtask("Item to delete");
        var item = vm.Subtasks[0];

        vm.RemoveSubtaskCommand.Execute(item);
        Assert.Empty(vm.Subtasks);

        undoAction?.Invoke();
        Assert.Single(vm.Subtasks);
        Assert.Equal("Item to delete", vm.Subtasks[0].Text);
    }

    [Fact]
    public void TaskMediaHelper_GetChecklistProgress_CountsCorrectly()
    {
        var task = new TaskItem();
        var block = new NoteBlock { Type = NoteBlockType.Checklist };
        block.ChecklistItems.Add(new ChecklistItem { Text = "A", IsChecked = true });
        block.ChecklistItems.Add(new ChecklistItem { Text = "B", IsChecked = false });
        block.ChecklistItems.Add(new ChecklistItem { Text = "C", IsChecked = true });
        task.Body.Add(block);

        var (completed, total) = TaskMediaHelper.GetChecklistProgress(task);

        Assert.Equal(2, completed);
        Assert.Equal(3, total);
    }

    [Fact]
    public void ChecklistProgressConverters_FormatProperly()
    {
        var task = new TaskItem();
        var block = new NoteBlock { Type = NoteBlockType.Checklist };
        block.ChecklistItems.Add(new ChecklistItem { Text = "A", IsChecked = true });
        block.ChecklistItems.Add(new ChecklistItem { Text = "B", IsChecked = false });
        task.Body.Add(block);

        var textConv = new ChecklistProgressTextConverter();
        var toolTipConv = new ChecklistProgressToolTipConverter();

        var text = textConv.Convert(task, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);
        var tip = toolTipConv.Convert(task, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("1/2", text);
        Assert.Equal("1 of 2 subtasks completed", tip);
    }

    [Fact]
    public void SidebarFilter_TomorrowAndSomeday_IconsExist()
    {
        var tomorrowItem = new SidebarFilterItem(SidebarFilterKind.Tomorrow, "Tomorrow");
        var somedayItem = new SidebarFilterItem(SidebarFilterKind.Someday, "Someday");

        Assert.False(string.IsNullOrWhiteSpace(tomorrowItem.Icon));
        Assert.False(string.IsNullOrWhiteSpace(somedayItem.Icon));
        Assert.Equal("Tomorrow", tomorrowItem.Label);
        Assert.Equal("Someday", somedayItem.Label);
    }
}
