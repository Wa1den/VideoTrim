using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VideoTrim;

public sealed class AboutWindow : Window
{
    public const string Repo = "https://github.com/Wa1den/VideoTrim";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    readonly TextBlock _update = Para("");

    public AboutWindow(Window owner, string? ffmpeg, Config cfg, UpdateCheck.Result? known)
    {
        Owner = owner;
        Title = Loc.T("about.title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.WatchTheme(this);

        var logo = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        try { logo.Source = MainWindow.Logo(48); } catch (Exception) { }

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "VideoTrim", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.Fg });
        name.Children.Add(new TextBlock { Text = Loc.T("about.version", Version), FontSize = 12, Foreground = Ui.FgDim });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(logo);
        head.Children.Add(name);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(head);
        panel.Children.Add(Para(Loc.T("about.text")));
        panel.Children.Add(Para(ffmpeg != null ? Loc.T("about.ffmpeg", ffmpeg) : Loc.T("about.ffmpeg.missing")));

        var repo = Para(Loc.T("about.source"));
        repo.Inlines.Add(Link(Repo, Repo));
        panel.Children.Add(repo);

        // обновления: проверка при запуске выключена по умолчанию, это единственное
        // обращение программы в сеть
        var auto = new CheckBox { Content = Loc.T("about.updates.auto"), MinWidth = 0, IsChecked = cfg.CheckUpdates };
        auto.Checked += (_, _) => cfg.CheckUpdates = true;
        auto.Unchecked += (_, _) => cfg.CheckUpdates = false;
        auto.Margin = new Thickness(0, 4, 0, 6);
        panel.Children.Add(auto);

        var now = Ui.Btn(Loc.T("about.updates.check"), async () => await CheckNow());
        _update.Margin = new Thickness(12, 0, 0, 0);
        _update.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Children = { now, _update } };
        DockPanel.SetDock(now, Dock.Left);
        panel.Children.Add(row);
        if (known != null) ShowResult(known);

        var close = Ui.Btn(Loc.T("about.close"), Close);
        close.IsCancel = true;
        close.IsDefault = true;
        close.Margin = new Thickness(0, 12, 0, 0);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(close);

        Content = panel;
    }

    async Task CheckNow()
    {
        _update.Text = Loc.T("about.updates.checking");
        ShowResult(await UpdateCheck.Check());
    }

    void ShowResult(UpdateCheck.Result r)
    {
        _update.Inlines.Clear();
        if (r.Newer != null)
        {
            _update.Inlines.Add(Loc.T("about.updates.available", r.Newer));
            _update.Inlines.Add(Link(Loc.T("about.updates.page"), r.Url));
        }
        else if (r.Error != null) _update.Text = Loc.T("about.updates.failed", r.Error);
        else _update.Text = Loc.T("about.updates.latest");
    }

    static Hyperlink Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text));
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        };
        return link;
    }

    static TextBlock Para(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = Ui.TextSize,
        Foreground = Ui.FgDim,
        Margin = new Thickness(0, 0, 0, 10)
    };
}
