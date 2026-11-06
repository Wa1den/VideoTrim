using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VideoTrim;

/// <summary>
/// Test runs with --sandbox: a hidden window that takes its commands from a file, as in MqttPc
/// (Docs/Работа.md, «Проверка изменений»).
/// </summary>
/// <remarks>
/// Earlier runs drove the real window from outside: SetForegroundWindow, SendKeys, UI
/// Automation patterns, and a swapped %AppData%\VideoTrim\config.json. Each of these takes the
/// foreground and the keyboard away from whatever the user is doing. Now nothing outside the
/// process touches the window: tools/probe.ps1 appends commands to probe.in in the sandbox
/// folder, the window carries them out on its own thread and answers in probe.out, and
/// snapshots are rendered by WPF itself.
///
/// Commands, one per line; times as in the start and end fields (m:ss, h:mm:ss or seconds):
///   open file                  — open a video, as the open button does
///   start t / end t            — the start or end of the clip, as typed into its field
///   seek t                     — move the playhead
///   step n                     — n frames forward, or back for a negative n (Ctrl + arrows)
///   tab n                      — the tab of the export card, from 0
///   combo name=item            — pick a list item by its text, or by index as #n; lists:
///                                codec mode quality size fps audiocodec audiorate format language
///   check name=on|off          — reencode copy aspect samebitrate keepaudio sameaudio
///   bitrate kbps               — the bitrate field
///   export file                — save the clip there, without the save dialog
///   wait                       — until the export ends; answers with the status line
///   state                      — clip, playhead, lists and status in one line
///   snap file.png              — the window content as an image
///   close                      — exit as through the close button
/// </remarks>
public sealed partial class MainWindow
{
    const int DWMWA_CLOAK = 13;

    Task _export = Task.CompletedTask;

    /// <summary>
    /// Shows the sandbox window without activating it and keeps it off the screen.
    /// </summary>
    /// <remarks>
    /// Cloaked, the way Windows hides the windows of another virtual desktop. A minimised WPF
    /// window does not render, and one placed outside every monitor came out as a white
    /// rectangle in PrintWindow (MqttPc). Out of the taskbar too, so nothing appears there to
    /// click. The window recreated on a language change goes through here as well.
    /// </remarks>
    void PlaceOutOfSight()
    {
        ShowActivated = false;
        ShowInTaskbar = false;

        SourceInitialized += (_, _) =>
        {
            int cloak = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_CLOAK, ref cloak, sizeof(int));
        };

