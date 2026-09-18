using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TodoApp.Models;
using TodoApp.Services;
using TodoApp;

namespace TodoApp.ViewModels;

// Undo, and every Bulk* action driven by the task list's multi-selection.
public partial class MainViewModel
{
    // Undo, and every Bulk* command driven by the task list's multi-selection (SelectedTasks)
    // rather than the single SelectedTask.
    private void InitializeBulkCommands()
    {
        UndoCommand = new RelayCommand(_ =>
        {
            if (_undoStack.Count == 0) return;
            var (_, undo) = _undoStack.Last!.Value;
            _undoStack.RemoveLast();
            OnPropertyChanged(nameof(UndoMenuLabel));
            undo();
        }, _ => _undoStack.Count > 0);

        BulkMarkDoneCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.Where(t => !t.IsDone).ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Mark {targets.Count} task(s) complete?",
                "Mark Complete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var t in targets) t.IsDone = true;
        }, _ => SelectedTasks.Count > 0);

        BulkTrashCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.Where(t => !t.IsClosed).ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Move {targets.Count} task(s) to Trash?",
                "Move to Trash", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var t in targets) t.IsClosed = true;
            PushUndo($"Move {targets.Count} task(s) to Trash", () =>
            {
                foreach (var t in targets) t.IsClosed = false;
            });
        }, _ => SelectedTasks.Count > 0);

        BulkRestoreCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.Where(t => t.IsClosed).ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Restore {targets.Count} task(s) from Trash?",
                "Restore from Trash", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var t in targets) t.IsClosed = false;
            PushUndo($"Restore {targets.Count} task(s) from Trash", () =>
            {
                foreach (var t in targets) t.IsClosed = true;
            });
        }, _ => SelectedTasks.Count > 0);

        BulkDeleteCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Delete {targets.Count} task(s) permanently? This also removes their photos and attachments.",
                "Delete Tasks", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            PermanentlyDelete(targets);
        }, _ => SelectedTasks.Count > 0);

        BulkTogglePinCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Toggle pin on {targets.Count} task(s)?",
                "Toggle Pin", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var t in targets) t.IsPinned = !t.IsPinned;
            PushUndo($"Toggle pin on {targets.Count} task(s)", () =>
            {
                foreach (var t in targets) t.IsPinned = !t.IsPinned;
            });
        }, _ => SelectedTasks.Count > 0);

        BulkSetDueDateCommand = new RelayCommand(_ => BulkSetDueDateRequested?.Invoke(), _ => SelectedTasks.Count > 0);
        BulkAddTagCommand = new RelayCommand(_ => BulkAddTagRequested?.Invoke(), _ => SelectedTasks.Count > 0);
    }

    // Called from MainWindow.xaml.cs after BulkDueDatePromptWindow returns (BulkSetDueDateRequested
    // triggers showing that dialog). date is null for "Clear Due Date," not "user cancelled" -
    // cancelling never calls this at all. DueDate is a plain SetField-backed property (unlike
    // Tags/Body below), so Task_PropertyChanged picks up the change and bumps ModifiedAt on its own -
    // no manual touch needed, same as every other single-task due-date edit.
    public void ApplyBulkDueDate(DateTime? date)
    {
        var targets = SelectedTasks.ToList();
        if (targets.Count == 0) return;

        var message = date is null
            ? $"Clear the due date on {targets.Count} task(s)?"
            : $"Set the due date to {date:M/d/yyyy} on {targets.Count} task(s)?";
        var result = ThemedMessageBox.Show(message, "Set Due Date", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // Snapshot each task's own prior due date (not just "clear back to null") since they didn't
        // necessarily share one before the bulk edit - Task_PropertyChanged doesn't special-case
        // DueDate the way it does IsDone, so this needs its own explicit PushUndo.
        var previous = targets.Select(t => (Task: t, DueDate: t.DueDate)).ToList();
        foreach (var t in targets) t.DueDate = date;

        PushUndo($"Set due date on {targets.Count} task(s)", () =>
        {
            foreach (var (task, due) in previous) task.DueDate = due;
        });
    }

    // Called from MainWindow.xaml.cs after BulkAddTagPromptWindow returns a tag (BulkAddTagRequested
    // triggers showing that dialog). Mirrors TaskDetailViewModel.AddTagCommand exactly, including its
    // own manual ModifiedAt bump - Tags is a plain ObservableCollection<string> with no SetField
    // wrapper, so Add() never raises TaskItem.PropertyChanged and the sync merge would otherwise never
    // see the new tag as an edit worth keeping.
    public void ApplyBulkTag(string rawTag)
    {
        var tag = TagUtils.Sanitize(rawTag);
        if (tag.Length == 0) return;
        var targets = SelectedTasks.ToList();
        if (targets.Count == 0) return;

        var result = ThemedMessageBox.Show($"Add the \"{tag}\" tag to {targets.Count} task(s)?",
            "Add Tag", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // Only the tasks that didn't already carry this tag actually change - undo must revert
        // exactly that subset, not every selected task, or it would strip a tag a task already had
        // on its own before this bulk edit ever ran.
        var added = new List<TaskItem>();
        foreach (var t in targets)
        {
            if (t.Tags.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
            t.Tags.Add(tag);
            t.ModifiedAt = DateTime.UtcNow;
            added.Add(t);
        }
        OnTaskChanged();

        if (added.Count == 0) return;
        PushUndo($"Add tag \"{tag}\" to {added.Count} task(s)", () =>
        {
            foreach (var t in added)
            {
                for (var i = t.Tags.Count - 1; i >= 0; i--)
                    if (t.Tags[i].Equals(tag, StringComparison.OrdinalIgnoreCase))
                        t.Tags.RemoveAt(i);
                t.ModifiedAt = DateTime.UtcNow;
            }
            OnTaskChanged();
        });
    }

    private List<TaskItem> TargetTasks()
    {
        if (SelectedTasks.Count > 1) return SelectedTasks.ToList();
        return SelectedTask is not null ? new List<TaskItem> { SelectedTask } : new List<TaskItem>();
    }

    private void PushUndo(string description, Action undo)
    {
        _undoStack.AddLast((description, undo));
        if (_undoStack.Count > MaxUndoDepth)
            _undoStack.RemoveFirst();
        OnPropertyChanged(nameof(UndoMenuLabel));
    }
}
