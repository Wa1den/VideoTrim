using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace VideoTrim;

public sealed record VideoInfo(
    string Path,
    double Duration,
    int Width,
    int Height,
    double Fps,
    string Codec,
    long VideoBitrate,
    bool BitrateEstimated,
    string? AudioCodec,
    long AudioBitrate)
{
    public string CodecName => Media.CodecName(Codec);
}

/// <param name="Title">Формат и имя кодировщика, одинаковые на всех языках.</param>
/// <param name="NoteKey">Ключ пояснения после запятой: чем кодирует, без потерь ли.</param>
/// <param name="Bitrate">False where the encoder ignores -b:v (ProRes picks its rate from the profile).</param>
public sealed record Encoder(string Name, string Codec, string Title, string? NoteKey, bool Hardware, bool Bitrate)
{
    public string Label => NoteKey == null ? Title : Title + ", " + Loc.T(NoteKey);
    public override string ToString() => Label;
}

public enum OutputKind { Video, Gif, M4a, Mp3 }

/// <param name="Video">null копирует потоки без перекодирования; настройки видео тогда не действуют.</param>
/// <param name="Kbps">null оставляет битрейт на усмотрение кодировщика.</param>
/// <param name="Quality">Значение CRF, CQ или QP; при нём битрейт не задаётся.</param>
/// <param name="Scale">null сохраняет исходный размер кадра.</param>
/// <param name="Fps">null сохраняет исходную частоту кадров; для GIF задаётся всегда.</param>
/// <param name="AudioEncoder">null копирует звук как есть.</param>
public sealed record ExportOptions(
    OutputKind Kind,
    Encoder? Video,
    long? Kbps,
    int? Quality,
    (int Width, int Height)? Scale,
    double? Fps,
    bool Audio,
    Encoder? AudioEncoder,
    int? AudioKbps);

