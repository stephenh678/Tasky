using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TodoApp.Services;

namespace TodoApp;

public partial class PhotoViewerWindow : Window
{
    public PhotoViewerWindow(string path)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        PhotoImage.Source = image;

        Title = Path.GetFileName(path);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
