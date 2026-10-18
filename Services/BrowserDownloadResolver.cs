using System.Text.RegularExpressions;
using System.Text.Json;

namespace CoolSnesh.Avalonia.Services;

public sealed record BrowserRelease(string Version, string DownloadUrl, string FileName);

/// <summary>Resolves releases whose official vendor pages publish rotating versioned installer URLs.</summary>
public static class BrowserDownloadResolver
{
    private const string TorDownloadPage = "https://download.torproject.org/tor-browser-for-desktop/";
    private const string VivaldiDownloadPage = "https://vivaldi.com/ru/download/";
    private const string LibreWolfDownloadPage = "https://librewolf.net/installation/windows/";
    private const string NvidiaAppDownloadPage = "https://www.nvidia.com/en-us/software/nvidia-app/";
    private const string VentoyLatestReleaseApi = "https://api.github.com/repos/ventoy/Ventoy/releases/latest";

    public static async Task<BrowserRelease?> ResolveTorBrowserAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(TorDownloadPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseTorBrowserDownloadPage(html);
    }

    public static BrowserRelease? ParseTorBrowserDownloadPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        html = System.Net.WebUtility.HtmlDecode(html);
        var links = Regex.Matches(
            html,
            """href\s*=\s*["'](?<href>[^"']+)["']""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match link in links)
        {
            var rawUrl = link.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(rawUrl))
                continue;

            Uri? uri = null;
            if (rawUrl.StartsWith("//", StringComparison.Ordinal))
            {
                if (Uri.TryCreate("https:" + rawUrl, UriKind.Absolute, out var protocolRelative))
                    uri = protocolRelative;
            }
            else if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var absolute))
            {
                uri = absolute;
            }
            else if (Uri.TryCreate(new Uri(TorDownloadPage), rawUrl, out var relative))
            {
                uri = relative;
            }

            if (uri is null ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                continue;

            var host = uri.Host;
            var isOfficialHost = host.Equals("torproject.org", StringComparison.OrdinalIgnoreCase) ||
                                 host.EndsWith(".torproject.org", StringComparison.OrdinalIgnoreCase);
            if (!isOfficialHost)
                continue;

            var match = Regex.Match(
                uri.AbsolutePath,
                @"^/(?:dist/)?torbrowser/(?<folder>\d+(?:\.\d+){1,3})/tor-browser-windows-x86_64-portable-(?<version>\d+(?:\.\d+){1,3})\.exe$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var folderVersion = match.Groups["folder"].Value;
            var fileVersion = match.Groups["version"].Value;
            if (!string.Equals(folderVersion, fileVersion, StringComparison.Ordinal))
                continue;

            return new BrowserRelease(fileVersion, uri.ToString(), Path.GetFileName(uri.LocalPath));
        }

        return null;
    }

    public static async Task<BrowserRelease?> ResolveVivaldiAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(VivaldiDownloadPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseVivaldiDownloadPage(html);
    }

    public static BrowserRelease? ParseVivaldiDownloadPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = System.Net.WebUtility.HtmlDecode(html);
        var match = Regex.Match(html,
            @"(?<url>https?://downloads\.vivaldi\.com/stable/Vivaldi\.(?<version>\d+(?:\.\d+){2,4})\.x64\.exe)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var uri = new Uri(match.Groups["url"].Value);
        return new BrowserRelease(match.Groups["version"].Value, uri.ToString(), Path.GetFileName(uri.LocalPath));
    }

    public static async Task<BrowserRelease?> ResolveLibreWolfAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(LibreWolfDownloadPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseLibreWolfDownloadPage(html);
    }

    /// <summary>Parses the current Windows x64 installer from LibreWolf's official Windows page.</summary>
    public static BrowserRelease? ParseLibreWolfDownloadPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = System.Net.WebUtility.HtmlDecode(html);

        var match = Regex.Match(html,
            @"(?<url>(?:https?:)?//dl\.librewolf\.net/librewolf/(?<version>\d+(?:\.\d+){1,2}-\d+)/librewolf-\d+(?:\.\d+){1,2}-\d+-windows-x86_64-setup\.exe)|(?<relative>/librewolf/(?<version2>\d+(?:\.\d+){1,2}-\d+)/librewolf-\d+(?:\.\d+){1,2}-\d+-windows-x86_64-setup\.exe)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var rawUrl = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["relative"].Value;
        var uri = rawUrl.StartsWith("//", StringComparison.Ordinal)
            ? new Uri("https:" + rawUrl)
            : Uri.TryCreate(rawUrl, UriKind.Absolute, out var absolute)
                ? absolute
                : new Uri(new Uri("https://dl.librewolf.net/"), rawUrl);
        if (!uri.Host.Equals("dl.librewolf.net", StringComparison.OrdinalIgnoreCase)) return null;
        var version = match.Groups["version"].Success ? match.Groups["version"].Value : match.Groups["version2"].Value;
        return new BrowserRelease(version, uri.ToString(), Path.GetFileName(uri.LocalPath));
    }

    public static async Task<BrowserRelease?> ResolveNvidiaAppAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(NvidiaAppDownloadPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseNvidiaAppDownloadPage(html);
    }

    /// <summary>Parses the installer URL linked by NVIDIA's official App page.</summary>
    public static BrowserRelease? ParseNvidiaAppDownloadPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = System.Net.WebUtility.HtmlDecode(html);
        var match = Regex.Match(html,
            @"(?<url>(?:https?:)?//(?<host>(?:us\.)?download\.nvidia\.com)/nvapp/client/(?<version>\d+(?:\.\d+){2,4})/(?<file>NVIDIA_app_v\d+(?:\.\d+){2,4}\.exe))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var rawUrl = match.Groups["url"].Value;
        var uri = rawUrl.StartsWith("//", StringComparison.Ordinal) ? new Uri("https:" + rawUrl) : new Uri(rawUrl);
        if (!uri.Host.EndsWith("download.nvidia.com", StringComparison.OrdinalIgnoreCase)) return null;
        return new BrowserRelease(match.Groups["version"].Value, uri.ToString(), match.Groups["file"].Value);
    }

    private const string RufusDownloadsPage = "https://rufus.ie/downloads/";
    private const string WinRarDownloadPage = "https://www.rarlab.com/download.htm";
    private const string SevenZipDownloadPage = "https://www.7-zip.org/";

    public static async Task<BrowserRelease?> ResolveVentoyAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        // Use Ventoy's official GitHub release API and the attached Windows ZIP asset.
        using var request = new HttpRequestMessage(HttpMethod.Get, VentoyLatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseVentoyLatestReleaseJson(json);
    }

    public static BrowserRelease? ParseVentoyLatestReleaseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                var match = Regex.Match(name, @"^ventoy-(?<version>\d+\.\d+(?:\.\d+)?)-windows\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith("/ventoy/Ventoy/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
                return new BrowserRelease(match.Groups["version"].Value, uri.ToString(), name);
            }
        }
        catch (JsonException ex) { AppLog.Write("VersionCheck", "Не удалось разобрать JSON-релиз Ventoy.", ex); }
        return null;
    }

    /// <summary>Legacy HTML parser retained for older page fixtures; links now point to GitHub releases.</summary>
    public static BrowserRelease? ParseVentoyDownloadPage(string? html)
    {
        var version = ExtractHighestVersion(html, @"ventoy-(?<version>\d+\.\d+(?:\.\d+)?)-windows\.zip");
        if (version is null) return null;
        var url = $"https://github.com/ventoy/Ventoy/releases/download/v{version}/ventoy-{version}-windows.zip";
        return new BrowserRelease(version, url, $"ventoy-{version}-windows.zip");
    }

    public static async Task<BrowserRelease?> ResolveRufusAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(RufusDownloadsPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseRufusDownloadPage(html);
    }

    public static BrowserRelease? ParseRufusDownloadPage(string? html)
    {
        var version = ExtractHighestVersion(html, @"rufus-(?<version>\d+\.\d+(?:\.\d+)?)(?:p|_x86|_arm64)?\.exe");
        if (version is null) return null;
        var url = $"https://github.com/pbatard/rufus/releases/download/v{version}/rufus-{version}.exe";
        return new BrowserRelease(version, url, $"rufus-{version}.exe");
    }

    public static async Task<BrowserRelease?> ResolveWinRarAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(WinRarDownloadPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseWinRarDownloadPage(html);
    }

    public static BrowserRelease? ParseWinRarDownloadPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = System.Net.WebUtility.HtmlDecode(html);
        // Parse the stable 64-bit WinRAR link from RARLAB's official download page.
        var anchor = Regex.Match(html,
            """(?is)<a[^>]+href=['"](?<url>(?:https?:)?//(?:www\.)?rarlab\.com/rar/winrar-x64-(?<build>\d+)\.exe|/rar/winrar-x64-(?<build2>\d+)\.exe)['"][^>]*>(?<text>.*?)</a>""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (anchor.Success)
        {
            var build = anchor.Groups["build"].Success ? anchor.Groups["build"].Value : anchor.Groups["build2"].Value;
            var rawUrl = anchor.Groups["url"].Value;
            var uri = rawUrl.StartsWith("//", StringComparison.Ordinal) ? new Uri("https:" + rawUrl) : new Uri(new Uri(WinRarDownloadPage), rawUrl);
            var versionText = anchor.Groups["text"].Value;
            var version = Regex.Match(versionText, @"(?<version>\d+\.\d+)").Groups["version"].Value;
            if (string.IsNullOrWhiteSpace(version) && build.Length >= 3) version = build[0] + "." + build[1..];
            if (!string.IsNullOrWhiteSpace(version)) return new BrowserRelease(version, uri.ToString(), Path.GetFileName(uri.LocalPath));
        }

        // Text fallback for the page's main stable x64 row.
        var match = Regex.Match(html, @"(?is)WinRAR x64\s*\(64 bit\).{0,1000}?(?<version>\d+\.\d+)", RegexOptions.CultureInvariant);
        return match.Success ? new BrowserRelease(match.Groups["version"].Value,
            $"https://www.rarlab.com/rar/winrar-x64-{match.Groups["version"].Value.Replace(".", "")}.exe",
            $"winrar-x64-{match.Groups["version"].Value.Replace(".", "")}.exe") : null;
    }

    public static async Task<BrowserRelease?> ResolveSevenZipAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(SevenZipDownloadPage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseSevenZipDownloadPage(html);
    }

    public static BrowserRelease? ParseSevenZipDownloadPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = System.Net.WebUtility.HtmlDecode(html);
        var match = Regex.Match(html, @"7-Zip\s+(?<version>\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var version = match.Groups["version"].Value;
        var compact = version.Replace(".", "");
        return new BrowserRelease(version, $"https://www.7-zip.org/a/7z{compact}-x64.exe", $"7z{compact}-x64.exe");
    }

    public static string? ParseRufusVersion(string? html) => ExtractHighestVersion(html, @"rufus-(?<version>\d+\.\d+(?:\.\d+)?)(?:p|_x86|_arm64)?\.exe");
    public static string? ParseVentoyVersion(string? html) => ExtractHighestVersion(html, @"ventoy-(?<version>\d+\.\d+(?:\.\d+)?)-windows\.zip");

    public static string? ParseSevenZipVersion(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var match = Regex.Match(System.Net.WebUtility.HtmlDecode(html), @"7-Zip\s+(?<version>\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : null;
    }

    public static string? ParseWinRarVersion(string? html)
    {
        var release = ParseWinRarDownloadPage(html);
        return release?.Version;
    }

    private static string? ExtractHighestVersion(string? html, string pattern)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = System.Net.WebUtility.HtmlDecode(html);
        Version? highest = null;
        string? highestText = null;
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var text = match.Groups["version"].Value;
            if (!Version.TryParse(text, out var parsed)) continue;
            if (highest is null || parsed > highest)
            {
                highest = parsed;
                highestText = text;
            }
        }
        return highestText;
    }

}