public static class Media
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The program folder first, so a copy of ffmpeg next to the exe wins over PATH; then
    /// the folder the download goes to when the program folder is not writable.
    /// </summary>
    public static string? Find(string name)
    {
        var file = name + ".exe";
        var dirs = new List<string> { AppContext.BaseDirectory, FfmpegSetup.LocalDir };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var d in dirs)
        {
            try
            {
                var p = System.IO.Path.Combine(d.Trim('"'), file);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static string Sec(double s) => s.ToString("0.###", Inv);

    // ---------- процессы ----------

    static ProcessStartInfo Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// Runs to completion; <paramref name="onLine"/> gets stdout line by line on a pool thread.
    /// Cancellation kills the process and waits for it to exit, so the caller may delete
    /// the half-written output right after.
    /// </summary>
    public static async Task<(int Code, string Out, string Err)> Run(
        string exe, IEnumerable<string> args, CancellationToken ct = default, Action<string>? onLine = null)
    {
        using var p = new Process { StartInfo = Start(exe, args) };
        p.StartInfo.StandardOutputEncoding = Encoding.UTF8;

        var output = new StringBuilder();
        var error = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (onLine != null) onLine(e.Data);
            else lock (output) output.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, output.ToString(), error.ToString());
    }

    static async Task<byte[]> RunBytes(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = new Process { StartInfo = Start(exe, args) };
        p.Start();

        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken.None);
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await Task.WhenAll(copy, err);
        await p.WaitForExitAsync(CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        return p.ExitCode == 0 ? ms.ToArray() : [];
    }

    // ---------- чтение файла ----------

    public static async Task<VideoInfo> Probe(string ffprobe, string path)
    {
        var (code, json, err) = await Run(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]);
        if (code != 0)
            throw new InvalidOperationException(LastLines(err, 1).Trim() is { Length: > 0 } m ? m : "ffprobe: " + Loc.P("код ", "exit code ") + code);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;

        JsonElement? video = null;
        string? audioCodec = null;
        long audioBitrate = 0, audioTotal = 0;

        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                if (type == "video" && video == null && !IsCover(s)) video = s.Clone();
                else if (type == "audio")
                {
                    long br = Lng(s, "bit_rate") ?? TagBps(s) ?? 0;
                    audioTotal += br;
                    if (audioCodec == null) { audioCodec = Str(s, "codec_name"); audioBitrate = br; }
                }
            }

        if (video is not JsonElement v)
            throw new InvalidOperationException(Loc.P("в файле нет видеопотока", "the file has no video stream"));

        double duration = Dbl(format, "duration") ?? Dbl(v, "duration") ?? 0;

        int w = (int)(Lng(v, "width") ?? 0), h = (int)(Lng(v, "height") ?? 0);
        if (Math.Abs(Rotation(v)) % 180 == 90) (w, h) = (h, w);

        double fps = Rate(Str(v, "avg_frame_rate")) ?? Rate(Str(v, "r_frame_rate")) ?? 0;

        // mkv не пишет битрейт в поток, только в теги или в общий на файл
        long bitrate = Lng(v, "bit_rate") ?? TagBps(v) ?? 0;
        bool estimated = false;
        if (bitrate <= 0 && Lng(format, "bit_rate") is long total && total > audioTotal)
        {
            bitrate = total - audioTotal;
            estimated = true;
        }
        if (bitrate <= 0 && duration > 0)
        {
            bitrate = (long)(new FileInfo(path).Length * 8 / duration) - audioTotal;
            estimated = true;
        }

        return new VideoInfo(path, duration, w, h, fps, Str(v, "codec_name") ?? "?",
                             Math.Max(0, bitrate), estimated, audioCodec, audioBitrate);
    }

    static bool IsCover(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a)
        && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    static int Rotation(JsonElement s)
    {
        if (s.TryGetProperty("side_data_list", out var list))
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                    return (int)r.GetDouble();

        if (s.TryGetProperty("tags", out var tags) && Str(tags, "rotate") is string t
            && int.TryParse(t, NumberStyles.Integer, Inv, out int deg))
            return deg;

        return 0;
    }

    static long? TagBps(JsonElement s)
    {
        if (!s.TryGetProperty("tags", out var tags)) return null;
        foreach (var p in tags.EnumerateObject())
            if ((p.Name.Equals("BPS", StringComparison.OrdinalIgnoreCase)
                 || p.Name.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase))
                && long.TryParse(p.Value.GetString(), NumberStyles.Integer, Inv, out long v))
                return v;
        return null;
    }

    static double? Rate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return null;
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, Inv, out double n)
            && double.TryParse(parts[1], NumberStyles.Float, Inv, out double d) && d > 0 && n > 0)
            return n / d;
        return null;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()
            : null;

    static double? Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, Inv, out double v) ? v : null;

    static long? Lng(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, Inv, out long v) ? v : null;

    public static string CodecName(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "H.265",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg4" => "MPEG-4 Part 2",
        "mpeg2video" => "MPEG-2",
        "prores" => "ProRes",
        "aac" => "AAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "flac" => "FLAC",
        _ => codec.ToUpperInvariant()
    };

    // ---------- кадры ----------

    /// <summary>
    /// One frame as a bitmap. <paramref name="keyOnly"/> decodes only the keyframe before
    /// <paramref name="t"/>: good enough for the timeline strip, and the frames between the
    /// keyframe and the exact moment are not decoded.
    /// </summary>
    public static async Task<BitmapSource?> Grab(string ffmpeg, string path, double t, int height,
                                                 bool keyOnly, CancellationToken ct = default)
    {
        var args = new List<string> { "-hide_banner", "-v", "error" };
        if (keyOnly) args.AddRange(["-skip_frame", "nokey", "-noaccurate_seek"]);
        args.AddRange(["-ss", Sec(t), "-i", path, "-an", "-sn", "-dn", "-frames:v", "1"]);
        if (height > 0) args.AddRange(["-vf", $"scale=-2:{height}"]);
        args.AddRange(["-f", "image2pipe", "-c:v", "bmp", "-"]);

        var bytes = await RunBytes(ffmpeg, args, ct);
        if (bytes.Length == 0) return null;

        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception) { return null; }
    }

    // ---------- кодировщики ----------

    static readonly Encoder[] Known =
    [
        new("libx264", "h264", "H.264: x264", "hw.cpu", false, true),
        new("h264_nvenc", "h264", "H.264: NVENC", "hw.nvidia", true, true),
        new("h264_qsv", "h264", "H.264: Quick Sync", "hw.intel", true, true),
        new("h264_amf", "h264", "H.264: AMF", "hw.amd", true, true),
        new("libx265", "hevc", "H.265: x265", "hw.cpu", false, true),
        new("hevc_nvenc", "hevc", "H.265: NVENC", "hw.nvidia", true, true),
        new("hevc_qsv", "hevc", "H.265: Quick Sync", "hw.intel", true, true),
        new("hevc_amf", "hevc", "H.265: AMF", "hw.amd", true, true),
        new("libsvtav1", "av1", "AV1: SVT-AV1", "hw.cpu", false, true),
        new("av1_nvenc", "av1", "AV1: NVENC", "hw.nvidia", true, true),
        new("av1_qsv", "av1", "AV1: Quick Sync", "hw.intel", true, true),
        new("av1_amf", "av1", "AV1: AMF", "hw.amd", true, true),
        new("libaom-av1", "av1", "AV1: libaom", "hw.cpu.slow", false, true),
        new("libvpx-vp9", "vp9", "VP9: libvpx", "hw.cpu", false, true),
        new("mpeg4", "mpeg4", "MPEG-4 Part 2", null, false, true),
        new("prores_ks", "prores", "ProRes", null, false, false)
    ];

    static readonly Encoder[] KnownAudio =
    [
        new("aac", "aac", "AAC", null, false, true),
        new("libmp3lame", "mp3", "MP3", null, false, true),
        new("libopus", "opus", "Opus", null, false, true),
        new("ac3", "ac3", "AC-3", null, false, true),
        new("flac", "flac", "FLAC", "audio.lossless", false, false)
    ];

    /// <summary>
    /// Encoders this ffmpeg build has, with the hardware ones actually tried on a few
    /// frames: a build lists NVENC, QSV and AMF alike whether or not the card is there.
    /// </summary>
    public static async Task<(List<Encoder> Video, List<Encoder> Audio)> DetectEncoders(string ffmpeg)
    {
        try
        {
            var (_, text, _) = await Run(ffmpeg, ["-hide_banner", "-encoders"]);
            var names = text.Split('\n')
                .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[0].Length == 6)
                .Select(p => p[1])
                .ToHashSet();

            var candidates = Known.Where(k => names.Contains(k.Name)).ToList();
            var works = await Task.WhenAll(candidates.Select(k => k.Hardware ? Works(ffmpeg, k.Name) : Task.FromResult(true)));
            return (candidates.Where((_, i) => works[i]).ToList(),
                    KnownAudio.Where(k => names.Contains(k.Name)).ToList());
        }
        catch (Exception) { return ([], []); }
    }

    static async Task<bool> Works(string ffmpeg, string encoder)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var (code, _, _) = await Run(ffmpeg,
                ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=size=640x360:rate=30",
                 "-frames:v", "5", "-c:v", encoder, "-f", "null", "-"], cts.Token);
            return code == 0;
        }
        catch (Exception) { return false; }
    }

    // ---------- экспорт ----------

    /// <summary>
    /// The value each quality level maps to for this encoder, and what the encoder calls it.
    /// The numbers put the levels at a similar picture across encoders: x264 CRF 23 is its
    /// own default, and the others are estimates of an equivalent picture, not measured here.
    /// </summary>
    public static (string Param, int[] Values)? QualityScale(string encoder) => encoder switch
    {
        "libx264" => ("CRF", [16, 20, 23, 28]),
        "libx265" => ("CRF", [18, 22, 26, 30]),
        "h264_nvenc" => ("CQ", [19, 23, 27, 32]),
        "hevc_nvenc" => ("CQ", [21, 25, 29, 33]),
        "av1_nvenc" => ("CQ", [24, 30, 36, 42]),
        "h264_qsv" or "hevc_qsv" or "av1_qsv" => ("ICQ", [18, 22, 26, 30]),
        "h264_amf" or "hevc_amf" or "av1_amf" => ("QP", [18, 22, 26, 30]),
        "libsvtav1" => ("CRF", [24, 30, 35, 42]),
        "libaom-av1" => ("CRF", [20, 28, 34, 40]),
        "libvpx-vp9" => ("CRF", [20, 28, 33, 40]),
        "mpeg4" => ("q", [2, 4, 6, 10]),
        _ => null
    };

    static string[] QualityArgs(string encoder, int q)
    {
        var v = q.ToString(Inv);
        return encoder switch
        {
            "libx264" or "libx265" or "libsvtav1" => ["-crf", v],
            "libaom-av1" or "libvpx-vp9" => ["-crf", v, "-b:v", "0"],
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => ["-rc", "vbr", "-cq", v, "-b:v", "0"],
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => ["-global_quality", v],
            "h264_amf" or "hevc_amf" or "av1_amf" => ["-rc", "cqp", "-qp_i", v, "-qp_p", v],
            "mpeg4" => ["-q:v", v],
            _ => []
        };
    }

    /// <summary>
    /// Bitrate for a smaller frame. Proportional to the pixel count would starve it: a
    /// smaller frame has less detail per pixel to lose, and encoders spend relatively more
    /// on it. The 0.75 power is a rule of thumb, not measured here; 1080p to 720p keeps 54 %.
    /// </summary>
    public static long ScaleBitrate(long kbps, long fromPixels, long toPixels) =>
        fromPixels <= 0 || toPixels >= fromPixels
            ? kbps
            : (long)Math.Round(kbps * Math.Pow((double)toPixels / fromPixels, 0.75) / 10) * 10;

    public static List<string> ExportArgs(VideoInfo v, double start, double end, ExportOptions o, string output)
    {
        var a = new List<string>
        {
            "-hide_banner", "-y", "-v", "error", "-nostats", "-progress", "pipe:1",
            // -ss до -i: при перекодировании поиск точный до кадра, при копировании — до
            // ключевого кадра перед точкой
            "-ss", Sec(start), "-i", v.Path, "-t", Sec(end - start)
        };

        switch (o.Kind)
        {
            case OutputKind.Gif:
                GifArgs(a, v, o);
                break;
            case OutputKind.M4a or OutputKind.Mp3:
                a.AddRange(["-map", "0:a:0", "-vn"]);
                AudioArgs(a, o);
                break;
            default:
                VideoArgs(a, v, o);
                break;
        }

        var ext = System.IO.Path.GetExtension(output).ToLowerInvariant();
        if (ext is ".mp4" or ".m4v" or ".mov" or ".m4a")
        {
            a.AddRange(["-movflags", "+faststart"]);

            // без hvc1 проигрыватели Apple и Windows не открывают H.265 в mp4
            if (o.Kind == OutputKind.Video && (o.Video?.Codec ?? v.Codec) == "hevc") a.AddRange(["-tag:v", "hvc1"]);
        }

        if (o.Kind != OutputKind.Gif) a.AddRange(["-map_metadata", "0"]);
        a.Add(output);
        return a;
    }

    static void VideoArgs(List<string> a, VideoInfo v, ExportOptions o)
    {
        a.AddRange(["-map", "0:v:0"]);
        if (o.Audio) a.AddRange(["-map", "0:a?"]);

        if (o.Video is not { } encoder)
        {
            a.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
            return;
        }

        // setsar=1: без него плеер растянет кадр обратно к исходным пропорциям
        var filters = new List<string>();
        if (o.Fps is double fps) filters.Add("fps=" + Sec(fps));
        if (o.Scale is (int w, int h)) filters.Add($"scale={w}:{h}:flags=lanczos,setsar=1");
        if (filters.Count > 0) a.AddRange(["-vf", string.Join(",", filters)]);

        a.AddRange(["-c:v", encoder.Name]);
        if (o.Quality is int q) a.AddRange(QualityArgs(encoder.Name, q));
        else if (encoder.Bitrate && o.Kbps is > 0) a.AddRange(["-b:v", o.Kbps + "k"]);

        switch (encoder.Name)
        {
            case "libaom-av1": a.AddRange(["-cpu-used", "6", "-row-mt", "1"]); break;
            case "libsvtav1": a.AddRange(["-preset", "8"]); break;
            case "libvpx-vp9": a.AddRange(["-deadline", "good", "-cpu-used", "4", "-row-mt", "1"]); break;

            // x265 пишет сводку в stderr мимо -v error, и она заслоняет настоящую ошибку
            case "libx265": a.AddRange(["-x265-params", "log-level=error"]); break;
        }

        // метки времени кадров переносятся как есть: частота кадров остаётся исходной,
        // в том числе переменная у записей с телефона
        a.AddRange(["-fps_mode", "passthrough"]);

        if (!o.Audio) a.Add("-an");
        else AudioArgs(a, o);
    }

    static void AudioArgs(List<string> a, ExportOptions o)
    {
        if (o.AudioEncoder is not { } ae)
        {
            a.AddRange(["-c:a", "copy"]);
            return;
        }
        a.AddRange(["-c:a", ae.Name]);
        if (ae.Bitrate && o.AudioKbps is > 0) a.AddRange(["-b:a", o.AudioKbps + "k"]);
    }

    public const int GifFps = 15, GifMaxWidth = 640;

    /// <summary>
    /// One pass with its own palette: palettegen needs the whole clip before paletteuse can
    /// start, and split feeds it the same frames. The default 256-colour web palette shows
    /// banding on gradients.
    /// </summary>
    static void GifArgs(List<string> a, VideoInfo v, ExportOptions o)
    {
        string scale = o.Scale is (int w, int h) ? $"scale={w}:{h}"
            : v.Width > GifMaxWidth ? $"scale={GifMaxWidth}:-1"
            : "scale=iw:ih";

        a.AddRange(["-map", "0:v:0", "-an", "-vf",
            $"fps={Sec(o.Fps ?? GifFps)},{scale}:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5",
            "-loop", "0"]);
    }

    public static string LastLines(string text, int n) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));
}
