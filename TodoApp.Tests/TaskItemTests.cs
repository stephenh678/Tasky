using System.ComponentModel;
using TodoApp.Models;

namespace TodoApp.Tests;

public class TaskItemTests
{
    [Fact]
    public void Text_LongerThan500Characters_IsTruncated()
    {
        var task = new TaskItem { Text = new string('a', 600) };
        Assert.Equal(500, task.Text.Length);
    }

    [Fact]
    public void Text_SetToNull_BecomesEmptyStringRatherThanNull()
    {
        var task = new TaskItem { Text = null! };
        Assert.Equal(string.Empty, task.Text);
    }

    [Fact]
    public void Notes_SetToNull_BecomesEmptyStringRatherThanNull()
    {
        var task = new TaskItem { Notes = null! };
        Assert.Equal(string.Empty, task.Notes);
    }

    [Fact]
    public void SettingText_RaisesPropertyChangedForText()
    {
        var task = new TaskItem();
        var raised = false;
        task.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(TaskItem.Text);

        task.Text = "Something new";

        Assert.True(raised);
    }

    [Fact]
    public void SettingSameTextValue_DoesNotRaisePropertyChanged()
    {
        var task = new TaskItem { Text = "Same" };
        var raiseCount = 0;
        task.PropertyChanged += (_, _) => raiseCount++;

        task.Text = "Same";

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public void NewTaskItem_HasUniqueIdAndDefaultsToNotDoneOrClosed()
    {
        var a = new TaskItem();
        var b = new TaskItem();

        Assert.NotEqual(a.Id, b.Id);
        Assert.False(a.IsDone);
        Assert.False(a.IsClosed);
        Assert.False(a.IsPinned);
        Assert.Null(a.DueDate);
    }

    [Fact]
    public void Tags_StartsEmptyAndAcceptsAdditions()
    {
        var task = new TaskItem();
        task.Tags.Add("finance");

        Assert.Single(task.Tags);
        Assert.Contains("finance", task.Tags);
    }
}

public class NoteBlockTests
{
    [Fact]
    public void Text_LongerThan10000Characters_IsTruncated()
    {
        var block = new NoteBlock { Text = new string('x', 11000) };
        Assert.Equal(10000, block.Text.Length);
    }

    [Fact]
    public void LinkLabel_LongerThan500Characters_IsTruncated()
    {
        var block = new NoteBlock { LinkLabel = new string('y', 600) };
        Assert.Equal(500, block.LinkLabel.Length);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?query=1")]
    public void Url_ValidHttpOrHttpsAbsoluteUri_IsAccepted(string url)
    {
        var block = new NoteBlock { Url = url };
        Assert.Equal(url, block.Url);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Url_InvalidOrNonHttpScheme_IsRejectedToEmptyString(string url)
    {
        var block = new NoteBlock { Url = url };
        Assert.Equal(string.Empty, block.Url);
    }

    [Fact]
    public void Url_IsTrimmedBeforeValidation()
    {
        var block = new NoteBlock { Url = "   https://example.com   " };
        Assert.Equal("https://example.com", block.Url);
    }
}
