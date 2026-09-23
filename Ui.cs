using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoTrim;

/// <summary>
/// The small controls the window is built from, cut down from the CaseLight set.
///
/// Nothing here carries a colour of its own: every brush is an alias declared in App.xaml
/// over a Fluent theme token.
/// </summary>
public static class Ui
{
    public const double TextSize = 14;

    /// <summary>
    /// Application resources rather than the window's own: the window indexer would return
    /// null for these keys.
    /// </summary>
    static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush Fg => Res("Fg");
    public static Brush FgDim => Res("FgDim");
    public static Brush Panel => Res("Panel");
    public static Brush PanelStroke => Res("PanelStroke");
    public static Brush Warn => Res("Warn");

    public static Brush Accent =>
        Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush;

    public static FontFamily IconFont =>
        Application.Current?.TryFindResource("Icons") as FontFamily ?? new FontFamily("Segoe UI");

    static readonly (string Alias, string Colour)[] Aliases =
    {
        ("Fg", "TextFillColorPrimary"),
        ("FgDim", "TextFillColorSecondary"),
        ("Warn", "SystemFillColorCaution"),
        ("Panel", "CardBackgroundFillColorDefault"),
        ("PanelStroke", "CardStrokeColorDefault"),
        ("PanelSolid", "SolidBackgroundFillColorBase")
    };

    /// <summary>Raised once the aliases hold the colours of a newly applied theme.</summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// A DynamicResource on an alias colour is resolved once: the brushes live in the
    /// application dictionary, outside the element tree, and a theme switch only updates
    /// references inside the tree. The window holds a reference to one token through this
    /// property, and its change copies the new colours into the shared brushes.
    /// </summary>
    public static readonly DependencyProperty ThemeProbeProperty = DependencyProperty.RegisterAttached(
        "ThemeProbe", typeof(object), typeof(Ui), new PropertyMetadata(null, (_, _) => RefreshAliases()));

    public static void WatchTheme(FrameworkElement root) =>
        root.SetResourceReference(ThemeProbeProperty, "TextFillColorPrimary");

    static void RefreshAliases()
    {
        var app = Application.Current;
        if (app == null) return;

        foreach (var (alias, colour) in Aliases)
            if (app.Resources[alias] is SolidColorBrush { IsFrozen: false } brush
                && app.TryFindResource(colour) is Color c)
                brush.Color = c;

        ThemeChanged?.Invoke();
    }

    public static Border Card(UIElement child)
    {
        var border = new Border { Child = child };
        if (Application.Current?.TryFindResource("Card") is Style style) border.Style = style;
        return border;
    }

    /// <summary>
    /// The popup inherits the font of the element it belongs to, and the icon font has no
    /// letters, so the tooltip text names its own.
    /// </summary>
    public static TextBlock HelpIcon(string text)
    {
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = IconFont,
            FontSize = 12,
            Foreground = FgDim,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Help,
            ToolTip = Tip(text)
        };

        ToolTipService.SetInitialShowDelay(icon, 200);
        ToolTipService.SetShowDuration(icon, 60000);
        return icon;
    }

    public static TextBlock Tip(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 320,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 12
    };

    public static UIElement Caption(string label, string? help = null)
    {
        var caption = new TextBlock
        {
            Text = label,
            Foreground = FgDim,
            FontSize = TextSize,
            VerticalAlignment = VerticalAlignment.Center
        };

        return help == null ? caption : WithHelpIcon(caption, help);
    }

    public static UIElement Labeled(string label, UIElement editor, string? help = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 16, 4) };
        panel.Children.Add(Caption(label, help));
        panel.Children.Add(editor);
        return panel;
    }

    public static StackPanel WithHelpIcon(UIElement element, string help)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(element);
        row.Children.Add(HelpIcon(help));
        return row;
    }

    public static Button Btn(string caption, Action onClick, bool accent = false, string? tip = null)
    {
        var b = new Button
        {
            Content = caption,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (accent && Application.Current?.TryFindResource("AccentButtonStyle") is Style style)
            b.Style = style;
        if (tip != null) b.ToolTip = Tip(tip);

        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button IconBtn(string glyph, Action onClick, string tip)
    {
        var b = Btn("", onClick, tip: tip);
        b.Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 16 };
        b.Padding = new Thickness(10, 7, 10, 7);

        // у значка нет текста, и экранный диктор без имени читает пустую кнопку
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>
    /// Page switcher in the manner of the WinUI 3 SelectorBar: plain captions, the chosen one
    /// in the primary colour with a short accent bar under it. The Fluent TabControl of
    /// WPF draws grey plates of fixed width with a barely darker selected plate, and the
    /// headers did not read as tabs at all.
    ///
    /// Every page stays in one grid cell and the hidden ones keep their place in layout,
    /// so the block is as tall as the tallest page and the video above does not jump.
    /// </summary>
    public static FrameworkElement SelectorBar(params (string Header, FrameworkElement Page)[] items)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-12, -8, 0, 8) };
        var pages = new Grid();
        var captions = new List<(TextBlock Text, Border Pill)>();

        void Select(int index)
        {
            for (int i = 0; i < items.Length; i++)
            {
                bool on = i == index;
                items[i].Page.Visibility = on ? Visibility.Visible : Visibility.Hidden;
                captions[i].Text.SetResourceReference(TextBlock.ForegroundProperty, on ? "Fg" : "FgDim");
                captions[i].Text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                captions[i].Pill.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            }
        }

        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var (header, page) = items[i];
            page.VerticalAlignment = VerticalAlignment.Top;
            pages.Children.Add(page);

            // полужирная подпись шире обычной; невидимая полужирная копия держит ширину,
            // чтобы соседние заголовки не сдвигались при выборе
            var text = new TextBlock { Text = header, FontSize = TextSize, HorizontalAlignment = HorizontalAlignment.Center };
            var widthKeeper = new TextBlock { Text = header, FontSize = TextSize, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Hidden };
            var pill = new Border
            {
                Width = 16,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            pill.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

            var caption = new Grid();
            caption.Children.Add(widthKeeper);
            caption.Children.Add(text);

            var stack = new StackPanel();
            stack.Children.Add(caption);
            stack.Children.Add(pill);

            // кнопка без оформления: подпись с полоской, но с фокусом клавиатуры и
            // нажатием из экранного диктора
            var template = new ControlTemplate(typeof(Button));
            var presenter = new FrameworkElementFactory(typeof(Border));
            presenter.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            presenter.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 4));
            presenter.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = presenter;

            var hit = new Button { Content = stack, Template = template, Cursor = Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(hit, header);
            hit.Click += (_, _) => Select(index);
            hit.MouseEnter += (_, _) => { if (pill.Visibility != Visibility.Visible) text.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); };
            hit.MouseLeave += (_, _) => { if (pill.Visibility != Visibility.Visible) text.SetResourceReference(TextBlock.ForegroundProperty, "FgDim"); };

            captions.Add((text, pill));
            bar.Children.Add(hit);
        }
        Select(0);

        var root = new StackPanel();
        root.Children.Add(bar);
        root.Children.Add(pages);
        return root;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var i in items) panel.Children.Add(i);
        return panel;
    }
}
