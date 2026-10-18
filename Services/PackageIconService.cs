using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Platform;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

/// <summary>Loads official-site favicons through Google's favicon endpoint and caches them locally.</summary>
public static class PackageIconService
{
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Bitmap> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoolSneshDownloads", "Icons");

    public static Task<Bitmap?> LoadAsync(PackageItem package, HttpClient client, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(package.IconAsset))
        {
            var assetKey = "asset:" + package.IconAsset;
            if (MemoryCache.TryGetValue(assetKey, out var localCached)) return Task.FromResult<Bitmap?>(localCached);
            return InFlight.GetOrAdd(assetKey, _ => LoadLocalAsync(package.IconAsset));
        }

        var url = package.IconUrl;
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);
        if (MemoryCache.TryGetValue(url, out var cached)) return Task.FromResult<Bitmap?>(cached);
        return InFlight.GetOrAdd(url, _ => LoadCoreAsync(url, client, cancellationToken));
    }

    private static async Task<Bitmap?> LoadLocalAsync(string assetName)
    {
        var key = "asset:" + assetName;
        try
        {
            var safeName = Path.GetFileName(assetName);
            if (!safeName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Встроенная иконка должна быть PNG.");
            var assetUri = new Uri($"avares://CoolSnesh.Avalonia/Assets/PackageIcons/{safeName}");
            byte[] bytes;
            using (var source = AssetLoader.Open(assetUri))
            using (var memory = new MemoryStream())
            {
                await source.CopyToAsync(memory);
                bytes = memory.ToArray();
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(stream));
            return MemoryCache.GetOrAdd(key, bitmap);
        }
        catch (Exception ex)
        {
            AppLog.Write("PackageIcon", $"Не удалось загрузить встроенную иконку {assetName}; будет показан запасной значок.", ex);
            return null;
        }
        finally { InFlight.TryRemove(key, out _); }
    }

    private static async Task<Bitmap?> LoadCoreAsync(string url, HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".iconcache";
            var filePath = Path.Combine(CacheDirectory, fileName);
            Bitmap bitmap;
            if (File.Exists(filePath))
            {
                try
                {
                    await using var cachedStream = File.OpenRead(filePath);
                    bitmap = await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(cachedStream));
                }
                catch
                {
                    try { File.Delete(filePath); } catch { /* stale cache cleanup is best-effort */ }
                    bitmap = await DownloadBitmapAsync(url, filePath, client, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Icon response was empty.");
                }
            }
            else
            {
                bitmap = await DownloadBitmapAsync(url, filePath, client, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Icon response was empty.");
            }
            return MemoryCache.GetOrAdd(url, bitmap);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            AppLog.Write("PackageIcon", $"Не удалось загрузить значок {url}; будет показан запасной значок.", ex);
            return null;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    private static async Task<Bitmap?> DownloadBitmapAsync(string url, string filePath, HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var isOfficialIcoAsset = Uri.TryCreate(url, UriKind.Absolute, out var iconUri) &&
                                 iconUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
                                 iconUri.AbsolutePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !isOfficialIcoAsset) return null;
        if (response.Content.Headers.ContentLength is > 2_000_000) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length is 0 or > 2_000_000) return null;

        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(bytes, writable: false);
        return await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(stream));
    }
}
