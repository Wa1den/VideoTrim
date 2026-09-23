using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VideoTrim;

public sealed partial class MainWindow : Window
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly Config _cfg = Config.Load();
    string? _ffmpeg = Media.Find("ffmpeg");
    string? _ffprobe = Media.Find("ffprobe");
    Task<(List<Encoder> Video, List<Encoder> Audio)> _encoders;
    bool _installing;
    UpdateCheck.Result? _update;

    readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        ScrubbingEnabled = true,
        Stretch = Stretch.Uniform
    };
    readonly Image _still = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    readonly TextBlock _placeholder = new()
    {
        Text = "Перетащите видео в окно или откройте его кнопкой «Открыть»",
        Foreground = Brushes.Gray,
        FontSize = Ui.TextSize,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    readonly Timeline _timeline = new() { Margin = new Thickness(0, 10, 0, 6) };

    readonly TextBlock _fileName = new() { FontSize = Ui.TextSize, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _fileInfo = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _time = new() { FontSize = Ui.TextSize, MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _length = new() { FontSize = Ui.TextSize, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, };

    readonly TextBox _startBox = TimeBox(), _endBox = TimeBox();

    readonly Button _openBtn, _aboutBtn, _playBtn, _backBtn, _fwdBtn, _startHere, _endHere;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };

    VideoInfo? _info;
    bool _playing, _mediaFailed, _stillMode, _rebuildingUi, _scrubbing;
    CancellationTokenSource? _thumbsCts;

    // кадр для запасного показа: запрошенная позиция и признак, что ffmpeg уже занят
    double? _stillWanted;
    bool _stillBusy;

    bool Hours => _info?.Duration >= 3600;

    public MainWindow(string? path)
    {
        Title = "VideoTrim";
        Width = 1040;
        Height = 800;
        MinWidth = 960;
        MinHeight = 600;
        RestoreGeometry();
        Ui.WatchTheme(this);
        SetupChrome();
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/icon.ico")); } catch (Exception) { }

        _openBtn = Ui.Btn("Открыть…", PickFile);
        _aboutBtn = Ui.IconBtn("", () => new AboutWindow(this, _ffmpeg, _cfg, _update).ShowDialog(), "О программе");
        _playBtn = Ui.IconBtn("", TogglePlay, "Воспроизведение и пауза, пробел");
        _backBtn = Ui.Btn("−1 с", () => Seek(_timeline.Position - 1), tip: "Стрелка влево, с Shift на 10 секунд");
        _fwdBtn = Ui.Btn("+1 с", () => Seek(_timeline.Position + 1), tip: "Стрелка вправо, с Shift на 10 секунд");
        _startHere = Ui.Btn("Отсюда", StartHere, tip: "Начало фрагмента на текущей секунде, клавиша I");
        _endHere = Ui.Btn("Досюда", EndHere, tip: "Конец фрагмента после текущей секунды, клавиша O");
        Content = Build();

        _encoders = _ffmpeg == null ? Task.FromResult((new List<Encoder>(), new List<Encoder>())) : Media.DetectEncoders(_ffmpeg);

        _media.MediaOpened += (_, _) => { if (!_media.HasVideo) EnterStillMode(false); };
        _media.MediaFailed += (_, _) => EnterStillMode(true);
        _media.MediaEnded += (_, _) => SetPlaying(false);

        _timeline.Seek += Seek;
        _timeline.RangeChanged += ShowRange;

        WireExport();
        _timeline.Scrubbing += on => { _scrubbing = on; UpdateMute(); };

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        PreviewKeyDown += OnKey;
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Open(files[0]);
        };
        Closing += OnClosing;

        _rebuildingUi = true;
        (_cfg.CopyStreams ? _copy : _reencode).IsChecked = true;
        _rebuildingUi = false;

        UpdateEnabled();
        ShowTime();

        Loaded += (_, _) =>
        {
            if (_ffmpeg == null || _ffprobe == null) OfferFfmpeg(path);
            else if (path != null) Open(path);
            if (_cfg.CheckUpdates) CheckUpdates();
        };
    }

    static TextBox TimeBox() => new()
    {
        Width = 84,
        Margin = new Thickness(8, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    UIElement Build()
    {
        _fileName.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _fileInfo.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        _time.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _length.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        var grid = new Grid { Margin = new Thickness(12, 0, 12, 6) };
        foreach (var h in new[] { new GridLength(TitleHeight), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = h });

        // заголовок: логотип, «Открыть», сведения о файле и «О программе» у системных кнопок
        var logo = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(6, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        try { logo.Source = Logo(32); } catch (Exception) { }

        _openBtn.Margin = new Thickness(0);
        _aboutBtn.Margin = new Thickness(12, 0, 0, 0);
        WindowChrome.SetIsHitTestVisibleInChrome(_openBtn, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_aboutBtn, true);
        _titleRight = _aboutBtn;

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        names.Children.Add(_fileName);
        names.Children.Add(_fileInfo);

        var header = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(logo, Dock.Left);
        DockPanel.SetDock(_openBtn, Dock.Left);
        DockPanel.SetDock(_aboutBtn, Dock.Right);
        header.Children.Add(logo);
        header.Children.Add(_openBtn);
        header.Children.Add(_aboutBtn);
        header.Children.Add(names);
        Place(grid, header, 0);
        ShowFile();

        // видео
        var screen = new Grid();
        screen.Children.Add(_media);
        screen.Children.Add(_still);
        screen.Children.Add(_placeholder);
        var video = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = screen,
            MinHeight = 200
        };
        video.MouseLeftButtonDown += (_, e) => { if (_info != null) TogglePlay(); e.Handled = true; };
        Place(grid, video, 1);

        Place(grid, _timeline, 2);

        // воспроизведение и фрагмент одной строкой под таймлайном
        CommitOnEnter(_startBox, v => _timeline.SetStart(v));
        CommitOnEnter(_endBox, v => _timeline.SetEnd(v));
        _startBox.ToolTip = Ui.Tip("Минуты и секунды через двоеточие, например 1:25, или часы, минуты и секунды: 1:02:05");
        _endBox.ToolTip = _startBox.ToolTip;
        _time.Margin = new Thickness(6, 0, 0, 0);

        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _length.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(_length, Dock.Right);
        controls.Children.Add(_length);
        controls.Children.Add(Ui.Row(
            _playBtn, _backBtn, _fwdBtn, _time,
            Spacer(28),
            Ui.Caption("Начало"), _startBox, _startHere,
            Spacer(16),
            Ui.Caption("Конец"), _endBox, _endHere));
        Place(grid, controls, 3);

        Place(grid, BuildExportCard(), 4);

        var status = BuildStatusBar();
        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        dock.Children.Add(grid);
        _root = dock;
        return dock;
    }

    static void Place(Grid g, UIElement e, int row)
    {
        Grid.SetRow(e, row);
        g.Children.Add(e);
    }

    static FrameworkElement Spacer(double w) => new() { Width = w };

    void CommitOnEnter(TextBox box, Action<double> set)
    {
        void Commit()
        {
            if (_info != null && TimeText.TryParse(box.Text, out double v)) set(v);
            ShowRange();
        }

        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); box.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowRange(); box.SelectAll(); e.Handled = true; }
        };
    }

    // ---------- файл ----------

    void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Видео|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.mts;*.m2ts;*.wmv;*.flv;*.3gp|Все файлы|*.*",
            InitialDirectory = Directory.Exists(_cfg.OpenDir) ? _cfg.OpenDir : ""
        };
        if (dlg.ShowDialog(this) == true) Open(dlg.FileName);
    }

    /// <summary>
    /// At startup only a newer version is reported. A failed request is not shown: the user
    /// did not start this check.
    /// </summary>
    async void CheckUpdates()
    {
        var result = await UpdateCheck.Check();
        _update = result;
        if (result.Newer != null && _exportCts == null)
            Say($"Доступна версия {result.Newer}, ссылка на неё в «О программе»");
    }

    void ShowFile()
    {
        _fileName.Text = _info == null ? "VideoTrim" : Path.GetFileName(_info.Path);
        _fileInfo.Text = _info == null ? "Файл не открыт" : Describe(_info);
    }

    /// <summary>
    /// Offers the download instead of just naming the missing files: without ffmpeg the
    /// program cannot open anything at all.
    /// </summary>
    async void OfferFfmpeg(string? pendingPath)
    {
        if (_installing) return;

        var answer = MessageBox.Show(this,
            "ffmpeg.exe и ffprobe.exe не найдены ни рядом с программой, ни в PATH, а без них "
            + "видео не открывается.\n\nСкачать сборку ffmpeg с gyan.dev (около 115 МБ) и положить "
            + "её рядом с программой?",
            "VideoTrim", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            Say("Без ffmpeg видео не открывается: ffmpeg.exe и ffprobe.exe нужно положить рядом с программой или добавить в PATH");
            return;
        }

        _installing = true;
        UpdateEnabled();
        Say("Загрузка ffmpeg…");
        try
        {
            var progress = new Progress<double>(p =>
                Say(p >= 0 ? $"Загрузка ffmpeg: {p * 100:0} %" : "Загрузка ffmpeg…"));
            var dir = await FfmpegSetup.Install(progress, CancellationToken.None);

            _ffmpeg = Media.Find("ffmpeg");
            _ffprobe = Media.Find("ffprobe");
            if (_ffmpeg == null || _ffprobe == null) throw new FileNotFoundException("exe не найдены после распаковки");

            _encoders = Media.DetectEncoders(_ffmpeg);
            Say("ffmpeg установлен в " + dir);
        }
        catch (Exception ex)
        {
            Say($"Не удалось скачать ffmpeg: {ex.Message}. Сборку можно взять вручную на {FfmpegSetup.Page}");
        }
        finally
        {
            _installing = false;
            UpdateEnabled();
        }

        if (pendingPath != null && _ffmpeg != null) Open(pendingPath);
    }

    async void Open(string path)
    {
        if (_exportCts != null || _installing) return;
        if (_ffprobe == null || _ffmpeg == null)
        {
            OfferFfmpeg(path);
            return;
        }

        Say("Чтение " + Path.GetFileName(path));
        VideoInfo info;
        try
        {
            info = await Media.Probe(_ffprobe, path);
            if (info.Duration <= 0) throw new InvalidOperationException("длительность неизвестна");
        }
        catch (Exception ex)
        {
            Say("Не удалось прочитать " + Path.GetFileName(path) + ": " + ex.Message);
            return;
        }

        _info = info;
        _cfg.OpenDir = Path.GetDirectoryName(path);
        _lastOutput = null;

        SetPlaying(false);
        _mediaFailed = _stillMode = false;
        _still.Source = null;
        _still.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Collapsed;
        _media.Source = new Uri(path);
        _media.Pause();   // открывает файл и показывает первый кадр

        _timeline.Reset(info.Duration, info.Height > 0 ? (double)info.Width / info.Height : 0);

        ShowFile();
        Title = Path.GetFileName(path) + " — VideoTrim";

        ShowRange();
        ShowTime();
        LoadThumbs(info);
        await FillCodecs(info);
        UpdateEnabled();
        if (_info == info && _status.Text.StartsWith("Чтение")) Say("");
    }

    static string Describe(VideoInfo v)
    {
        var parts = new List<string>
        {
            $"{v.Width}×{v.Height}",
            v.Fps > 0 ? v.Fps.ToString("0.###", Ru) + " кадров/с" : "частота кадров неизвестна",
            v.CodecName + (v.VideoBitrate > 0 ? ", " + Kbps(v.VideoBitrate) + (v.BitrateEstimated ? " (оценка)" : "") : "")
        };
        if (v.AudioCodec != null)
            parts.Add("звук " + Media.CodecName(v.AudioCodec) + (v.AudioBitrate > 0 ? ", " + Kbps(v.AudioBitrate) : ""));
        parts.Add(TimeText.Format(v.Duration, v.Duration >= 3600, true));
        return string.Join("  ·  ", parts);
    }

    static string Kbps(long bps) => bps >= 10_000_000
        ? (bps / 1e6).ToString("0.#", Ru) + " Мбит/с"
        : (bps / 1000).ToString("0", Ru) + " кбит/с";

    async void LoadThumbs(VideoInfo info)
    {
        _thumbsCts?.Cancel();
        var cts = _thumbsCts = new CancellationTokenSource();
        var ffmpeg = _ffmpeg!;

        double aspect = info.Height > 0 ? (double)info.Width / info.Height : 16.0 / 9;
        double width = Math.Max(_timeline.ActualWidth, 800);
        int n = Math.Clamp((int)Math.Ceiling(width / (Timeline.StripH * aspect)), 4, 40);
        int px = (int)Math.Round(Timeline.StripH * VisualTreeHelper.GetDpi(this).DpiScaleY / 2) * 2;

        using var gate = new SemaphoreSlim(4);
        var jobs = Enumerable.Range(0, n).Select(async i =>
        {
            await gate.WaitAsync(cts.Token);
            try
            {
                double t = (i + 0.5) * info.Duration / n;
                var img = await Media.Grab(ffmpeg, info.Path, t, px, keyOnly: true, cts.Token);
                if (img != null && !cts.IsCancellationRequested) _timeline.AddThumb(t, img);
            }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(jobs); }
        catch (OperationCanceledException) { }
    }

    // ---------- показ ----------

    /// <summary>
    /// The Windows player opens what Media Foundation can decode; for the rest (VP9 in mkv
    /// without the extension, ProRes, many MPEG-2 files) frames come from ffmpeg instead.
    /// Sound still plays if the player managed to open the audio track.
    /// </summary>
    void EnterStillMode(bool failed)
    {
        if (_info == null) return;
        _stillMode = true;
        _mediaFailed = failed;
        if (failed)
        {
            SetPlaying(false);
            Say("Проигрыватель Windows этот формат не открывает: кадры показываются через ffmpeg, без воспроизведения");
        }
        _media.Visibility = Visibility.Collapsed;
        _still.Visibility = Visibility.Visible;
        RequestStill(_timeline.Position);
        UpdateEnabled();
    }

    void RequestStill(double t)
    {
        _stillWanted = t;
        if (!_stillBusy) _ = StillLoop();
    }

    async Task StillLoop()
    {
        _stillBusy = true;
        try
        {
            while (_stillWanted is double t && _info is VideoInfo info)
            {
                _stillWanted = null;
                var img = await Media.Grab(_ffmpeg!, info.Path, t, Math.Min(info.Height, 1080), keyOnly: false);
                if (img != null && _info == info) _still.Source = img;
            }
        }
        catch (Exception) { }
        finally { _stillBusy = false; }
    }

    void OnTick()
    {
        if (_info == null || !_playing) return;
        var t = _media.Position.TotalSeconds;
        _timeline.SetPosition(t);
        ShowTime();
        if (_stillMode && !_stillBusy) RequestStill(t);
    }

    void Seek(double t)
    {
        if (_info == null) return;
        t = Math.Clamp(t, 0, _info.Duration);
        if (!_mediaFailed) _media.Position = TimeSpan.FromSeconds(t);
        _timeline.SetPosition(t);
        ShowTime();
        if (_stillMode) RequestStill(t);
    }

    void TogglePlay()
    {
        if (_info == null || _mediaFailed) return;

        // с конца файла воспроизведение начинается с начала фрагмента
        if (!_playing && _timeline.Position >= _info.Duration - 0.05) Seek(_timeline.Start);
        SetPlaying(!_playing);
    }

    void SetPlaying(bool on)
    {
        _playing = on;
        if (on) _media.Play(); else _media.Pause();
        UpdateMute();
        ((TextBlock)_playBtn.Content).Text = on ? "" : "";
    }

    /// <summary>
    /// With scrubbing on, every seek plays a few milliseconds of sound around the new
    /// position, and dragging the playhead turns that into clicks. Sound is kept only while
    /// playing and not dragging.
    /// </summary>
    void UpdateMute() => _media.IsMuted = !_playing || _scrubbing;

    void ShowTime()
    {
        _time.Text = _info == null
            ? "0:00 / 0:00"
            : TimeText.Format(_timeline.Position, Hours) + " / " + TimeText.Format(_info.Duration, Hours);
    }

    void ShowRange()
    {
        if (_info == null)
        {
            _startBox.Text = _endBox.Text = _length.Text = "";
            return;
        }
        _startBox.Text = TimeText.Format(_timeline.Start, Hours, true);
        _endBox.Text = TimeText.Format(_timeline.End, Hours, true);
        _length.Text = "Длина " + TimeText.Format(_timeline.End - _timeline.Start, Hours, true);
        UpdateEstimate();
    }

    // начало округляется вниз, конец вверх: текущий кадр остаётся внутри фрагмента
    void StartHere() => _timeline.SetStart(Math.Floor(_timeline.Position + 1e-6));

    void EndHere()
    {
        if (_info == null) return;
        double e = Math.Ceiling(_timeline.Position - 1e-6);
        _timeline.SetEnd(e > _info.Duration - 0.5 ? _info.Duration : e);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_info == null || Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Seek(_timeline.Position - step); break;
            case Key.Right: Seek(_timeline.Position + step); break;
            case Key.I: StartHere(); break;
            case Key.O: EndHere(); break;
            case Key.Home: Seek(_timeline.Start); break;
            case Key.End: Seek(_timeline.End); break;
            default: return;
        }
        e.Handled = true;
    }

    void UpdateEnabled()
    {
        bool file = _info != null, busy = _exportCts != null;

        _openBtn.IsEnabled = !busy && !_installing;
        _playBtn.IsEnabled = file && !_mediaFailed;
        _backBtn.IsEnabled = _fwdBtn.IsEnabled = _startHere.IsEnabled = _endHere.IsEnabled = file;
        _startBox.IsEnabled = _endBox.IsEnabled = file;
        _timeline.IsEnabled = file;

        UpdateExportEnabled(file, busy);
    }

    void Say(string text) => _status.Text = text;

    // ---------- окно ----------

    void RestoreGeometry()
    {
        if (_cfg.Width is double w && _cfg.Height is double h && _cfg.Left is double l && _cfg.Top is double t
            && l >= SystemParameters.VirtualScreenLeft && t >= SystemParameters.VirtualScreenTop
            && l + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && t + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
            Width = w;
            Height = h;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_cfg.Maximized) WindowState = WindowState.Maximized;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exportCts != null)
        {
            // незаконченный файл удаляется после выхода ffmpeg, окно закрывается следом
            e.Cancel = true;
            _closeAfterExport = true;
            _exportCts.Cancel();
            return;
        }

        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _cfg.Left = r.Left;
        _cfg.Top = r.Top;
        _cfg.Width = r.Width;
        _cfg.Height = r.Height;
        _cfg.Maximized = WindowState == WindowState.Maximized;
        _cfg.Save();

        _thumbsCts?.Cancel();
        _media.Close();
    }
}
