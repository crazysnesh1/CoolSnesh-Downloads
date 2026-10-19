using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

/// <summary>Resolves official latest-version sources for packages that publish a machine-readable version.</summary>
public static class VersionResolver
{
    public const string UnknownVersion = "Не определяется источником";
    private static readonly ConcurrentDictionary<string, VersionCacheEntry> SuccessfulCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SuccessfulCacheTtl = TimeSpan.FromMinutes(5);
    private const string ProxifierDownloadPage = "https://www.proxifier.com/download/";
    private const string SteamClientManifestUrl = "https://client-update.fastly.steamstatic.com/steam_client_win64";

    public static bool HasKnownSource(PackageItem package) =>
        !string.IsNullOrWhiteSpace(package.Repo) ||
        !string.IsNullOrWhiteSpace(package.VersionRepo) ||
        package.Name.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Яндекс Браузер", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Opera", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Opera GX", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Tor Browser", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Vivaldi", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Ventoy", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Rufus", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("WinRAR", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("7-Zip", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Incy", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("Proxifier", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("LibreWolf", StringComparison.OrdinalIgnoreCase) ||
        package.Name.Equals("NVIDIA App", StringComparison.OrdinalIgnoreCase) ||
        package.IsSteam;

    /// <summary>Resolves a package version with bounded retries for transient network/source failures.
    /// Retries incomplete version responses as well as transient HTTP/transport failures.
    /// </summary>
    public static async Task<string> ResolveLatestVersionAsync(
        PackageItem package,
        HttpClient client,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3,
        TimeSpan? retryDelay = null)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var baseDelay = retryDelay ?? TimeSpan.FromMilliseconds(650);
        var cacheKey = BuildCacheKey(package);
        if (SuccessfulCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.StoredAt < SuccessfulCacheTtl)
        {
            AppLog.Write("VersionCheck", $"{package.Name}: использована недавно проверенная версия {cached.Version} (кэш до 5 минут).");
            return cached.Version;
        }

        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resolved = await ResolveLatestVersionOnceAsync(package, client, cancellationToken);
                if (!IsUnresolvedVersion(resolved))
                {
                    SuccessfulCache[cacheKey] = new VersionCacheEntry(resolved, DateTimeOffset.UtcNow);
                    return resolved;
                }

                if (attempt < maxAttempts)
                {
                    AppLog.Write("VersionCheck", $"{package.Name}: источник не вернул версию (попытка {attempt}/{maxAttempts}); повторяем запрос.");
                    await DelayBeforeRetryAsync(attempt, baseDelay, cancellationToken);
                    continue;
                }

                return resolved;
            }
            catch (Exception ex) when (IsRetryable(ex, package, cancellationToken))
            {
                lastException = ex;
                AppLog.Write("VersionCheck", $"{package.Name}: временная ошибка проверки (попытка {attempt}/{maxAttempts}): {ex.Message}");
                if (attempt >= maxAttempts) throw;
                await DelayBeforeRetryAsync(attempt, baseDelay, cancellationToken);
            }
        }

        if (lastException is not null) throw lastException;
        return UnknownVersion;
    }

    private static string BuildCacheKey(PackageItem package)
    {
        var repository = !string.IsNullOrWhiteSpace(package.VersionRepo) ? package.VersionRepo : package.Repo;
        return $"{package.Name}|{repository}|{package.WebsiteUrl}";
    }

    private sealed record VersionCacheEntry(string Version, DateTimeOffset StoredAt);

    private static async Task DelayBeforeRetryAsync(int attempt, TimeSpan baseDelay, CancellationToken cancellationToken)
    {
        var milliseconds = Math.Min(5000, baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
    }

    private static bool IsUnresolvedVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ||
        version.Equals(UnknownVersion, StringComparison.OrdinalIgnoreCase) ||
        version.Equals("Версия не указана", StringComparison.OrdinalIgnoreCase) ||
        version.Equals("Ошибка проверки", StringComparison.OrdinalIgnoreCase) ||
        version.Equals("Не проверена", StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryable(Exception exception, PackageItem package, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (exception is TaskCanceledException) return true; // HttpClient timeout, not caller cancellation.
        if (exception is IOException or JsonException) return true; // Interrupted stream or transient malformed response.
        if (exception is not HttpRequestException httpException) return false;
        if (httpException.StatusCode is null) return true; // DNS, socket, TLS, proxy or connection failure.

        var status = (int)httpException.StatusCode.Value;
        if (status is 408 or 425 or 429 or 500 or 502 or 503 or 504) return true;
        // GitHub's public API also reports exhausted rate limits as 403. Package repos in this catalog are public.
        return status == 403 && (!string.IsNullOrWhiteSpace(package.Repo) || !string.IsNullOrWhiteSpace(package.VersionRepo) ||
            package.Name.Equals("Incy", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ResolveLatestVersionOnceAsync(
        PackageItem package, HttpClient client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // These official sources must take precedence over a generic GitHub repository or an old direct URL.
        if (package.Name.Equals("Ventoy", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveVentoyAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("LibreWolf", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveLibreWolfAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("NVIDIA App", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveNvidiaAppAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("Incy", StringComparison.OrdinalIgnoreCase))
        {
            var release = await IncyReleaseResolver.ResolveLatestAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        var versionRepo = !string.IsNullOrWhiteSpace(package.VersionRepo) ? package.VersionRepo : package.Repo;
        if (!string.IsNullOrWhiteSpace(versionRepo))
        {
            using var response = await client.GetAsync(
                $"https://api.github.com/repos/{versionRepo}/releases/latest", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var tag = json.RootElement.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() : null;
            return string.IsNullOrWhiteSpace(tag) ? "Версия не указана" : tag!;
        }

        if (package.Name.Equals("Rufus", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveRufusAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("WinRAR", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveWinRarAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("7-Zip", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveSevenZipAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync(
                "https://versionhistory.googleapis.com/v1/chrome/platforms/win64/channels/stable/versions?pageSize=1&order_by=version%20desc",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0 &&
                versions[0].TryGetProperty("version", out var versionNode))
                return versionNode.GetString() ?? UnknownVersion;
            return UnknownVersion;
        }

        if (package.Name.Equals("Mozilla Firefox", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync("https://product-details.mozilla.org/1.0/firefox_versions.json", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.TryGetProperty("LATEST_FIREFOX_VERSION", out var versionNode)
                ? versionNode.GetString() ?? UnknownVersion : UnknownVersion;
        }

        if (package.Name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync("https://edgeupdates.microsoft.com/api/products?view=enterprise", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var product in json.RootElement.EnumerateArray())
            {
                if (!product.TryGetProperty("Product", out var productName) ||
                    !string.Equals(productName.GetString(), "Stable", StringComparison.OrdinalIgnoreCase) ||
                    !product.TryGetProperty("Releases", out var releases)) continue;
                foreach (var release in releases.EnumerateArray())
                {
                    var platform = release.TryGetProperty("Platform", out var platformNode) ? platformNode.GetString() : "";
                    var arch = release.TryGetProperty("Architecture", out var archNode) ? archNode.GetString() : "";
                    var version = release.TryGetProperty("ProductVersion", out var versionNode) ? versionNode.GetString() : null;
                    if (string.Equals(platform, "Windows", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(arch, "x64", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(version))
                        return version!;
                }
            }
            return UnknownVersion;
        }

        if (package.Name.Equals("Яндекс Браузер", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync(
                "https://browser.yandex.ru/download?os=win&bitness=64&lang=ru", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri;
            var match = finalUri is null ? Match.Empty : Regex.Match(finalUri.AbsolutePath,
                @"/(?<version>\d+(?:_\d+){2,})/", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["version"].Value.Replace('_', '.') : UnknownVersion;
        }

        if (package.Name.Equals("Opera", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync("https://get.opera.com/pub/opera/desktop/", cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseOperaDirectoryVersion(html) ?? UnknownVersion;
        }

        if (package.Name.Equals("Opera GX", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync("https://get.geo.opera.com/ftp/pub/opera_gx/", cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseOperaDirectoryVersion(html) ?? UnknownVersion;
        }

        if (package.Name.Equals("Tor Browser", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveTorBrowserAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("Vivaldi", StringComparison.OrdinalIgnoreCase))
        {
            var release = await BrowserDownloadResolver.ResolveVivaldiAsync(client, cancellationToken);
            return release?.Version ?? UnknownVersion;
        }

        if (package.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase))
        {
            // Official Discord desktop update manifest; host_version contains the Windows app version components.
            using var response = await client.GetAsync(
                "https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var manifest = await response.Content.ReadAsStringAsync(cancellationToken);
            var version = ParseDiscordManifestVersion(manifest);
            if (version is null)
            {
                AppLog.Write("VersionCheck", "В официальном манифесте Discord не найден full.host_version.");
                return UnknownVersion;
            }
            return version;
        }

        if (package.Name.Equals("Proxifier", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await client.GetAsync(ProxifierDownloadPage, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var version = ParseProxifierVersion(html);
            if (version is null)
            {
                AppLog.Write("VersionCheck", "На официальной странице Proxifier не найден паттерн версии Windows Standard Edition; источник нужно проверить.");
                return UnknownVersion;
            }
            return version;
        }

        if (package.IsSteam)
        {
            // The Valve CDN client manifest publishes the stable Windows client build number as `version`.
            // steam.inf's ClientVersion/ClientVersionSteam uses the matching numeric build identifier.
            var url = SteamClientManifestUrl + "?__TIME__=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var manifest = await response.Content.ReadAsStringAsync(cancellationToken);
            var version = ParseSteamManifestVersion(manifest);
            if (version is null)
            {
                AppLog.Write("VersionCheck", "В официальном манифесте Steam не найден числовой ключ version.");
                return UnknownVersion;
            }
            return version;
        }

        return UnknownVersion;
    }

    /// <summary>Parses the highest versioned directory from Opera's official public release index.</summary>
    public static string? ParseOperaDirectoryVersion(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        html = WebUtility.HtmlDecode(html);
        var matches = Regex.Matches(html,
            @"(?:href\s*=\s*[""']?)(?<version>\d+\.\d+\.\d+\.\d+)/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        Version? highest = null;
        string? highestText = null;
        foreach (Match match in matches)
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

    /// <summary>Extracts the official Discord Windows application version from its update manifest.</summary>
    public static string? ParseDiscordManifestVersion(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        try
        {
            using var json = JsonDocument.Parse(manifest);
            if (!json.RootElement.TryGetProperty("full", out var full) ||
                !full.TryGetProperty("host_version", out var version)) return null;

            if (version.ValueKind == JsonValueKind.Array)
            {
                var components = new List<string>();
                foreach (var item in version.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                        components.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed))
                        components.Add(parsed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    else return null;
                }
                return components.Count > 0 ? string.Join(".", components) : null;
            }

            if (version.ValueKind == JsonValueKind.String)
            {
                var text = version.GetString();
                return VersionComparison.Normalize(text);
            }
        }
        catch (JsonException ex) { AppLog.Write("VersionCheck", "Не удалось разобрать манифест Discord.", ex); }
        return null;
    }

    /// <summary>Extracts only the Windows Standard Edition version, not the Mac/Android versions on the same page.</summary>
    public static string? ParseProxifierVersion(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var section = Regex.Match(text,
            @"Proxifier\s+Standard\s+Edition\s+v4[\s\S]{0,300}?Version\s*:?\s*v?(?<version>\d+(?:\.\d+){1,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (section.Success) return section.Groups["version"].Value;

        // Some page revisions may omit the edition heading while retaining the exact Windows release label.
        var windows = Regex.Match(text,
            @"Proxifier\s+Standard\s+Edition[\s\S]{0,300}?Version\s*:?\s*v?(?<version>\d+(?:\.\d+){1,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return windows.Success ? windows.Groups["version"].Value : null;
    }

    /// <summary>Parses Valve's KeyValues-style Steam client manifest.</summary>
    public static string? ParseSteamManifestVersion(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        var match = Regex.Match(manifest,
            @"(?im)^\s*""?version""?\s+""?(?<version>\d+)""?\s*$",
            RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups["version"].Value;

        // Also accept a JSON-style manifest if Valve changes its serialization.
        match = Regex.Match(manifest, @"""version""\s*:\s*""?(?<version>\d+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : null;
    }
}
