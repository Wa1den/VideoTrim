using System.IO;
using System.Windows;

namespace VideoTrim;

public partial class App : Application
{
    /// <summary>The folder after --sandbox, or null for a normal run.</summary>
    public static string? Sandbox { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args.ToList();
        int flag = args.IndexOf("--sandbox");
        if (flag >= 0 && flag + 1 < args.Count)
        {
            // пробный запуск: настройки и языки из своей папки, настоящий config.json не трогается
            Sandbox = Path.GetFullPath(args[flag + 1]);
            Directory.CreateDirectory(Sandbox);
            Config.Folder = Sandbox;
            Loc.Configure(Path.Combine(Sandbox, "lang"));
            args.RemoveRange(flag, 2);
        }

        var cfg = Config.Load();
        Loc.Load(cfg.Language ?? Loc.SystemDefault());

        // файл можно передать аргументом или перетащить на exe
        new MainWindow(args.FirstOrDefault(), cfg).Show();
    }
}
