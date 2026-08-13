using System.Collections.ObjectModel;
using TodoApp.Models;

namespace TodoApp.Services;

public class AppState
{
    public ObservableCollection<TaskItem> Tasks { get; set; } = new();
}
