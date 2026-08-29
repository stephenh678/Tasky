using System;
using System.Globalization;
using System.Windows.Data;

namespace TodoApp.Converters;

// Backs the sidebar's selection highlight (see SidebarListBoxItem in ControlStyles.xaml) - three
// separate ListBoxes (Sidebar/Tags/Views) share one bound MainViewModel.SelectedSidebarItem, and
// relying on Selector.SelectedItem's own TwoWay binding to clear a list's highlight once the
// shared value points at an item that isn't in that list's own Items turned out not to hold up in
// practice (reported live: switching between a saved View, a Tag, and a built-in section could
// leave more than one of the three highlighted at once, with no way to clear it short of typing in
// the search box). Comparing the item to the shared value directly, via Equals (SidebarFilterItem
// overrides it) rather than reference identity, sidesteps that entirely - the highlight is always
// computed fresh from "is this actually the current selection," never inherited stale state.
public class ObjectsEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
