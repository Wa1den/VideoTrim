using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoTrim;

/// <summary>
/// Ruler, a strip of frames, the selected range with two handles and the playhead.
///
/// Handles snap to whole seconds, the playhead does not: the range is what gets exported,
/// and the playhead is only where the picture is shown.
/// </summary>
public sealed class Timeline : FrameworkElement
{
    public const double RulerH = 20, StripH = 64;
    const double HandleW = 8, Grip = 9;

    readonly List<(double T, BitmapSource Img)> _thumbs = new();
    double _aspect = 16.0 / 9;

    enum Drag { None, Start, End, Playhead }
    Drag _drag;

    public double Duration { get; private set; }
    public double Start { get; private set; }
    public double End { get; private set; }
    public double Position { get; private set; }

    /// <summary>The shortest range, one second or the whole file if it is shorter.</summary>
    double MinLen => Math.Min(1, Duration);

    /// <summary>The user dragged or clicked the playhead.</summary>
    public event Action<double>? Seek;

    public event Action? RangeChanged;

    /// <summary>True when the playhead drag begins, false when it ends.</summary>
    public event Action<bool>? Scrubbing;

    public Timeline()
    {
        Height = RulerH + StripH + 2;
        Cursor = Cursors.Arrow;
        Focusable = false;
        Ui.ThemeChanged += InvalidateVisual;
    }

    public void Reset(double duration, double aspect)
    {
        Duration = duration;
        _aspect = aspect > 0 ? aspect : 16.0 / 9;
        _thumbs.Clear();
        Start = 0;
        End = duration;
        Position = 0;
        InvalidateVisual();
    }

    public void AddThumb(double t, BitmapSource img)
    {
        _thumbs.Add((t, img));
        InvalidateVisual();
    }

    public void SetPosition(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        if (t == Position) return;
        Position = t;
        InvalidateVisual();
    }

