using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using TodoApp.Services;
using TodoApp.ViewModels;

namespace TodoApp;

public partial class WelcomeWindow : Window
{
    private readonly MainViewModel _viewModel;

    public WelcomeWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _viewModel = viewModel;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // NavigateUri is a fixed literal set in XAML, not data loaded from disk/user input, so
        // this doesn't need the scheme-allowlisting that link blocks loaded from the .tasky file
        // require elsewhere in the app (see AboutWindow's identical handler).
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        if (AddSamplesCheck.IsChecked == true)
        {
            _viewModel.AddDemoTask("Welcome to Tasky! Click a task to open the editor #getting-started");
            _viewModel.AddDemoTask(
                "This due date came from typing !due:today @9am in Quick Add - try Ctrl+Alt+T",
                "!due:today @9am");
            _viewModel.AddDemoTask(
                "Ctrl+Alt+T, the tray icon, or the toolbar's Quick Add button open a floating capture box from anywhere #getting-started");
            _viewModel.AddDemoTask("Click this task's checkbox to mark it done #getting-started");
        }
        Close();
    }
}
