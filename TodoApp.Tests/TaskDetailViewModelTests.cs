using System;
using System.Collections.Generic;
using TodoApp.Models;
using TodoApp.ViewModels;

namespace TodoApp.Tests;

// Regression coverage for "selecting a task edits it" (Fable 5 review, 2026-08-25): the
// constructor and PrimaryBlock getter both used to insert normalization blocks into Task.Body as
// a side effect, and Body_CollectionChanged unconditionally bumped ModifiedAt for any Body change
// - so merely viewing certain tasks marked them modified and made them silently win future sync
// merges. These assert construction/PrimaryBlock reads never touch ModifiedAt or trigger a save.
public class TaskDetailViewModelTests
{
    private static readonly DateTime Baseline = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TaskDetailViewModel Create(TaskItem task, out bool onChangedCalled, IEnumerable<string>? allTags = null)
    {
        var called = false;
        var vm = new TaskDetailViewModel(task, () => called = true, () => allTags ?? Array.Empty<string>(), () => { }, (_, _) => { });
        onChangedCalled = called;
        return vm;
    }

    [Fact]
    public void Construction_WithEmptyBody_NormalizesWithoutBumpingModifiedAtOrSaving()
    {
        var task = new TaskItem { ModifiedAt = Baseline };

        Create(task, out var onChangedCalled);

        Assert.Single(task.Body);
        Assert.Equal(NoteBlockType.Text, task.Body[0].Type);
        Assert.Equal(Baseline, task.ModifiedAt);
        Assert.False(onChangedCalled);
    }

    [Fact]
    public void Construction_WithNonTextFirstBlock_InsertsTextBlockWithoutBumpingModifiedAt()
    {
        var task = new TaskItem { ModifiedAt = Baseline };
        task.Body.Add(new NoteBlock { Type = NoteBlockType.Photo, PhotoPath = "photo.jpg" });

        Create(task, out var onChangedCalled);

        Assert.Equal(2, task.Body.Count);
        Assert.Equal(NoteBlockType.Text, task.Body[0].Type);
        Assert.Equal(NoteBlockType.Photo, task.Body[1].Type);
        Assert.Equal(Baseline, task.ModifiedAt);
        Assert.False(onChangedCalled);
    }

    [Fact]
    public void PrimaryBlock_ReadAfterLiveMergeLeavesNonTextFirstBlock_NormalizesWithoutBumpingModifiedAt()
    {
        var task = new TaskItem { ModifiedAt = Baseline };
        var vm = Create(task, out _);

        // Simulate TaskSyncMerge.ApplyTaskFields replacing Body in place on the live, already-open
        // task - a remote edit can legitimately leave a non-Text block at index 0 while this exact
        // task is open in the detail view.
        var mergedModifiedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        task.Body.Clear();
        task.Body.Add(new NoteBlock { Type = NoteBlockType.Link, Url = "https://example.com" });
        task.ModifiedAt = mergedModifiedAt; // ApplyTaskFields sets this explicitly right after Body.Add

        var primary = vm.PrimaryBlock;

        Assert.Equal(NoteBlockType.Text, primary.Type);
        Assert.Equal(2, task.Body.Count);
        Assert.Equal(NoteBlockType.Link, task.Body[1].Type);
        Assert.Equal(mergedModifiedAt, task.ModifiedAt);
    }

    [Fact]
    public void PrimaryBlock_WhenAlreadyNormalized_ReturnsSameInstanceWithoutMutatingBody()
    {
        var task = new TaskItem();
        task.Body.Add(new NoteBlock { Type = NoteBlockType.Text, Text = "hello" });
        var vm = Create(task, out _);

        var first = vm.PrimaryBlock;
        var second = vm.PrimaryBlock;

        Assert.Same(first, second);
        Assert.Single(task.Body);
    }
}

// Regression coverage for "new tags aren't selectable in the dropdown": FilteredAvailableTags only
// ever listed tags that already exist elsewhere, so a name nobody had used yet had no click target
// at all - Enter still created it (AddTagCommand), but there was nothing to click, which read as
// broken. CanCreateNewTag/NewTagPreview back the "+ Create" row that fixes this.
public class TaskDetailViewModelTagTests
{
    private static TaskDetailViewModel Create(TaskItem task, IEnumerable<string>? allTags = null)
        => new(task, () => { }, () => allTags ?? Array.Empty<string>(), () => { }, (_, _) => { });

    [Fact]
    public void CanCreateNewTag_EmptyText_IsFalse()
    {
        var vm = Create(new TaskItem());
        vm.ToggleTagPopupCommand.Execute(null);

        Assert.Equal(string.Empty, vm.NewTagText);
        Assert.False(vm.CanCreateNewTag);
    }

    [Fact]
    public void CanCreateNewTag_NameNobodyHasUsedYet_IsTrue()
    {
        var vm = Create(new TaskItem(), allTags: new[] { "work" });
        vm.ToggleTagPopupCommand.Execute(null); // populates _availableTags from allTags

        vm.NewTagText = "brand-new-tag";

        Assert.True(vm.CanCreateNewTag);
        Assert.Equal("brand-new-tag", vm.NewTagPreview);
    }

    [Fact]
    public void CanCreateNewTag_MatchesAnExistingGlobalTag_IsFalse()
    {
        var vm = Create(new TaskItem(), allTags: new[] { "work" });
        vm.ToggleTagPopupCommand.Execute(null);

        vm.NewTagText = "WORK"; // case-insensitive match

        Assert.False(vm.CanCreateNewTag);
    }

    [Fact]
    public void CanCreateNewTag_MatchesATagAlreadyOnThisTask_IsFalse()
    {
        var task = new TaskItem();
        task.Tags.Add("urgent");
        var vm = Create(task);
        vm.ToggleTagPopupCommand.Execute(null);

        vm.NewTagText = "urgent";

        Assert.False(vm.CanCreateNewTag);
    }

    [Fact]
    public void NewTagPreview_TrimsHashPrefixAndLowercases()
    {
        var vm = Create(new TaskItem());
        vm.ToggleTagPopupCommand.Execute(null);

        vm.NewTagText = "  #Finance ";

        Assert.Equal("finance", vm.NewTagPreview);
    }

    [Fact]
    public void AddTagCommand_ForNewName_AddsExactlyThePreviewedTag()
    {
        var task = new TaskItem();
        var vm = Create(task);
        vm.ToggleTagPopupCommand.Execute(null);
        vm.NewTagText = "  #Finance ";

        vm.AddTagCommand.Execute(null);

        Assert.Equal(new[] { "finance" }, task.Tags);
    }

    // Matches Tasky Web's addTag() (docs/js/app.js): stray spaces/punctuation are stripped, not just
    // the leading '#', so a tag created here can't contain characters that would break "#tag"
    // quick-add parsing or the "tag:name" search operator on either platform.
    [Fact]
    public void NewTagPreview_StripsSpacesAndPunctuation()
    {
        var vm = Create(new TaskItem());
        vm.ToggleTagPopupCommand.Execute(null);

        vm.NewTagText = "my tag, #1!";

        Assert.Equal("mytag1", vm.NewTagPreview);
    }

    [Fact]
    public void AddTagCommand_WithSpacesAndPunctuation_AddsSanitizedTag()
    {
        var task = new TaskItem();
        var vm = Create(task);
        vm.ToggleTagPopupCommand.Execute(null);
        vm.NewTagText = "my tag, #1!";

        vm.AddTagCommand.Execute(null);

        Assert.Equal(new[] { "mytag1" }, task.Tags);
    }
}
