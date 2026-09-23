using System.Windows;

namespace VideoTrim;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // файл можно передать аргументом или перетащить на exe
        new MainWindow(e.Args.FirstOrDefault()).Show();
    }
}
