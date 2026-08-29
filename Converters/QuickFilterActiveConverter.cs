using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using TodoApp.ViewModels;

namespace TodoApp.Converters;

// One-way only: each filter checkbox's IsChecked reflects MainViewModel.ActiveQuickFilters
// (ground truth), while its own Command/CommandParameter is what actually toggles membership -
// same "command-driven, binding-reflects-result" split already used for other checkable controls
// in MainWindow. ConvertBack is never called because the XAML binding is Mode=OneWay.
public class QuickFilterActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is IReadOnlyCollection<QuickFilter> active && parameter is QuickFilter filter && active.Contains(filter);

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