        Loaded += async (_, _) =>
        {
            await _ready.Task;
            SandboxProbe.Start();
        };
    }

    /// <summary>
    /// Carries out one command; returns a remark for the answer line, usually empty.
    /// </summary>
    internal async Task<string> Run(string verb, string arg)
    {
        switch (verb)
        {
            case "open":
            {
                var path = Path.GetFullPath(arg);
                await Open(path);
                if (_info?.Path != path) throw new InvalidOperationException(_status.Text);
                await Settled();
                return ": " + _fileInfo.Text;
            }

            case "start":
                NeedFile();
                _timeline.SetStart(Time(arg));
                return ": " + _startBox.Text + " – " + _endBox.Text;

            case "end":
                NeedFile();
                _timeline.SetEnd(Time(arg));
                return ": " + _startBox.Text + " – " + _endBox.Text;

            case "seek":
                NeedFile();
                Seek(Time(arg));
                await Settled();
                return ": " + _time.Text;

            case "step":
            {
                NeedFile();
                int n = int.Parse(arg, CultureInfo.InvariantCulture);
                for (int i = 0; i < Math.Abs(n); i++) StepFrame(Math.Sign(n));
                await Settled();
                return ": " + _time.Text;
            }

            case "tab":
            {
                int index = int.Parse(arg, CultureInfo.InvariantCulture);
                if (index < 0 || index >= _tabBar.Children.Count) throw new InvalidOperationException("нет вкладки " + index);
                ((Button)_tabBar.Children[index]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return "";
            }

            case "combo":
            {
                var (name, value) = Split(arg);
                var combo = Combo(name);
                if (!combo.IsEnabled) throw new InvalidOperationException("список «" + name + "» выключен");

                var items = combo.Items.Cast<object>().ToList();
                object? item = value.StartsWith('#')
                    ? items.ElementAtOrDefault(int.Parse(value[1..], CultureInfo.InvariantCulture))
                    : items.FirstOrDefault(i => string.Equals(Text(i), value, StringComparison.OrdinalIgnoreCase))
                      ?? items.FirstOrDefault(i => Text(i).StartsWith(value, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                    throw new InvalidOperationException("в «" + name + "» нет «" + value + "»: " + string.Join(" | ", items.Select(Text)));

                combo.SelectedItem = item;

                // смена языка пересоздаёт окно: ответ ждёт, пока новое откроет тот же файл
                if (Application.Current.MainWindow is MainWindow next && next != this)
                {
                    await next._ready.Task;
                    await next.Settled();
                }
                return ": " + Text(item);
            }

            case "check":
            {
                var (name, value) = Split(arg);
                ToggleButton box = name switch
                {
                    "reencode" => _reencode,
                    "copy" => _copy,
                    "aspect" => _keepAspect,
                    "samebitrate" => _sameBitrate,
                    "keepaudio" => _keepAudio,
                    "sameaudio" => _sameAudio,
                    _ => throw new InvalidOperationException("нет галки «" + name + "»")
                };
                if (!box.IsEnabled) throw new InvalidOperationException("галка «" + name + "» выключена");
                if (box is RadioButton && value == "off") throw new InvalidOperationException("переключатель снимается выбором другого");
                box.IsChecked = value switch
                {
                    "on" => true,
                    "off" => false,
                    _ => throw new InvalidOperationException("нужно on или off")
                };
                return "";
            }

            case "bitrate":
                if (!_bitrateBox.IsEnabled) throw new InvalidOperationException("поле битрейта выключено");
                _bitrateBox.Text = arg;
                return "";

            case "export":
            {
                NeedFile();
                if (!_exportBtn.IsEnabled) throw new InvalidOperationException("сохранение недоступно");
                if (ReadOptions(_info!) is not ExportOptions options) throw new InvalidOperationException(_status.Text);
                _export = Save(_info!, options, Path.GetFullPath(arg));
                return ": " + _status.Text;
            }

            case "wait":
                await _export;
                return ": " + _status.Text;

            case "state":
                return ": " + State();

            case "snap":
                Snap(Path.GetFullPath(arg));
                return "";

            default:
                throw new InvalidOperationException("неизвестная команда");
        }
    }

    internal bool RebuildingUi => _rebuildingUi;

    void NeedFile()
    {
        if (_info == null) throw new InvalidOperationException("файл не открыт");
    }

    static double Time(string text) =>
        TimeText.TryParse(text, out double t) ? t : throw new InvalidOperationException("время не разобрано: " + text);

    static (string Name, string Value) Split(string arg)
    {
        int eq = arg.IndexOf('=');
        if (eq < 0) throw new InvalidOperationException("нужно «имя=значение»");
        return (arg[..eq].Trim(), arg[(eq + 1)..].Trim());
    }

    ComboBox Combo(string name) => name switch
    {
        "codec" => _codec,
        "mode" => _rateMode,
        "quality" => _quality,
        "size" => _resolution,
        "fps" => _fps,
        "audiocodec" => _audioCodec,
        "audiorate" => _audioRate,
        "format" => _format,
        "language" => _language,
        _ => throw new InvalidOperationException("нет списка «" + name + "»")
    };

    static string Text(object? item) => item switch
    {
        null => "",
        ComboBoxItem { Content: var content } => content?.ToString() ?? "",
        _ => item.ToString() ?? ""
    };

    string State()
    {
        var parts = new List<string>
        {
            "language=" + Loc.Language,
            "file=" + (_info == null ? "-" : Path.GetFileName(_info.Path)),
            "clip=" + _startBox.Text + "–" + _endBox.Text,
            "time=" + _time.Text,
            "mode=" + (_copy.IsChecked == true ? "copy" : "reencode")
        };
        foreach (var name in new[] { "codec", "mode", "quality", "size", "fps", "audiocodec", "audiorate", "format" })
            parts.Add(name + "=" + Text(Combo(name).SelectedItem) + (Combo(name).IsEnabled ? "" : " (off)"));
        parts.Add("bitrate=" + _bitrateBox.Text);
        parts.Add("estimate=" + _estimate.Text);
        parts.Add("status=" + _status.Text);
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Waits until the picture matches the playhead: the ffmpeg frame laid over the player
    /// while paused, the position the player takes on opening, and the strip of thumbnails.
    /// </summary>
    async Task Settled()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline
               && (_stillBusy || _stillWanted != null || (_pendingSeek != null && !_mediaFailed) || !_thumbs.IsCompleted))
            await Task.Delay(50);
    }

    /// <summary>
    /// The window content rendered by WPF itself, on the opaque theme background.
    /// </summary>
    /// <remarks>
    /// The content has no background of its own (the window shows Mica through it), so it is
    /// drawn over the solid theme colour; without that the text would sit on transparency.
    /// The caption buttons are drawn by DWM and are not in the picture.
    /// </remarks>
    void Snap(string path)
    {
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Ui.PanelSolid, null, size);
            // без Viewbox кисть растягивает на прямоугольник границы нарисованного, а они
            // начинаются за полем сетки: края окна обрезались, а всё остальное растягивалось
            dc.DrawRectangle(new VisualBrush(root) { Viewbox = size, ViewboxUnits = BrushMappingMode.Absolute }, null, size);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
                                            (int)Math.Ceiling(size.Height * scale),
                                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

/// <summary>
/// Reads probe.in and answers in probe.out for whichever window is current. One for the whole
/// run rather than one per window: a language change replaces the window in the middle of a
/// command, and the next command has to reach the new one.
/// </summary>
static class SandboxProbe
{
    static DispatcherTimer? _timer;
    static long _offset;
    static readonly Queue<string> _queue = new();
    static bool _busy;

    static string In => Path.Combine(App.Sandbox!, "probe.in");
    static string Out => Path.Combine(App.Sandbox!, "probe.out");

    public static void Start()
    {
        if (_timer != null) return;
        try
        {
            // обе стороны начинают с пустого файла: команды прошлой пробы не повторяются
            File.WriteAllText(In, "");
            File.WriteAllText(Out, "");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("песочница: файлы пробы недоступны: " + ex.Message);
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Read();
        _timer.Start();
    }

    static void Read()
    {
        string text;
        try
        {
            using var stream = new FileStream(In, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= _offset) return;

            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch
        {
            return;   // файл пишут прямо сейчас — прочитается на следующем такте
        }

        // недописанная строка остаётся до следующего такта
        int end = text.LastIndexOf('\n');
        if (end < 0) return;

        _offset += Encoding.UTF8.GetByteCount(text[..(end + 1)]);

        foreach (var raw in text[..end].Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length > 0) _queue.Enqueue(line);
        }

        RunNext();
    }

    /// <summary>
    /// Runs one command and answers once the window has settled after it.
    /// </summary>
    /// <remarks>
    /// The answer waits for ApplicationIdle and for the rebuild flag to drop. In MqttPc an
    /// answer written straight away let the next command reach a page that was not laid out
    /// yet: a snapshot came out blank, and the next switch was swallowed by the flag.
    /// </remarks>
    static async void RunNext()
    {
        if (_busy || _queue.Count == 0) return;
        _busy = true;

        string line = _queue.Dequeue();
        int space = line.IndexOf(' ');
        string verb = space < 0 ? line : line[..space];
        string arg = space < 0 ? "" : line[(space + 1)..].Trim();

        string answer;
        try
        {
            if (verb == "close")
            {
                // ответ сразу: после закрытия отвечать будет некому
                Write("ok close");
                _queue.Clear();
                _timer?.Stop();
                Application.Current.MainWindow?.Close();
                return;
            }

            if (Application.Current.MainWindow is not MainWindow window)
                throw new InvalidOperationException("окна нет");

            answer = "ok " + line + await window.Run(verb, arg);
        }
        catch (Exception ex)
        {
            answer = "error " + line + ": " + ex.Message;
        }

        var dispatcher = Application.Current.Dispatcher;
        do await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        while (Application.Current.MainWindow is MainWindow { RebuildingUi: true });

        Write(answer);
        _busy = false;
        RunNext();
    }

    static void Write(string answer)
    {
        try { File.AppendAllText(Out, answer.Replace('\n', ' ').Replace("\r", "") + Environment.NewLine); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("песочница: " + ex.Message); }
    }
}
