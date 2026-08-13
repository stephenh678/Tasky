using System.Collections.Generic;
using TodoApp.Models;

namespace TodoApp.Services;

public class AppState
{
    public List<TaskItem> Tasks { get; set; } = new();
}
