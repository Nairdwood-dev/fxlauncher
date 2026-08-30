using System.Windows;

namespace Nairdwood.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.IsEnabled = false;

        var splash = new SplashWindow { Owner = mainWindow };
        splash.Closed += (_, _) =>
        {
            if (!mainWindow.IsVisible) return;
            mainWindow.IsEnabled = true;
            mainWindow.Activate();
        };
        splash.Show();
    }
}
