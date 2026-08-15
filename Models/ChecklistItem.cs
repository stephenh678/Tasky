using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoApp.Models;

/// <summary>
/// A single item in a task's checklist block.
/// </summary>
public class ChecklistItem : INotifyPropertyChanged
{
    private const int MaxTextLength = 500;
    
    private string _text = string.Empty;
    private bool _isChecked;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The text of this checklist item. Limited to 500 characters.
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            var truncated = trimmed.Length > MaxTextLength ? trimmed.Substring(0, MaxTextLength) : trimmed;
            SetField(ref _text, truncated);
        }
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
