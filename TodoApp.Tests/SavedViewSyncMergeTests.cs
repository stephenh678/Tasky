using System.Collections.Generic;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class SavedViewSyncMergeTests
{
    [Fact]
    public void ViewAddedOnOneSide_MergesIntoTheOther()
    {
        var local = new List<SavedView> { new() { Id = "a", Label = "Overdue", Query = "is:overdue" } };
        var remote = new List<SavedView>();

        var (merged, _) = SavedViewSyncMerge.Merge(local, remote, new List<string>(), new List<string>());

        Assert.Single(merged);
        Assert.Equal("a", merged[0].Id);
    }

    [Fact]
    public void TwoIndependentlyAddedViews_BothSurviveMerge()
    {
        var local = new List<SavedView> { new() { Id = "a", Label = "Overdue", Query = "is:overdue" } };
        var remote = new List<SavedView> { new() { Id = "b", Label = "Pinned", Query = "is:pinned" } };

        var (merged, _) = SavedViewSyncMerge.Merge(local, remote, new List<string>(), new List<string>());

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, v => v.Id == "a");
        Assert.Contains(merged, v => v.Id == "b");
    }

    [Fact]
    public void ViewDeletedLocally_DoesNotResurrectFromStaleRemoteCopy()
    {
        var local = new List<SavedView>();
        var remote = new List<SavedView> { new() { Id = "a", Label = "Overdue", Query = "is:overdue" } };
        var localDeletedIds = new List<string> { "a" };

        var (merged, mergedDeletedIds) = SavedViewSyncMerge.Merge(local, remote, localDeletedIds, new List<string>());

        Assert.Empty(merged);
        Assert.Contains("a", mergedDeletedIds);
    }

    [Fact]
    public void ViewDeletedRemotely_RemovesLocalCopyToo()
    {
        var local = new List<SavedView> { new() { Id = "a", Label = "Overdue", Query = "is:overdue" } };
        var remote = new List<SavedView>();
        var remoteDeletedIds = new List<string> { "a" };

        var (merged, mergedDeletedIds) = SavedViewSyncMerge.Merge(local, remote, new List<string>(), remoteDeletedIds);

        Assert.Empty(merged);
        Assert.Contains("a", mergedDeletedIds);
    }

    [Fact]
    public void SameIdOnBothSides_LocalWinsTheCollision()
    {
        var local = new List<SavedView> { new() { Id = "a", Label = "Local Label", Query = "tag:local" } };
        var remote = new List<SavedView> { new() { Id = "a", Label = "Remote Label", Query = "tag:remote" } };

        var (merged, _) = SavedViewSyncMerge.Merge(local, remote, new List<string>(), new List<string>());

        Assert.Single(merged);
        Assert.Equal("Local Label", merged[0].Label);
    }

    [Fact]
    public void DeletedIdSets_AreUnionedFromBothSides()
    {
        var (_, mergedDeletedIds) = SavedViewSyncMerge.Merge(
            new List<SavedView>(), new List<SavedView>(),
            new List<string> { "a" }, new List<string> { "b" });

        Assert.Contains("a", mergedDeletedIds);
        Assert.Contains("b", mergedDeletedIds);
    }
}
