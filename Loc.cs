using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VideoTrim;

/// <summary>
/// Strings by key, a copy of the CaseLight Loc (Docs/Локализация.md) cut down to what this
/// program needs. Two languages are built in and written out as JSON to
/// %AppData%\VideoTrim\lang on first run; from then on the folder is what gets read, and
/// any other file there joins the language list without a rebuild.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Bumped whenever the built-in strings change: files on disk win over the built-ins,
    /// and without a new version an old file would hide a reworded label.
    /// </summary>
    const string Version = "1";

    const string VersionKey = "_version";
    const string NameKey = "_name";

    static readonly string[] BuiltinCodes = { "ru", "en" };

    public static string Language { get; private set; } = "ru";

    static Dictionary<string, string> _current = new();
    static string[] _available = BuiltinCodes;
    static readonly Dictionary<string, string> _names = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoTrim", "lang");

    public static string[] Available => _available;

    public static string DisplayName(string code) =>
        _names.TryGetValue(code, out var name) ? name : BuiltinName(code);

    static string BuiltinName(string code) => code switch
    {
        "ru" => "Русский",
        "en" => "English",
        _ => code
    };

    /// <summary>Russian for a Russian-speaking system, English for any other.</summary>
    public static string SystemDefault() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" ? "ru" : "en";

    public static void Load(string language)
    {
        WriteDefaults();
        Scan();

        Language = Array.IndexOf(_available, language) >= 0 ? language : "ru";

        // английский подложкой: строка, пропущенная в переводе, показывается
        // по-английски, а не ключом
        var strings = English();
        if (Array.IndexOf(BuiltinCodes, Language) >= 0) Overlay(strings, Builtin(Language));
        Overlay(strings, ReadLocale(Language));

        _current = strings;
    }

    static void Overlay(Dictionary<string, string> onto, Dictionary<string, string>? from)
    {
        if (from == null) return;
        foreach (var kv in from) onto[kv.Key] = kv.Value;
    }

    /// <summary>Missing keys fall back to the key itself, so nothing renders blank.</summary>
    public static string T(string key) => _current.TryGetValue(key, out var v) ? v : key;

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>
    /// A translated pair written inline, for one-off text such as status messages. Anything
    /// other than Russian gets the English half.
    /// </summary>
    public static string P(string ru, string en) => Language == "ru" ? ru : en;

    static void Scan()
    {
        _names.Clear();
        foreach (var code in BuiltinCodes) _names[code] = BuiltinName(code);

        var extra = new List<string>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(path);
                if (code.Length == 0) continue;

                var loaded = ReadLocale(code);
                if (loaded == null) continue;

                if (Array.IndexOf(BuiltinCodes, code) < 0) extra.Add(code);

                if (loaded.TryGetValue(NameKey, out var name) && name.Trim().Length > 0) _names[code] = name.Trim();
                else if (!_names.ContainsKey(code)) _names[code] = code;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("lang: " + ex.Message);
        }

        extra.Sort(StringComparer.OrdinalIgnoreCase);
        _available = BuiltinCodes.Concat(extra).ToArray();
    }

    /// <summary>
    /// How many known keys a file must carry to count as a translation: enough to keep an
    /// unrelated JSON file out of the list, few enough to let a partial translation in.
    /// </summary>
    const int MinKnownKeys = 8;

    static Dictionary<string, string>? ReadLocale(string code)
    {
        string path = Path.Combine(Directory, code + ".json");
        if (!File.Exists(path)) return null;

        try
        {
            // нестроковое значение бросает исключение здесь, это и есть проверка формата
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded == null) return null;

            var known = English();
            return loaded.Keys.Count(known.ContainsKey) >= MinKnownKeys ? loaded : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("lang: " + Path.GetFileName(path) + ": " + ex.Message);
            return null;
        }
    }

    static void WriteDefaults()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // без ослабленного кодировщика каждая кириллическая буква пишется как \uXXXX,
            // и файл для правки руками нечитаем
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            foreach (var code in BuiltinCodes)
            {
                string path = Path.Combine(Directory, code + ".json");
                if (File.Exists(path) && VersionOf(path) == Version) continue;
                File.WriteAllText(path, JsonSerializer.Serialize(Builtin(code), opts));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("lang: " + ex.Message);
        }
    }

    static string VersionOf(string path)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return d != null && d.TryGetValue(VersionKey, out var v) ? v : "";
        }
        catch (Exception) { return ""; }
    }

    static Dictionary<string, string> Builtin(string code)
    {
        var d = code == "en" ? English() : Russian();
        d[NameKey] = BuiltinName(code);
        d[VersionKey] = Version;
        return d;
    }

    static Dictionary<string, string> Russian() => new()
    {
        ["app.open"] = "Открыть…",
        ["app.about"] = "О программе",
        ["app.language"] = "Язык",
        ["app.placeholder"] = "Перетащите видео в окно или откройте его кнопкой «Открыть»",
        ["app.nofile"] = "Файл не открыт",

        ["play.toggle"] = "Воспроизведение и пауза, пробел",
        ["play.back"] = "−1 с",
        ["play.back.note"] = "Стрелка влево, с Shift на 10 секунд",
        ["play.forward"] = "+1 с",
        ["play.forward.note"] = "Стрелка вправо, с Shift на 10 секунд",

        ["range.start"] = "Начало",
        ["range.end"] = "Конец",
        ["range.fromhere"] = "Отсюда",
        ["range.fromhere.note"] = "Начало фрагмента на текущей секунде, клавиша I",
        ["range.tohere"] = "Досюда",
        ["range.tohere.note"] = "Конец фрагмента после текущей секунды, клавиша O",
        ["range.time.note"] = "Минуты и секунды через двоеточие, например 1:25, или часы, минуты и секунды: 1:02:05",
        ["range.length"] = "Длина {0}",

        ["info.fps"] = "{0} кадров/с",
        ["info.fps.unknown"] = "частота кадров неизвестна",
        ["info.estimate"] = "оценка",
        ["info.audio"] = "звук {0}",
        ["unit.kbps"] = "кбит/с",
        ["unit.mbps"] = "Мбит/с",

        ["tab.encoding"] = "Кодирование",
        ["tab.video"] = "Видео",
        ["tab.audio"] = "Аудио",

        ["mode.reencode"] = "Перекодировать",
        ["mode.reencode.note"] = "Фрагмент начинается и заканчивается ровно на выбранных секундах. Видео кодируется заново по настройкам на вкладках. Время зависит от кодировщика и разрешения: 15 секунд 720p через x264 сохраняются примерно за 2 секунды.",
        ["mode.copy"] = "Без перекодирования",
        ["mode.copy.note"] = "Потоки копируются как есть: качество не меняется, сохранение занимает секунды, настройки на вкладках не действуют. Начало сдвигается назад к ближайшему ключевому кадру, поэтому фрагмент может начаться на несколько секунд раньше выбранного.",

        ["enc.codec"] = "Кодек",
        ["enc.codec.note"] = "По умолчанию тот же формат, что в исходнике, и процессорный кодировщик. Кодировщики видеокарты разгружают процессор, но при том же битрейте дают картинку хуже.",
        ["enc.mode"] = "Режим",
        ["enc.mode.note"] = "По битрейту размер файла известен заранее, а качество плавает: сложные сцены выходят хуже простых. По качеству наоборот: картинка ровная, а размер зависит от содержимого.",
        ["enc.mode.bitrate"] = "По битрейту",
        ["enc.mode.quality"] = "По качеству",
        ["enc.bitrate"] = "Битрейт, кбит/с",
        ["enc.bitrate.note"] = "Средний битрейт видеопотока. Исходный берётся из файла, а если в файле его нет, считается по размеру и длительности. При уменьшении кадра исходный битрейт снижается вместе с ним: 1080p → 720p оставляет 54 %. Пустое поле оставляет выбор кодировщику.",
        ["enc.bitrate.same"] = "исходный",
        ["enc.quality"] = "Качество",
        ["enc.quality.note"] = "Качество держится постоянным, а битрейт меняется от сцены к сцене: на сложных он выше, на простых ниже. «Высокое» у x264 — CRF 20, «Среднее» — CRF 23, значение x264 по умолчанию. Чем больше число, тем меньше файл и хуже картинка.",
        ["quality.0"] = "Почти без потерь",
        ["quality.1"] = "Высокое",
        ["quality.2"] = "Среднее",
        ["quality.3"] = "Низкое",
        ["hw.cpu"] = "процессор",
        ["hw.cpu.slow"] = "процессор, медленно",
        ["hw.nvidia"] = "видеокарта NVIDIA",
        ["hw.intel"] = "графика Intel",
        ["hw.amd"] = "видеокарта AMD",
        ["audio.lossless"] = "без потерь",

        ["video.size"] = "Размер кадра",
        ["video.size.note"] = "Уменьшение кадра масштабированием Lanczos. С пропорциями список идёт от исходного размера вниз по стандартным высотам. Без них предлагаются размеры 16:9, и кадр другой формы растягивается до них.",
        ["video.size.source"] = "Исходное, {0}×{1}",
        ["video.aspect"] = "Сохранить пропорции",
        ["video.aspect.note"] = "Высота выбирается из стандартных, ширина считается по пропорциям исходного кадра и округляется до чётной: кодировщики требуют чётных размеров.",
        ["video.fps"] = "Частота кадров",
        ["video.fps.note"] = "Снижение частоты отбрасывает кадры равномерно по времени, движение становится менее плавным. Выше исходной частоту поднять нельзя: новых кадров взять неоткуда. Для GIF по умолчанию 15 кадров в секунду.",
        ["video.fps.source"] = "Исходная, {0}",

        ["audio.keep"] = "Сохранить звук",
        ["audio.keep.note"] = "Без галки фрагмент сохраняется без звуковой дорожки.",
        ["audio.same"] = "Исходный",
        ["audio.same.note"] = "Звук копируется без перекодирования, с исходными кодеком и битрейтом.",
        ["audio.codec"] = "Кодек",
        ["audio.bitrate"] = "Битрейт, кбит/с",
        ["audio.bitrate.note"] = "FLAC сжимает без потерь, битрейт у него не задаётся.",

        ["format.video"] = "Видео, {0}",
        ["format.gif"] = "GIF",
        ["format.m4a"] = "Звук, .m4a",
        ["format.mp3"] = "Звук, .mp3",
        ["format.note"] = "GIF: частота кадров и размер с вкладки «Видео», а при исходном размере не шире {0} точек, без звука. Звук: дорожка фрагмента без видео, с кодеком и битрейтом с вкладки «Аудио».",
        ["export.save"] = "Сохранить фрагмент…",
        ["export.cancel"] = "Отмена",
        ["export.show"] = "Показать в папке",
        ["export.estimate.note"] = "Оценка по битрейту и длине фрагмента. В режиме качества и для GIF размер зависит от содержимого и заранее неизвестен.",

        ["about.title"] = "О программе",
        ["about.version"] = "Версия {0}",
        ["about.text"] = "Вырезает фрагмент из видео через ffmpeg. Разрешение, частота кадров, кодек и битрейт по умолчанию остаются исходными, их можно сменить. Фрагмент режется точно по секундам с перекодированием или без потерь по ключевым кадрам.",
        ["about.ffmpeg"] = "ffmpeg: {0}",
        ["about.ffmpeg.missing"] = "ffmpeg не найден",
        ["about.source"] = "Исходный код: ",
        ["about.updates.auto"] = "Проверять обновления при запуске",
        ["about.updates.check"] = "Проверить сейчас",
        ["about.updates.checking"] = "Проверка…",
        ["about.updates.available"] = "Доступна версия {0}: ",
        ["about.updates.page"] = "страница релиза",
        ["about.updates.failed"] = "Проверить не удалось: {0}",
        ["about.updates.latest"] = "Установлена последняя версия",
        ["about.close"] = "Закрыть",
    };

    static Dictionary<string, string> English() => new()
    {
        ["app.open"] = "Open…",
        ["app.about"] = "About",
        ["app.language"] = "Language",
        ["app.placeholder"] = "Drop a video onto the window or open it with the Open button",
        ["app.nofile"] = "No file open",

        ["play.toggle"] = "Play and pause, Space",
        ["play.back"] = "−1 s",
        ["play.back.note"] = "Left arrow, with Shift by 10 seconds",
        ["play.forward"] = "+1 s",
        ["play.forward.note"] = "Right arrow, with Shift by 10 seconds",

        ["range.start"] = "Start",
        ["range.end"] = "End",
        ["range.fromhere"] = "From here",
        ["range.fromhere.note"] = "Clip start at the current second, key I",
        ["range.tohere"] = "To here",
        ["range.tohere.note"] = "Clip end after the current second, key O",
        ["range.time.note"] = "Minutes and seconds separated by a colon, such as 1:25, or hours, minutes and seconds: 1:02:05",
        ["range.length"] = "Length {0}",

        ["info.fps"] = "{0} fps",
        ["info.fps.unknown"] = "frame rate unknown",
        ["info.estimate"] = "estimate",
        ["info.audio"] = "audio {0}",
        ["unit.kbps"] = "kbit/s",
        ["unit.mbps"] = "Mbit/s",

        ["tab.encoding"] = "Encoding",
        ["tab.video"] = "Video",
        ["tab.audio"] = "Audio",

        ["mode.reencode"] = "Re-encode",
        ["mode.reencode.note"] = "The clip starts and ends exactly on the chosen seconds. The video is encoded again with the settings on the tabs. The time depends on the encoder and resolution: 15 seconds of 720p through x264 take about 2 seconds.",
        ["mode.copy"] = "Copy streams",
        ["mode.copy.note"] = "The streams are copied as they are: quality does not change, saving takes seconds, the settings on the tabs do not apply. The start moves back to the nearest keyframe, so the clip may begin a few seconds earlier than chosen.",

        ["enc.codec"] = "Codec",
        ["enc.codec.note"] = "By default the same format as the source and a CPU encoder. Graphics card encoders take the load off the CPU but give a worse picture at the same bitrate.",
        ["enc.mode"] = "Mode",
        ["enc.mode.note"] = "By bitrate the file size is known in advance and the quality varies: complex scenes come out worse than simple ones. By quality it is the other way round: the picture is even and the size depends on the content.",
        ["enc.mode.bitrate"] = "By bitrate",
        ["enc.mode.quality"] = "By quality",
        ["enc.bitrate"] = "Bitrate, kbit/s",
        ["enc.bitrate.note"] = "Average bitrate of the video stream. The source value is read from the file, or worked out from its size and duration when the file does not carry it. When the frame is made smaller the source bitrate goes down with it: 1080p → 720p keeps 54 %. An empty field leaves the choice to the encoder.",
        ["enc.bitrate.same"] = "source",
        ["enc.quality"] = "Quality",
        ["enc.quality.note"] = "Quality is held constant and the bitrate changes from scene to scene: higher on complex ones, lower on simple ones. “High” for x264 is CRF 20, “Medium” is CRF 23, the x264 default. The larger the number, the smaller the file and the worse the picture.",
        ["quality.0"] = "Nearly lossless",
        ["quality.1"] = "High",
        ["quality.2"] = "Medium",
        ["quality.3"] = "Low",
        ["hw.cpu"] = "CPU",
        ["hw.cpu.slow"] = "CPU, slow",
        ["hw.nvidia"] = "NVIDIA graphics card",
        ["hw.intel"] = "Intel graphics",
        ["hw.amd"] = "AMD graphics card",
        ["audio.lossless"] = "lossless",

        ["video.size"] = "Frame size",
        ["video.size.note"] = "Lanczos downscaling. With the aspect ratio kept, the list runs from the source size down through standard heights. Without it the sizes are 16:9, and a frame of another shape is stretched to them.",
        ["video.size.source"] = "Source, {0}×{1}",
        ["video.aspect"] = "Keep aspect ratio",
        ["video.aspect.note"] = "The height is picked from the standard ones, the width follows the aspect ratio of the source frame and is rounded to an even number: encoders need even sizes.",
        ["video.fps"] = "Frame rate",
        ["video.fps.note"] = "A lower rate drops frames evenly over time, and motion gets less smooth. The rate cannot go above the source: there are no frames to add. GIF defaults to 15 frames per second.",
        ["video.fps.source"] = "Source, {0}",

        ["audio.keep"] = "Keep audio",
        ["audio.keep.note"] = "Unchecked, the clip is saved without an audio track.",
        ["audio.same"] = "Source",
        ["audio.same.note"] = "The audio is copied without re-encoding, with the source codec and bitrate.",
        ["audio.codec"] = "Codec",
        ["audio.bitrate"] = "Bitrate, kbit/s",
        ["audio.bitrate.note"] = "FLAC is lossless, it takes no bitrate.",

        ["format.video"] = "Video, {0}",
        ["format.gif"] = "GIF",
        ["format.m4a"] = "Audio, .m4a",
        ["format.mp3"] = "Audio, .mp3",
        ["format.note"] = "GIF: frame rate and size from the Video tab, no wider than {0} pixels at the source size, no audio. Audio: the clip's audio track without video, with the codec and bitrate from the Audio tab.",
        ["export.save"] = "Save clip…",
        ["export.cancel"] = "Cancel",
        ["export.show"] = "Show in folder",
        ["export.estimate.note"] = "Estimate from the bitrate and the clip length. In quality mode and for GIF the size depends on the content and is not known in advance.",

        ["about.title"] = "About",
        ["about.version"] = "Version {0}",
        ["about.text"] = "Cuts a clip out of a video through ffmpeg. Resolution, frame rate, codec and bitrate stay as in the source by default and can be changed. The clip is cut exactly on the second with re-encoding, or losslessly on keyframes.",
        ["about.ffmpeg"] = "ffmpeg: {0}",
        ["about.ffmpeg.missing"] = "ffmpeg not found",
        ["about.source"] = "Source code: ",
        ["about.updates.auto"] = "Check for updates on startup",
        ["about.updates.check"] = "Check now",
        ["about.updates.checking"] = "Checking…",
        ["about.updates.available"] = "Version {0} is available: ",
        ["about.updates.page"] = "release page",
        ["about.updates.failed"] = "Could not check: {0}",
        ["about.updates.latest"] = "This is the latest version",
        ["about.close"] = "Close",
    };
}
