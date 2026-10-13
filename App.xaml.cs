using System.Windows;

namespace BetterFlow;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (mainWindow.ShouldStartHiddenInTray)
            mainWindow.StartHiddenInTray();
        else
            mainWindow.Show();
    }
}
