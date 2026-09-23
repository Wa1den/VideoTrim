using System.Windows;

namespace VideoTrim;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var cfg = Config.Load();
        Loc.Load(cfg.Language ?? Loc.SystemDefault());

        // файл можно передать аргументом или перетащить на exe
        new MainWindow(e.Args.FirstOrDefault(), cfg).Show();
    }
}
