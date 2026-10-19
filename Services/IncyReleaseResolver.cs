using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoolSnesh.Avalonia.Services;

public sealed record IncyRelease(string Version, string DownloadUrl, string FileName);

/// <summary>Resolves the desktop Windows installer from INCY's official release manifest.</summary>
public static class IncyReleaseResolver
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/INCY-DEV/incy-platforms/main/RELEASE.json";
    private const string ExpectedAssetName = "incy-windows-setup.exe";
    private const string ExpectedAssetPath = "/INCY-DEV/incy-platforms/releases/download/";

    public static async Task<IncyRelease?> ResolveLatestAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseManifest(json);
    }

    public static IncyRelease? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("desktop", out var desktop) || desktop.ValueKind != JsonValueKind.Object ||
                !desktop.TryGetProperty("version", out var versionNode) || versionNode.ValueKind != JsonValueKind.String ||
                !desktop.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Object ||
                !windows.TryGetProperty("x64", out var urlNode) || urlNode.ValueKind != JsonValueKind.String)
                return null;

            var version = versionNode.GetString()?.Trim();
            var rawUrl = urlNode.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(version) ||
                !Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant) ||
                !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith(ExpectedAssetPath, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(uri.AbsolutePath).Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase))
                return null;

            return new IncyRelease(version, uri.ToString(), ExpectedAssetName);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
