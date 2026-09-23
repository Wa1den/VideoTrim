using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace VideoTrim;

/// <summary>
/// Downloads the essentials build from gyan.dev and puts ffmpeg.exe and ffprobe.exe next
/// to the program, or into %LocalAppData% when the program folder is not writable
/// (Program Files without elevation).
/// </summary>
public static class FfmpegSetup
{
    public const string Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string Page = "https://www.gyan.dev/ffmpeg/builds/";

    public static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoTrim", "ffmpeg");

    /// <param name="progress">Доля от 0 до 1; -1, если сервер не сообщил размер.</param>
    /// <returns>Папка, куда легли exe.</returns>
    public static async Task<string> Install(IProgress<double> progress, CancellationToken ct)
    {
        var target = Writable(AppContext.BaseDirectory) ? AppContext.BaseDirectory : LocalDir;
        Directory.CreateDirectory(target);

        var zip = Path.Combine(Path.GetTempPath(), "VideoTrim-ffmpeg.zip");
        try
        {
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VideoTrim/" + AboutWindow.Version);
                using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long? total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);

                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress.Report(total > 0 ? (double)done / total.Value : -1);
                }
            }

            using var archive = ZipFile.OpenRead(zip);
            foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                                e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase)
                                && e.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException(Loc.P("в архиве нет ", "the archive has no ") + exe);
                entry.ExtractToFile(Path.Combine(target, exe), overwrite: true);
            }
            return target;
        }
        finally
        {
            try { File.Delete(zip); } catch (Exception) { }
        }
    }

    static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }
}
