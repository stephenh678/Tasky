using System;

namespace TodoApp.Models;

public class TaskLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
