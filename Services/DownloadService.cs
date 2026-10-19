using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

public sealed record DownloadProgress(string Status, int Percent);

/// <summary>Downloads installers using the URL/release rules from the original launcher.</summary>
public sealed class DownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _concurrency;

    public DownloadService(ProxySettings? proxySettings = null, int maxParallelDownloads = 5)
    {
        _concurrency = new SemaphoreSlim(Math.Max(1, maxParallelDownloads));
        _httpClient = NetworkClientFactory.Create(proxySettings, TimeSpan.FromMinutes(30));
    }

    public async Task<string> DownloadAsync(
        PackageItem package,
        string folderPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(folderPath);
            progress.Report(new DownloadProgress("Получение ссылки на загрузку…", 0));

            string downloadUrl;
            string fileName;

            if (package.Name.Equals("Incy", StringComparison.OrdinalIgnoreCase))
            {
                var release = await IncyReleaseResolver.ResolveLatestAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("В официальном RELEASE.json INCY не найдена ссылка на установщик Windows x64.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("LibreWolf", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveLibreWolfAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице LibreWolf не найден актуальный установщик Windows x64. Открой librewolf.net/installation/windows/ и попробуй позже.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("NVIDIA App", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveNvidiaAppAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице NVIDIA не удалось определить ссылку на установщик NVIDIA App.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("Ventoy", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveVentoyAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице Ventoy не удалось определить последнюю версию Windows ZIP.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("Rufus", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveRufusAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице Rufus не удалось определить последнюю версию Windows.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("WinRAR", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveWinRarAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице RARLAB не удалось найти стабильный 64-битный установщик WinRAR.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("7-Zip", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveSevenZipAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальном сайте 7-Zip не удалось определить текущий установщик Windows x64.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("Tor Browser", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveTorBrowserAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице Tor Project не найден установщик Windows x64. Открой сайт проекта и попробуй позже.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (package.Name.Equals("Vivaldi", StringComparison.OrdinalIgnoreCase))
            {
                var release = await BrowserDownloadResolver.ResolveVivaldiAsync(_httpClient, cancellationToken)
                    ?? throw new InvalidOperationException("На официальной странице Vivaldi не найден установщик Windows x64. Открой сайт проекта и попробуй позже.");
                downloadUrl = release.DownloadUrl;
                fileName = release.FileName;
            }
            else if (!string.IsNullOrWhiteSpace(package.DirectUrl))
            {
                if (GoogleDriveDownloadResolver.TryExtractFileId(package.DirectUrl, out _))
                    downloadUrl = await GoogleDriveDownloadResolver.ResolveDirectDownloadUrlAsync(_httpClient, package.DirectUrl, cancellationToken);
                else
                    downloadUrl = package.DirectUrl;
                fileName = ResolveFileName(package.DirectUrl, package.FixedFileName, package.OutputName);
            }
            else if (!string.IsNullOrWhiteSpace(package.Repo))
            {
                var releaseUrl = $"https://api.github.com/repos/{package.Repo}/releases/latest";
                using var response = await _httpClient.GetAsync(releaseUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(jsonStream, cancellationToken: cancellationToken);

                var assets = release?.Assets ?? new List<GitHubAsset>();
                var asset = assets.FirstOrDefault(a => IsMatch(a.Name, package.FilePattern) &&
                    (string.IsNullOrWhiteSpace(package.Exclude) || !IsMatch(a.Name, package.Exclude)));
                if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                    throw new InvalidOperationException("В последнем релизе GitHub не найден файл, подходящий под заданную маску.");

                downloadUrl = asset.BrowserDownloadUrl;
                fileName = Path.GetFileName(asset.Name);
            }
            else
            {
                throw new InvalidOperationException("Для этого приложения не настроен источник загрузки.");
            }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = string.IsNullOrWhiteSpace(package.OutputName) ? "installer.bin" : package.OutputName;

            var safeName = Path.GetFileName(fileName);
            // Never truncate an existing app executable when the user also chose its directory for downloads.
            var outputPath = GetNonOverwritingOutputPath(folderPath, safeName);
            progress.Report(new DownloadProgress("Скачивание…", 0));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                using var responseDownload = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                responseDownload.EnsureSuccessStatusCode();
                if (package.IsIsoImage && string.Equals(responseDownload.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Google Drive вернул HTML-страницу вместо ISO. Проверь доступ к файлу по ссылке и повтори загрузку.");

                var totalBytes = responseDownload.Content.Headers.ContentLength;
                await using var input = await responseDownload.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true);
                var buffer = new byte[262144];
                long totalRead = 0;
                var lastReport = DateTime.UtcNow;
                int lastPercent = -1;

                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;

                    if (totalBytes is > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        var percent = (int)Math.Clamp(totalRead * 100d / totalBytes.Value, 0, 99);
                        if (percent != lastPercent)
                        {
                            progress.Report(new DownloadProgress("Скачивание…", percent));
                            lastPercent = percent;
                        }
                        lastReport = DateTime.UtcNow;
                    }
                }

                progress.Report(new DownloadProgress("Готово", 100));
                return outputPath;
            }
            catch
            {
                // Do not leave a partial installer behind after a failed or cancelled download.
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* Best-effort cleanup. */ }
                throw;
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static string GetNonOverwritingOutputPath(string folderPath, string safeName)
    {
        var path = Path.Combine(folderPath, safeName);
        if (!File.Exists(path)) return path;
        var extension = Path.GetExtension(safeName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? DateTime.Now.ToString("yyyyMMdd-HHmmss") : DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + attempt;
            path = Path.Combine(folderPath, $"{stem}-download-{suffix}{extension}");
            if (!File.Exists(path)) return path;
        }
        throw new IOException("Не удалось подобрать свободное имя для скачиваемого файла.");
    }

    private static string ResolveFileName(string url, string fixedName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(fixedName)) return fixedName;
        try
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            if (!string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                return name;
        }
        catch (UriFormatException) { }
        return fallback;
    }

    private static bool IsMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        try { return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return false; }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _concurrency.Dispose();
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
