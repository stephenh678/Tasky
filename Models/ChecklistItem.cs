using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoApp.Models;

public class ChecklistItem : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isChecked;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
