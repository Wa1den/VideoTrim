using System.Net.Http;
using System.Text.Json;

namespace VideoTrim;

/// <summary>
/// Asks GitHub whether a release newer than this build exists, as CaseLight does
/// (Docs/Решения.md, «Проверка обновлений на GitHub»).
///
/// The releases API answers with the one release marked latest, skipping drafts and
/// pre-releases. For a private repository it answers 404 to an anonymous request, same as
/// for a repository without releases.
/// </summary>
public static class UpdateCheck
{
    const string Api = "https://api.github.com/repos/Wa1den/VideoTrim/releases/latest";
    public const string Page = AboutWindow.Repo + "/releases/latest";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <param name="Newer">null — установлена последняя версия или проверка не удалась.</param>
    /// <param name="Error">Причина неудачи для ручной проверки; при проверке на старте не показывается.</param>
    public sealed record Result(Version? Newer, string Url, string? Error);

    public static async Task<Result> Check()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api);

            // без User-Agent GitHub отвечает 403
            request.Headers.UserAgent.ParseAdd("VideoTrim/" + AboutWindow.Version);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request);
            if ((int)response.StatusCode == 404)
                return new Result(null, Page, Loc.P("релизов на GitHub пока нет, или репозиторий закрыт", "GitHub has no releases yet, or the repository is private"));
            if (!response.IsSuccessStatusCode)
                return new Result(null, Page, Loc.P("GitHub ответил ", "GitHub answered ") + $"{(int)response.StatusCode} {response.ReasonPhrase}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? Page : Page;

            if (!TryRead(tag, out var latest)) return new Result(null, url, Loc.P("не разобран тег релиза ", "could not read the release tag ") + tag);
            var current = Three(typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0));
            return new Result(latest > current ? latest : null, url, null);
        }
        catch (Exception ex)
        {
            return new Result(null, Page, ex is TaskCanceledException ? Loc.P("GitHub не ответил за 10 секунд", "GitHub did not answer within 10 seconds") : ex.Message);
        }
    }

    /// <summary>
    /// Assembly versions carry four numbers and release tags three, and Version counts a
    /// missing fourth as lower than zero: 1.0.0 from a tag would read as older than 1.0.0.0.
    /// </summary>
    static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool TryRead(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed)) return false;
        version = Three(parsed);
        return true;
    }
}
