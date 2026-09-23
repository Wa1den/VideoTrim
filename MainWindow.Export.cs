using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VideoTrim;

/// <summary>The export settings card, the status bar with the save button, and the export itself.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// One height for combo boxes, text boxes and the buttons beside them. Fluent draws a
    /// combo box 36 tall and a text box 32; below 36 the combo box cuts off the bottom of
    /// its text.
    /// </summary>
    const double FieldHeight = 36;

    static readonly int[] ShortSides = [2160, 1440, 1080, 720, 540, 480, 360, 240];
    static readonly int[] AudioRates = [96, 128, 160, 192, 256, 320];

    readonly RadioButton _reencode = new() { Content = Loc.T("mode.reencode"), GroupName = "mode", MinWidth = 0 };
    readonly RadioButton _copy = new() { Content = Loc.T("mode.copy"), GroupName = "mode", MinWidth = 0 };

    readonly ComboBox _codec = Combo(280);
    readonly ComboBox _rateMode = Combo(150);
    readonly TextBlock _rateCaption = new() { FontSize = Ui.TextSize, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _rateHelp = Ui.HelpIcon("");
    readonly TextBox _bitrateBox = new() { Width = 110, Height = FieldHeight, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _sameBitrate = Check(Loc.T("enc.bitrate.same"));
    readonly ComboBox _quality = Combo(240);
    FrameworkElement _bitrateEditor = null!, _qualityEditor = null!;

    readonly CheckBox _keepAspect = Check(Loc.T("video.aspect"));
    readonly ComboBox _resolution = Combo(280);
    readonly ComboBox _fps = Combo(170);

    /// <summary>15 выбрано самой программой при переходе на GIF, а не пользователем.</summary>
    bool _fpsAuto;

    readonly CheckBox _keepAudio = Check(Loc.T("audio.keep"));
    readonly CheckBox _sameAudio = Check(Loc.T("audio.same"));
    readonly ComboBox _audioCodec = Combo(200);
    readonly ComboBox _audioRate = Combo(110);

    readonly ComboBox _format = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    readonly TextBlock _estimate = new() { FontSize = Ui.TextSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
    readonly ProgressBar _progress = new() { Width = 160, Height = 4, Maximum = 100, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    Button _exportBtn = null!, _cancelBtn = null!, _showBtn = null!;

    List<Encoder> _audioEncoders = [];
    Encoder? _sameEncoder;
    CancellationTokenSource? _exportCts;
    string? _lastOutput;
    bool _closeAfterExport;

    sealed record FrameSize(int Width, int Height, bool Source)
    {
        public override string ToString() =>
            Source ? Loc.T("video.size.source", Width, Height) : $"{Width}×{Height}";
    }

    /// <param name="Value">null — исходная частота.</param>
    sealed record FrameRate(double? Value, string Label)
    {
        public override string ToString() => Label;
    }

    static readonly int[] StandardRates = [60, 50, 30, 25, 24, 20, 15, 12, 10];

    sealed record Format(OutputKind Kind, string Ext, string Label)
    {
        public override string ToString() => Label;
    }

    static ComboBox Combo(double width) => new()
    {
        Width = width,
        Height = FieldHeight,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0)
    };

    static CheckBox Check(string text) => new()
    {
        Content = text,
        MinWidth = 0,
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center
    };

    FrameworkElement BuildExportCard()
    {
        // кодирование
        var modes = Ui.Row(
            Ui.WithHelpIcon(_reencode, Loc.T("mode.reencode.note")),
            Spacer(24),
            Ui.WithHelpIcon(_copy, Loc.T("mode.copy.note")));
        modes.Margin = new Thickness(0, 0, 0, 2);

        _rateMode.Items.Add(Loc.T("enc.mode.bitrate"));
        _rateMode.Items.Add(Loc.T("enc.mode.quality"));

        var bitrate = new StackPanel { Orientation = Orientation.Horizontal, Children = { _bitrateBox, _sameBitrate } };
        _bitrateEditor = bitrate;
        _qualityEditor = _quality;
        _quality.Margin = new Thickness(0);
        var slot = new Grid { Margin = new Thickness(0, 2, 0, 0), Children = { bitrate, _quality } };

        var rateCaption = new StackPanel { Orientation = Orientation.Horizontal, Children = { _rateCaption, _rateHelp } };
        _rateCaption.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        var rate = new StackPanel { Margin = new Thickness(0, 4, 16, 4), Children = { rateCaption, slot } };

        var video = new StackPanel();
        video.Children.Add(modes);
        video.Children.Add(Ui.Row(
            Ui.Labeled(Loc.T("enc.codec"), _codec, Loc.T("enc.codec.note")),
            Ui.Labeled(Loc.T("enc.mode"), _rateMode, Loc.T("enc.mode.note")),
            rate));

        // разрешение
        var resolution = Ui.Row(
            Ui.Labeled(Loc.T("video.size"), _resolution, Loc.T("video.size.note")),
            Beside(Ui.WithHelpIcon(_keepAspect, Loc.T("video.aspect.note"))),
            Ui.Labeled(Loc.T("video.fps"), _fps, Loc.T("video.fps.note")));

        // аудио: галки над полями, «Сохранить звук» над кодеком, «Исходный» над битрейтом
        var audio = new Grid();
        audio.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        audio.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        audio.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        audio.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var keep = Ui.WithHelpIcon(_keepAudio, Loc.T("audio.keep.note"));
        var same = Ui.WithHelpIcon(_sameAudio, Loc.T("audio.same.note"));
        keep.Margin = same.Margin = new Thickness(0, 6, 16, 2);
        var audioCodec = Ui.Labeled(Loc.T("audio.codec"), _audioCodec);
        var audioRate = Ui.Labeled(Loc.T("audio.bitrate"), _audioRate, Loc.T("audio.bitrate.note"));

        foreach (var (e, row, col) in new (UIElement, int, int)[] { (keep, 0, 0), (same, 0, 1), (audioCodec, 1, 0), (audioRate, 1, 1) })
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            audio.Children.Add(e);
        }

        foreach (var r in AudioRates) _audioRate.Items.Add(r);
        _audioRate.SelectedItem = 192;

        return Ui.Card(Ui.SelectorBar(
            (Loc.T("tab.encoding"), video),
            (Loc.T("tab.video"), resolution),
            (Loc.T("tab.audio"), audio)));
    }

    /// <summary>
    /// A checkbox standing in a row of captioned fields, level with the fields: an empty
    /// caption line above it takes the place of theirs.
    /// </summary>
    static FrameworkElement Beside(UIElement e)
    {
        var holder = new Border { Height = FieldHeight, Margin = new Thickness(0, 2, 0, 0), Child = e };
        if (e is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Margin = new Thickness(0, 4, 16, 4),
            Children = { new TextBlock { Text = " ", FontSize = Ui.TextSize }, holder }
        };
    }

    /// <summary>
    /// What to save and the save button on the left, the message on the right. A long path
    /// in the message is trimmed rather than pushing the buttons.
    /// </summary>
    FrameworkElement BuildStatusBar()
    {
        _exportBtn = Ui.Btn(Loc.T("export.save"), Export, accent: true);
        _cancelBtn = Ui.Btn(Loc.T("export.cancel"), () => _exportCts?.Cancel());
        _showBtn = Ui.Btn(Loc.T("export.show"), ShowOutput);

        // кнопки той же высоты, что список рядом с ними
        _format.Height = FieldHeight;
        _format.VerticalContentAlignment = VerticalAlignment.Center;
        foreach (var b in new[] { _exportBtn, _cancelBtn, _showBtn }) b.Height = FieldHeight;

        _format.ToolTip = Ui.Tip(Loc.T("format.note", Media.GifMaxWidth));
        _estimate.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        _estimate.ToolTip = Ui.Tip(Loc.T("export.estimate.note"));

        _status.TextAlignment = TextAlignment.Right;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(12, 0, 0, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _format, _exportBtn, _estimate, _progress, _cancelBtn, _showBtn }
        };
        var bar = new DockPanel { Margin = new Thickness(12, 0, 12, 10) };
        DockPanel.SetDock(buttons, Dock.Left);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        return bar;
    }

    void WireExport()
    {
        // у списков подпись стоит отдельным текстом; экранный диктор без имени читает «список»
        foreach (var (c, name) in new (Control, string)[]
                 {
                     (_codec, Loc.T("enc.codec")), (_rateMode, Loc.T("enc.mode")), (_quality, Loc.T("enc.quality")),
                     (_bitrateBox, Loc.T("enc.bitrate")),
                     (_resolution, Loc.T("video.size")), (_fps, Loc.T("video.fps")),
                     (_audioCodec, Loc.T("audio.codec")), (_audioRate, Loc.T("audio.bitrate")),
                     (_format, Loc.P("Формат", "Format"))
                 })
            System.Windows.Automation.AutomationProperties.SetName(c, name);

        _rebuildingUi = true;
        _rateMode.SelectedIndex = _cfg.ByQuality ? 1 : 0;
        _rebuildingUi = false;

        _reencode.Checked += (_, _) => ModeChanged();
        _copy.Checked += (_, _) => ModeChanged();
        _sameBitrate.Checked += (_, _) => SameBitrateChanged();
        _sameBitrate.Unchecked += (_, _) => SameBitrateChanged();
        _codec.SelectionChanged += (_, _) => CodecChanged();
        _rateMode.SelectionChanged += (_, _) =>
        {
            if (!_rebuildingUi) _cfg.ByQuality = _rateMode.SelectedIndex == 1;
            UpdateEnabled();
        };
        _quality.SelectionChanged += (_, _) =>
        {
            if (!_rebuildingUi && _quality.SelectedIndex >= 0) _cfg.QualityLevel = _quality.SelectedIndex;
        };
        _bitrateBox.TextChanged += (_, _) => UpdateEstimate();
        _keepAspect.Checked += (_, _) => FillResolutions();
        _keepAspect.Unchecked += (_, _) => FillResolutions();
        _resolution.SelectionChanged += (_, _) => { ApplySameBitrate(); UpdateEnabled(); };
        foreach (var c in new[] { _keepAudio, _sameAudio })
        {
            c.Checked += (_, _) => UpdateEnabled();
            c.Unchecked += (_, _) => UpdateEnabled();
        }
        _audioCodec.SelectionChanged += (_, _) => UpdateEnabled();
        _audioRate.SelectionChanged += (_, _) => UpdateEstimate();
        _format.SelectionChanged += (_, _) => FormatChanged();
        _fps.SelectionChanged += (_, _) => { if (!_rebuildingUi) _fpsAuto = false; };
    }

    async Task FillCodecs(VideoInfo info)
    {
        var (list, audio) = await _encoders;
        if (_info != info) return;

        _rebuildingUi = true;
        _codec.Items.Clear();
        _sameEncoder = list.FirstOrDefault(e => e.Codec == info.Codec && !e.Hardware)
                       ?? list.FirstOrDefault(e => e.Codec == info.Codec);
        foreach (var e in list) _codec.Items.Add(e);

        var remembered = list.FirstOrDefault(e => e.Name == _cfg.Encoder && e.Codec == info.Codec);
        _codec.SelectedItem = remembered ?? _sameEncoder ?? list.FirstOrDefault();
        _sameBitrate.IsChecked = info.VideoBitrate > 0;

        _audioEncoders = audio;
        _audioCodec.Items.Clear();
        foreach (var e in audio) _audioCodec.Items.Add(e);
        _audioCodec.SelectedItem = audio.FirstOrDefault(e => e.Codec == info.AudioCodec) ?? audio.FirstOrDefault();

        // исходный битрейт звука, округлённый до ближайшего из списка
        if (info.AudioBitrate > 0)
            _audioRate.SelectedItem = AudioRates.MinBy(r => Math.Abs(r - info.AudioBitrate / 1000.0));

        _keepAudio.IsChecked = info.AudioCodec != null;

        // форматы: звук предлагается, только если он есть и есть чем его записать
        _format.Items.Clear();
        var ext = Path.GetExtension(info.Path).ToLowerInvariant();
        _format.Items.Add(new Format(OutputKind.Video, ext, Loc.T("format.video", ext)));
        _format.Items.Add(new Format(OutputKind.Gif, ".gif", Loc.T("format.gif")));
        if (info.AudioCodec != null && (info.AudioCodec == "aac" || audio.Any(e => e.Codec == "aac")))
            _format.Items.Add(new Format(OutputKind.M4a, ".m4a", Loc.T("format.m4a")));
        if (info.AudioCodec != null && (info.AudioCodec == "mp3" || audio.Any(e => e.Codec == "mp3")))
            _format.Items.Add(new Format(OutputKind.Mp3, ".mp3", Loc.T("format.mp3")));
        _format.SelectedIndex = 0;
        _rebuildingUi = false;

        FillQuality();
        FillResolutions();
        FillFrameRates();
        ApplySameBitrate();

        if (list.Count == 0)
            Say(Loc.P("Кодировщиков в этой сборке ffmpeg не найдено, доступно только сохранение без перекодирования",
                      "This ffmpeg build has no encoders, only saving without re-encoding is available"));
        else if (_sameEncoder == null)
            Say(Loc.P($"Кодировщика {info.CodecName} в этой сборке ffmpeg нет, для перекодирования выбран {list[0].Label}",
                      $"This ffmpeg build has no {info.CodecName} encoder, {list[0].Label} is chosen for re-encoding"));

        UpdateEnabled();
    }

    /// <summary>The levels with the value this encoder gets for each, shown in the list itself.</summary>
    void FillQuality()
    {
        int level = _quality.SelectedIndex >= 0 ? _quality.SelectedIndex : Math.Clamp(_cfg.QualityLevel, 0, 3);

        _rebuildingUi = true;
        _quality.Items.Clear();
        if (_codec.SelectedItem is Encoder enc && Media.QualityScale(enc.Name) is var (param, values))
            for (int i = 0; i < values.Length; i++)
                _quality.Items.Add($"{Loc.T("quality." + i)}, {param} {values[i]}");
        _quality.SelectedIndex = _quality.Items.Count > 0 ? level : -1;
        _rebuildingUi = false;
    }

    /// <summary>
    /// Sizes from the source down, by the short side, so a portrait video gets 1080×1920
    /// rather than a landscape frame squeezed onto it.
    /// </summary>
    void FillResolutions()
    {
        if (_info is not VideoInfo v || v.Width <= 0 || v.Height <= 0) return;

        var previous = _resolution.SelectedItem?.ToString();
        bool landscape = v.Width >= v.Height;
        int shortSide = Math.Min(v.Width, v.Height), longSide = Math.Max(v.Width, v.Height);
        double ratio = _keepAspect.IsChecked == true ? (double)longSide / shortSide : 16.0 / 9;

        _resolution.Items.Clear();
        _resolution.Items.Add(new FrameSize(v.Width, v.Height, true));
        foreach (int s in ShortSides.Where(s => s <= shortSide))
        {
            int l = (int)Math.Round(s * ratio / 2) * 2;
            var size = landscape ? new FrameSize(l, s, false) : new FrameSize(s, l, false);
            if (size.Width == v.Width && size.Height == v.Height) continue;
            _resolution.Items.Add(size);
        }

        _resolution.SelectedItem = _resolution.Items.Cast<FrameSize>().FirstOrDefault(s => s.ToString() == previous)
                                   ?? _resolution.Items[0];
    }

    /// <summary>
    /// The source rate and the standard ones below it. Nothing above the source: the fps
    /// filter would only repeat frames, and the file would grow with nothing gained.
    /// </summary>
    void FillFrameRates()
    {
        if (_info is not VideoInfo v) return;

        _rebuildingUi = true;
        _fps.Items.Clear();
        string source = v.Fps > 0 ? v.Fps.ToString("0.###", Num) : "?";
        _fps.Items.Add(new FrameRate(null, Loc.T("video.fps.source", source)));
        foreach (int r in StandardRates.Where(r => v.Fps <= 0 || r < v.Fps - 0.5))
            _fps.Items.Add(new FrameRate(r, r.ToString(CultureInfo.InvariantCulture)));
        _fps.SelectedIndex = 0;
        _fpsAuto = false;
        _rebuildingUi = false;
    }

    /// <summary>
    /// GIF stores every frame as a full picture with its own palette, so 30 frames per
    /// second take twice the size of 15. Switching to GIF picks 15 unless the rate was
    /// chosen by hand, and switching back returns what was there.
    /// </summary>
    void FormatChanged()
    {
        if (!_rebuildingUi)
        {
            var gifRate = _fps.Items.Cast<FrameRate>().FirstOrDefault(r => r.Value == Media.GifFps);
            _rebuildingUi = true;
            if (Kind == OutputKind.Gif && _fps.SelectedItem is FrameRate { Value: null } && gifRate != null)
            {
                _fps.SelectedItem = gifRate;
                _fpsAuto = true;
            }
            else if (Kind != OutputKind.Gif && _fpsAuto)
            {
                _fps.SelectedIndex = 0;
                _fpsAuto = false;
            }
            _rebuildingUi = false;
        }
        UpdateEnabled();
    }

    /// <summary>The source bitrate, brought down with the frame when the frame is made smaller.</summary>
    long SourceKbps()
    {
        if (_info is not VideoInfo v) return 0;
        long kbps = v.VideoBitrate / 1000;
        return _resolution.SelectedItem is FrameSize { Source: false } s
            ? Media.ScaleBitrate(kbps, (long)v.Width * v.Height, (long)s.Width * s.Height)
            : kbps;
    }

    void ApplySameBitrate()
    {
        if (_sameBitrate.IsChecked == true && _info != null)
            _bitrateBox.Text = SourceKbps().ToString(CultureInfo.InvariantCulture);
    }

    void ModeChanged()
    {
        if (!_rebuildingUi) _cfg.CopyStreams = _copy.IsChecked == true;
        UpdateEnabled();
    }

    void CodecChanged()
    {
        if (!_rebuildingUi && _codec.SelectedItem is Encoder enc) _cfg.Encoder = enc.Name;
        FillQuality();
        UpdateEnabled();
    }

    void SameBitrateChanged()
    {
        ApplySameBitrate();
        UpdateEnabled();
    }

    OutputKind Kind => (_format.SelectedItem as Format)?.Kind ?? OutputKind.Video;

    /// <summary>
    /// Only the editors are disabled, never the rows: a disabled parent takes the tooltip of
    /// the help glyph with it, and the tooltip is where the reason a setting is off is explained.
    /// </summary>
    void UpdateExportEnabled(bool file, bool busy)
    {
        var kind = Kind;
        bool video = kind == OutputKind.Video, audioOnly = kind is OutputKind.M4a or OutputKind.Mp3;

        bool canEncode = _codec.Items.Count > 0;
        if (!canEncode && _reencode.IsChecked == true && file)
        {
            _rebuildingUi = true;
            _copy.IsChecked = true;
            _rebuildingUi = false;
        }
        bool encode = video && _reencode.IsChecked == true && canEncode && !busy;
        bool byQuality = _rateMode.SelectedIndex == 1;
        var enc = _codec.SelectedItem as Encoder;
        bool hasQuality = enc != null && Media.QualityScale(enc.Name) != null;
        bool rate = encode && enc is { Bitrate: true };

        _reencode.IsEnabled = video && canEncode && !busy;
        _copy.IsEnabled = video && !busy;

        _codec.IsEnabled = encode;
        _rateMode.IsEnabled = rate && hasQuality;
        _sameBitrate.IsEnabled = rate && _info?.VideoBitrate > 0;
        _bitrateBox.IsEnabled = rate && _sameBitrate.IsChecked != true;
        _quality.IsEnabled = rate && hasQuality;

        bool showQuality = byQuality && hasQuality;
        _bitrateEditor.Visibility = showQuality ? Visibility.Hidden : Visibility.Visible;
        _qualityEditor.Visibility = showQuality ? Visibility.Visible : Visibility.Hidden;
        _rateCaption.Text = Loc.T(showQuality ? "enc.quality" : "enc.bitrate");
        ((TextBlock)_rateHelp.ToolTip).Text = Loc.T(showQuality ? "enc.quality.note" : "enc.bitrate.note");

        bool frame = (encode || kind == OutputKind.Gif) && !busy;
        _keepAspect.IsEnabled = _resolution.IsEnabled = _fps.IsEnabled = frame;

        bool hasAudio = _info?.AudioCodec != null;
        bool audioVideo = encode && hasAudio;
        bool keepAudio = audioOnly || _keepAudio.IsChecked == true;
        bool audio = (audioVideo || audioOnly) && keepAudio && !busy;

        // для звуковых форматов кодек задан расширением, копировать можно только тот же
        bool canCopyAudio = !audioOnly || _info?.AudioCodec == (kind == OutputKind.M4a ? "aac" : "mp3");
        bool audioEncode = audio && (_sameAudio.IsChecked != true || !canCopyAudio);

        _keepAudio.IsEnabled = audioVideo && !busy;
        _sameAudio.IsEnabled = audio && canCopyAudio;
        _audioCodec.IsEnabled = audioEncode && !audioOnly && _audioEncoders.Count > 0;
        _audioRate.IsEnabled = audioEncode && (audioOnly || _audioCodec.SelectedItem is Encoder { Bitrate: true });

        _format.IsEnabled = file && !busy;
        _exportBtn.IsEnabled = file && !busy;
        _estimate.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _cancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _showBtn.Visibility = !busy && _lastOutput != null ? Visibility.Visible : Visibility.Collapsed;

        UpdateEstimate();
    }

    /// <summary>
    /// Expected size from the bitrates and the clip length. Shown only where the bitrate is
    /// known: in quality mode and for GIF the size follows the picture.
    /// </summary>
    void UpdateEstimate()
    {
        _estimate.Text = "";
        if (_info is not VideoInfo v || _format.Items.Count == 0 || _info == null) return;

        double seconds = _timeline.End - _timeline.Start;
        double sourceAudio = v.AudioBitrate / 1000.0;
        double chosenAudio = _audioRate.SelectedItem is int r ? r : 192;
        double kbps;

        switch (Kind)
        {
            case OutputKind.Gif:
                return;

            case OutputKind.M4a or OutputKind.Mp3:
                kbps = _sameAudio.IsEnabled && _sameAudio.IsChecked == true ? sourceAudio : chosenAudio;
                break;

            default:
                if (_copy.IsChecked == true || _codec.Items.Count == 0)
                {
                    kbps = v.VideoBitrate / 1000.0 + sourceAudio;
                    break;
                }
                if (_rateMode.SelectedIndex == 1 && _quality.IsEnabled) return;
                if (!double.TryParse(_bitrateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out kbps)) return;

                if (v.AudioCodec != null && _keepAudio.IsChecked == true)
                    kbps += _sameAudio.IsChecked == true ? sourceAudio
                          : _audioCodec.SelectedItem is Encoder { Bitrate: false } ? 900 : chosenAudio;
                break;
        }

        if (kbps <= 0) return;
        double bytes = kbps * 1000 / 8 * seconds * 1.01;   // процент на контейнер
        _estimate.Text = "≈ " + (bytes >= 1e9 ? (bytes / 1e9).ToString("0.0", Num) + Loc.P(" ГБ", " GB")
                               : bytes >= 1e6 ? (bytes / 1e6).ToString(bytes >= 1e8 ? "0" : "0.0", Num) + Loc.P(" МБ", " MB")
                               : (bytes / 1e3).ToString("0", Num) + Loc.P(" КБ", " KB"));
    }

    ExportOptions? ReadOptions(VideoInfo info)
    {
        var kind = Kind;
        var size = _resolution.SelectedItem as FrameSize;
        (int, int)? scale = size is { Source: false } ? (size.Width, size.Height) : null;
        int? audioKbps = _audioRate.SelectedItem as int?;
        double? fps = (_fps.SelectedItem as FrameRate)?.Value;

        switch (kind)
        {
            case OutputKind.Gif:
                // для GIF «исходная» значит частоту исходника, а не 15 по умолчанию
                return new ExportOptions(kind, null, null, null, scale, fps ?? (info.Fps > 0 ? info.Fps : null), false, null, null);

            case OutputKind.M4a or OutputKind.Mp3:
            {
                bool copy = _sameAudio.IsEnabled && _sameAudio.IsChecked == true;
                var codec = kind == OutputKind.M4a ? "aac" : "mp3";
                var enc = copy ? null : _audioEncoders.FirstOrDefault(e => e.Codec == codec);
                if (!copy && enc == null)
                {
                    Say(Loc.P("В этой сборке ffmpeg нет кодировщика для ", "This ffmpeg build has no encoder for ") + codec);
                    return null;
                }
                return new ExportOptions(kind, null, null, null, null, null, true, enc, audioKbps);
            }
        }

        if (_reencode.IsChecked != true)
            return new ExportOptions(kind, null, null, null, null, null, true, null, null);

        if (_codec.SelectedItem is not Encoder encoder) return null;

        long? kbps = null;
        int? quality = null;
        if (_quality.IsEnabled && _rateMode.SelectedIndex == 1 && Media.QualityScale(encoder.Name) is var (_, values))
        {
            quality = values[Math.Clamp(_quality.SelectedIndex, 0, values.Length - 1)];
        }
        else
        {
            var text = _bitrateBox.Text.Trim();
            if (text.Length > 0)
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long k) || k <= 0)
                {
                    Say(Loc.P("Битрейт — целое число килобит в секунду, например 8000",
                              "The bitrate is a whole number of kilobits per second, such as 8000"));
                    return null;
                }
                kbps = k;
            }
        }

        bool keepAudio = info.AudioCodec != null && _keepAudio.IsChecked == true;
        var audioEncoder = keepAudio && _sameAudio.IsChecked != true ? _audioCodec.SelectedItem as Encoder : null;

        return new ExportOptions(kind, encoder, kbps, quality, scale, fps, keepAudio, audioEncoder, audioKbps);
    }

    async void Export()
    {
        if (_info is not VideoInfo info || _ffmpeg == null || _exportCts != null) return;
        if (ReadOptions(info) is not ExportOptions options) return;

        double start = _timeline.Start, end = _timeline.End;
        var format = _format.SelectedItem as Format;
        var ext = format?.Ext ?? Path.GetExtension(info.Path);
        string what = options.Kind switch
        {
            OutputKind.Gif => "GIF",
            OutputKind.M4a or OutputKind.Mp3 => Loc.P("Звук", "Audio"),
            _ => Loc.P("Видео", "Video")
        };

        string Stamp(double t) => TimeText.Format(t, Hours, true).Replace(':', '-').Replace(',', '.');
        var dlg = new SaveFileDialog
        {
            FileName = $"{Path.GetFileNameWithoutExtension(info.Path)}_{Stamp(start)}_{Stamp(end)}{ext}",
            InitialDirectory = Directory.Exists(_cfg.SaveDir) ? _cfg.SaveDir : Path.GetDirectoryName(info.Path),
            Filter = $"{what} (*{ext})|*{ext}|{Loc.P("Все файлы", "All files")}|*.*",
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != true) return;

        // имя без папки ffmpeg записал бы в рабочую папку программы
        var output = Path.IsPathRooted(dlg.FileName)
            ? dlg.FileName
            : Path.Combine(dlg.InitialDirectory ?? Path.GetDirectoryName(info.Path)!, dlg.FileName);
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(info.Path), StringComparison.OrdinalIgnoreCase))
        {
            Say(Loc.P("Фрагмент нельзя записать поверх исходного файла", "The clip cannot overwrite the source file"));
            return;
        }
        _cfg.SaveDir = Path.GetDirectoryName(output);

        var args = Media.ExportArgs(info, start, end, options, output);
        double len = end - start;

        _exportCts = new CancellationTokenSource();
        _lastOutput = null;
        _progress.Value = 0;
        UpdateEnabled();
        Say(options.Kind switch
        {
            OutputKind.Gif => Loc.P("Сохранение GIF…", "Saving GIF…"),
            OutputKind.M4a or OutputKind.Mp3 => Loc.P("Сохранение звука…", "Saving audio…"),
            _ => options.Video == null ? Loc.P("Копирование фрагмента…", "Copying the clip…")
                                       : Loc.P("Кодирование: ", "Encoding: ") + options.Video.Label
        });
        var clock = Stopwatch.StartNew();

        try
        {
            var (code, _, err) = await Media.Run(_ffmpeg, args, _exportCts.Token, line =>
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out long us))
                    Dispatcher.BeginInvoke(() => _progress.Value = Math.Clamp(us / 1e6 / len, 0, 1) * 100);
            });

            if (code == 0)
            {
                _lastOutput = output;
                string took = clock.Elapsed.TotalSeconds.ToString("0.#", Num);
                string size = (new FileInfo(output).Length / 1e6).ToString("0.#", Num);
                Say(Loc.P($"Сохранено за {took} с, {size} МБ: {output}", $"Saved in {took} s, {size} MB: {output}"));
            }
            else
            {
                TryDelete(output);
                Say(Loc.P("ffmpeg завершился с ошибкой, файл не сохранён", "ffmpeg stopped with an error, the file is not saved"));
                MessageBox.Show(this, Media.LastLines(err, 8), Loc.P("Ошибка ffmpeg", "ffmpeg error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            Say(Loc.P("Сохранение отменено", "Saving cancelled"));
        }
        catch (Exception ex)
        {
            TryDelete(output);
            Say(Loc.P("Не удалось запустить ffmpeg: ", "Could not start ffmpeg: ") + ex.Message);
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            UpdateEnabled();
            if (_closeAfterExport) Close();
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    void ShowOutput()
    {
        if (_lastOutput == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
