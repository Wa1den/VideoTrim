using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace VideoTrim;

/// <summary>
/// The content extended over the title bar, on Mica, as in CaseLight
/// (Docs/Интерфейс.md, «Заголовок окна»). The caption buttons are left to DWM: they bring
/// the snap layouts flyout and the dark theme glyphs, which buttons of our own would not.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The system caption buttons are 30 tall at 100 % and stay at the top edge.</summary>
    const double TitleHeight = 48;

    const double CaptionGap = 8;

    WindowChrome _chrome = null!;
    IntPtr _hwnd;
    FrameworkElement _root = null!;
    FrameworkElement _titleRight = null!;

    /// <summary>Width of the three caption buttons; an estimate until DWM has been asked.</summary>
    double _captionWidth = 146;

    void SetupChrome()
    {
        _chrome = new WindowChrome
        {
            CaptionHeight = TitleHeight,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            UseAeroCaptionButtons = true,
            CornerRadius = new CornerRadius(0)
        };
        WindowChrome.SetWindowChrome(this, _chrome);
        Background = Brushes.Transparent;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(ChromeHook);
            ApplyBackdrop();
            UpdateChromeMetrics();
        };

        StateChanged += (_, _) => UpdateChromeMetrics();
        DpiChanged += (_, _) => UpdateChromeMetrics();
    }

    /// <summary>
    /// The 32 px frame of the icon, scaled down to 16. An ico opened as a single image gives
    /// its first frame, the 16 px one, which blurs when stretched at 150 %.
    /// </summary>
    internal static BitmapSource Logo(int size)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/icon.ico"),
                                           BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.FirstOrDefault(f => f.PixelWidth == size) ?? decoder.Frames[0];
    }

    /// <summary>
    /// The Fluent theme sets Mica on its own when it styles the window, so the backdrop is
    /// written after it, and again after every theme or colour change.
    /// </summary>
    void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;

        int type = DWMSBT_MAINWINDOW;
        DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));

        // с включённым «цветом элементов на заголовках» DWM заливает им полосу заголовка
        // поверх содержимого
        int none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_CAPTION_COLOR, ref none, sizeof(int));
    }

    IntPtr ChromeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyBackdrop));
        return IntPtr.Zero;
    }

    /// <summary>
    /// Keeps the title bar clear of the caption buttons, and the content on screen while
    /// maximised: a maximised window hangs its resize frame 8 px past the screen edges at
    /// 100 %, and with the frame turned into client area the content would go with it.
    /// </summary>
    void UpdateChromeMetrics()
    {
        if (_hwnd == IntPtr.Zero || WindowState == WindowState.Minimized) return;

        var dpi = VisualTreeHelper.GetDpi(this);

        if (DwmGetWindowAttribute(_hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out var bounds, Marshal.SizeOf<RECT>()) == 0
            && bounds.Right > bounds.Left)
            _captionWidth = (bounds.Right - bounds.Left) / dpi.DpiScaleX;

        double frame = 0;
        if (WindowState == WindowState.Maximized)
        {
            uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
            frame = (GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpiValue)
                     + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpiValue)) / dpi.DpiScaleX;
        }

        _root.Margin = new Thickness(frame);
        _chrome.CaptionHeight = TitleHeight + frame;

        // поле сетки справа 12 уже есть, до кнопок остаётся CaptionGap
        _titleRight.Margin = new Thickness(0, 0, Math.Max(0, _captionWidth + CaptionGap - 12), 0);
    }

    const int WM_SETTINGCHANGE = 0x001A;
    const int WM_THEMECHANGED = 0x031A;
    const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    const int DWMSBT_MAINWINDOW = 2;

    const int SM_CXSIZEFRAME = 32;
    const int SM_CXPADDEDBORDER = 92;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