    public void SetStart(double s)
    {
        Start = Math.Clamp(s, 0, Math.Max(0, End - MinLen));
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    public void SetEnd(double e)
    {
        End = Math.Clamp(e, Math.Min(Duration, Start + MinLen), Duration);
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    double X(double t) => Duration > 0 ? t / Duration * ActualWidth : 0;
    double T(double x) => ActualWidth > 0 ? Math.Clamp(x / ActualWidth, 0, 1) * Duration : 0;

    /// <summary>Whole seconds, except the very end, which is rarely a whole second.</summary>
    double Snap(double t) => t > Duration - 0.5 ? Duration : Math.Round(t);

    static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)));
    static readonly Brush White = Brushes.White;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        var strip = new Rect(0, RulerH, w, StripH);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        dc.DrawRoundedRectangle(Ui.Panel, new Pen(Ui.PanelStroke, 1), strip, 4, 4);
        if (Duration <= 0 || w <= 0) return;

        DrawRuler(dc, w);

        dc.PushClip(new RectangleGeometry(strip, 4, 4));
        if (_thumbs.Count > 0)
        {
            double tw = StripH * _aspect;
            for (double x = 0; x < w; x += tw)
            {
                double t = T(x + tw / 2);
                var best = _thumbs.MinBy(p => Math.Abs(p.T - t));
                dc.DrawImage(best.Img, new Rect(x, RulerH, tw, StripH));
            }
        }

        double xs = X(Start), xe = X(End);
        dc.DrawRectangle(Shade, null, new Rect(0, RulerH, xs, StripH));
        dc.DrawRectangle(Shade, null, new Rect(xe, RulerH, Math.Max(0, w - xe), StripH));
        dc.Pop();

        var accent = Ui.Accent;
        dc.DrawRectangle(null, new Pen(accent, 2), new Rect(xs + 1, RulerH + 1, Math.Max(0, xe - xs - 2), StripH - 2));
        DrawHandle(dc, accent, xs);
        DrawHandle(dc, accent, xe - HandleW);

        // курсор воспроизведения: линия и треугольник на линейке
        double xp = Math.Round(X(Position)) + 0.5;
        var fg = Ui.Fg;
        dc.DrawLine(new Pen(fg, 2), new Point(xp, RulerH - 4), new Point(xp, RulerH + StripH));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(xp - 5, RulerH - 10), true, true);
            g.LineTo(new Point(xp + 5, RulerH - 10), true, false);
            g.LineTo(new Point(xp, RulerH - 3), true, false);
        }
        dc.DrawGeometry(fg, null, tri);
    }

    void DrawHandle(DrawingContext dc, Brush accent, double x)
    {
        dc.DrawRoundedRectangle(accent, null, new Rect(x, RulerH, HandleW, StripH), 2, 2);
        double cx = Math.Round(x + HandleW / 2) + 0.5, cy = RulerH + StripH / 2;
        dc.DrawLine(new Pen(White, 1), new Point(cx, cy - Grip), new Point(cx, cy + Grip));
    }

    static readonly double[] Steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    void DrawRuler(DrawingContext dc, double w)
    {
        bool hours = Duration >= 3600;
        double minPx = hours ? 80 : 60;
        double step = Steps.FirstOrDefault(s => s / Duration * w >= minPx, Steps[^1]);

        var dim = Ui.FgDim;
        var pen = new Pen(dim, 1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface("Segoe UI");

        for (double t = 0; t <= Duration + 1e-6; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(pen, new Point(x, RulerH - 6), new Point(x, RulerH));

            var text = new FormattedText(TimeText.Format(t, hours), CultureInfo.CurrentCulture,
                                         FlowDirection.LeftToRight, face, 11, dim, dpi);
            if (x + 3 + text.Width <= w) dc.DrawText(text, new Point(x + 3, 1));
        }
    }

    // ---------- мышь ----------

    Drag HitHandle(double x)
    {
        double xs = X(Start) + HandleW / 2, xe = X(End) - HandleW / 2;
        double ds = Math.Abs(x - xs), de = Math.Abs(x - xe);
        const double reach = HandleW;
        if (ds > reach && de > reach) return Drag.None;
        return ds < de || (ds == de && x < xs) ? Drag.Start : Drag.End;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        var p = e.GetPosition(this);
        _drag = p.Y >= RulerH ? HitHandle(p.X) : Drag.None;
        if (_drag == Drag.None)
        {
            _drag = Drag.Playhead;
            Scrubbing?.Invoke(true);
        }
        CaptureMouse();
        Apply(p.X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag != Drag.None) { Apply(p.X); return; }
        Cursor = Duration > 0 && p.Y >= RulerH && HitHandle(p.X) != Drag.None ? Cursors.SizeWE : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_drag == Drag.Playhead) Scrubbing?.Invoke(false);
        _drag = Drag.None;
    }

    void Apply(double x)
    {
        switch (_drag)
        {
            case Drag.Start: SetStart(Snap(T(x))); break;
            case Drag.End: SetEnd(Snap(T(x))); break;
            case Drag.Playhead: Seek?.Invoke(T(x)); break;
        }
    }

    protected override Size MeasureOverride(Size available) => new(0, RulerH + StripH + 2);
}

public static class TimeText
{
    /// <summary>m:ss or h:mm:ss; tenths only when asked for and the value is not whole.</summary>
    public static string Format(double s, bool hours, bool tenths = false)
    {
        s = Math.Max(0, s);
        s = tenths ? Math.Round(s, 1) : Math.Floor(s + 1e-6);
        var ts = TimeSpan.FromSeconds(s);
        string r = hours
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
        int tenth = (int)Math.Round((s - Math.Floor(s)) * 10);
        if (tenths && tenth > 0) r += (Loc.Language == "ru" ? "," : ".") + tenth;
        return r;
    }

    /// <summary>Accepts h:mm:ss, m:ss or plain seconds, with a comma or a dot for fractions.</summary>
    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 1 or > 3) return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0)
                return false;
            if (!last && v != Math.Floor(v)) return false;
            total = total * 60 + v;
        }

        seconds = total;
        return true;
    }
}
