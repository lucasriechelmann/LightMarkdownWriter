using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace LightMarkdownWriter.Services;

/// <summary>A newer release: its version (e.g. "1.3.0") and the GitHub page to download it from.</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// Asks GitHub for the latest release at most once a day and says whether it's newer than this build. Only
/// informs: the user downloads it themselves. Failures (offline, rate limit, odd JSON) are silent; the next day
/// tries again. Sends nothing but a generic User-Agent (GitHub requires one).
/// </summary>
public static class UpdateService
{
    public const string LatestReleaseApi = "https://api.github.com/repos/lucasriechelmann/LightMarkdownWriter/releases/latest";
    public const string ReleasesPage = "https://github.com/lucasriechelmann/LightMarkdownWriter/releases/latest";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>This build's version (from the csproj &lt;Version&gt;). Debug builds can pretend to be older with
    /// the LMW_FAKE_VERSION environment variable, to see the prompt.</summary>
    public static Version CurrentVersion
    {
        get
        {
#if DEBUG
            if (Version.TryParse(Environment.GetEnvironmentVariable("LMW_FAKE_VERSION"), out var fake)) return fake;
#endif
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        }
    }

    /// <summary>
    /// Checks for an update, using the result cached in the settings when the last check was less than a day ago.
    /// Returns null when updates are off, nothing newer exists, or the check failed.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(AppSettings settings, Version current, HttpMessageHandler? handler = null,
                                                     DateTimeOffset? now = null)
    {
        if (!settings.CheckForUpdates) return null;
        var time = now ?? DateTimeOffset.UtcNow;

        if (settings.LastUpdateCheckUtc is not { } last || time - last >= CheckInterval || time < last)
        {
            if (await FetchLatestAsync(handler) is not { } latest) return null; // offline: try again next start
            settings.LatestReleaseTag = latest.Tag;
            settings.LatestReleaseUrl = latest.Url;
            settings.LastUpdateCheckUtc = time;
        }

        return settings.LatestReleaseTag is { } tag && IsNewer(tag, current)
            ? new UpdateInfo(VersionOf(tag)!.ToString(3), settings.LatestReleaseUrl ?? ReleasesPage)
            : null;
    }

    private static async Task<(string Tag, string Url)?> FetchLatestAsync(HttpMessageHandler? handler)
    {
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LightMarkdownWriter", CurrentVersion.ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return ParseLatest(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null; // offline, DNS, timeout, proxy trouble: never bother the user about an update check
        }
    }

    /// <summary>The tag and page of a GitHub "latest release" response; null for drafts, pre-releases or bad JSON.</summary>
    public static (string Tag, string Url)? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName || VersionOf(tagName) is null)
                return null;
            var url = root.TryGetProperty("html_url", out var html) && html.GetString() is { } page
                      && page.StartsWith("https://github.com/", StringComparison.Ordinal) ? page : ReleasesPage;
            return (tagName, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a tag like "v1.3.0" is a newer version than <paramref name="current"/> (major.minor.patch).</summary>
    public static bool IsNewer(string tag, Version current) =>
        VersionOf(tag) is { } latest && latest > Normalize(current);

    /// <summary>"v1.3.0", "1.3", "v1.3.0-beta+abc" → 1.3.0; null if it isn't a version.</summary>
    public static Version? VersionOf(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) text = text[..end];
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    // Compare as major.minor.patch: 1.3 equals 1.3.0, and the build's 4th part (always 0) is ignored.
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
