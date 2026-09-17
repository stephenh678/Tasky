using System;
using System.Linq;
using TodoApp.Converters;
using TodoApp.Models;
using TodoApp.Services;
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

    [Fact]
    public void TaskItem_SortOrder_ClonesProperly()
    {
        var task = new TaskItem { Text = "Test", SortOrder = 42 };
        var clone = task.Clone();
        Assert.Equal(42, clone.SortOrder);
    }

    [Fact]
    public void TaskSyncMerge_ApplyTaskFields_CopiesSortOrder()
    {
        var source = new TaskItem { Text = "Source", SortOrder = 99 };
        var target = new TaskItem { Text = "Target", SortOrder = 1 };
        TaskSyncMerge.ApplyTaskFields(target, source);
        Assert.Equal(99, target.SortOrder);
    }

    [Fact]
    public void MainViewModel_ReorderTask_MovesTaskAndSetsSortOrder()
    {
        var vm = new MainViewModel();
        vm.AllTasks.Clear();

        var taskA = new TaskItem { Text = "A", SortOrder = 0 };
        var taskB = new TaskItem { Text = "B", SortOrder = 1 };
        var taskC = new TaskItem { Text = "C", SortOrder = 2 };

        vm.AllTasks.Add(taskA);
        vm.AllTasks.Add(taskB);
        vm.AllTasks.Add(taskC);

        // Reorder: Move taskC before taskA
        vm.ReorderTask(taskC, taskA, insertAfter: false);

        Assert.Equal(SortOption.Manual, vm.CurrentSort);
        Assert.Equal(taskC, vm.AllTasks[0]);
        Assert.Equal(taskA, vm.AllTasks[1]);
        Assert.Equal(taskB, vm.AllTasks[2]);

        Assert.Equal(0, taskC.SortOrder);
        Assert.Equal(1, taskA.SortOrder);
        Assert.Equal(2, taskB.SortOrder);

        // Undo
        vm.UndoCommand.Execute(null);

        Assert.Equal(taskA, vm.AllTasks[0]);
        Assert.Equal(taskB, vm.AllTasks[1]);
        Assert.Equal(taskC, vm.AllTasks[2]);
        Assert.Equal(0, taskA.SortOrder);
        Assert.Equal(1, taskB.SortOrder);
        Assert.Equal(2, taskC.SortOrder);
    }

    [Fact]
    public void MainViewModel_ReorderTask_AutoPinsAndUnpinsAcrossBoundary()
    {
        var vm = new MainViewModel();
        vm.AllTasks.Clear();

        var pinned = new TaskItem { Text = "Pinned", IsPinned = true, SortOrder = 0 };
        var unpinned1 = new TaskItem { Text = "Unpinned 1", IsPinned = false, SortOrder = 1 };
        var unpinned2 = new TaskItem { Text = "Unpinned 2", IsPinned = false, SortOrder = 2 };

        vm.AllTasks.Add(pinned);
        vm.AllTasks.Add(unpinned1);
        vm.AllTasks.Add(unpinned2);

        // Drag unpinned1 above pinned -> gets pinned
        vm.ReorderTask(unpinned1, pinned, insertAfter: false);
        Assert.True(unpinned1.IsPinned);

        // Drag now-pinned unpinned1 below unpinned2 -> gets unpinned
        vm.ReorderTask(unpinned1, unpinned2, insertAfter: true);
        Assert.False(unpinned1.IsPinned);
    }
}
