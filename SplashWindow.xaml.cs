using System.Windows;
using System.Windows.Input;
using Nairdwood.Launcher.Services;

namespace Nairdwood.Launcher;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkChrome(this);
    }

    private void SplashWindow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }
}
