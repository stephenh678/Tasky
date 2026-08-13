using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using TodoApp.Services;

namespace TodoApp;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // NavigateUri is a fixed literal set in XAML, not data loaded from disk/user input, so
        // this doesn't need the scheme-allowlisting that link blocks loaded from the .tasky file
        // require elsewhere in the app.
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
