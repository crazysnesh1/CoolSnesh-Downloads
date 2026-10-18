using CoolSnesh.Avalonia.Models;
using CoolSnesh.Avalonia.Services;
using Xunit;

namespace CoolSnesh.Avalonia.Tests;

public sealed class VersionComparisonTests
{
    [Theory]
    [InlineData("1.11.2", "1.11.2.0")]
    [InlineData("1.11.2.0", "1.11.2.0.0")]
    [InlineData("release-5.2.4", "5.2.4")]
    [InlineData("3.3.6(591)", "3.3.6")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("ver. 1.2.3", "1.2.3")]
    [InlineData("version 1.2.3", "1.2.3")]
    [InlineData("1.2.3+build.77", "1.2.3")]
    [InlineData("v1.11.2.0.0", "1.11.2")]
    public void EquivalentVersionsCompareAsEqual(string left, string right) =>
        Assert.Equal<int?>(0, VersionComparison.Compare(left, right));

    [Fact]
    public void ThreeAndFourSegmentProxyVersionsAreCurrent()
    {
        var package = new PackageItem { Name = "TG WS Proxy", Version = "1.11.2" };
        var status = InstalledVersionService.FormatStatus(package, new InstalledInfo(true, "1.11.2.0", ""));
        Assert.Equal("Актуально · 1.11.2", status);
    }

    [Fact]
    public void QbittorrentReleasePrefixDoesNotHideUpgrade()
    {
        var package = new PackageItem { Name = "qBittorrent (x64)", Version = "release-5.2.4" };
        var status = InstalledVersionService.FormatStatus(package, new InstalledInfo(true, "5.2.3", ""));
        Assert.Equal("Доступно обновление 5.2.3 → 5.2.4", status);
    }

    [Fact]
    public void HappVersionReportsUpgrade()
    {
        Assert.Equal<int?>(-1, VersionComparison.Compare("3.3.6(591)", "4.5.4"));
        var package = new PackageItem { Name = "Happ", Version = "4.5.4" };
        var status = InstalledVersionService.FormatStatus(package, new InstalledInfo(true, "3.3.6(591)", ""));
        Assert.Equal("Доступно обновление 3.3.6 → 4.5.4", status);
    }

    [Theory]
    [InlineData("", "1.2.3")]
    [InlineData(" ", "1.2.3")]
    [InlineData("1.2.x", "1.2.3")]
    [InlineData("not-a-version", "1.2.3")]
    [InlineData("1.2.3", "")]
    public void InvalidVersionsCannotBeCompared(string left, string right) =>
        Assert.Null(VersionComparison.Compare(left, right));

    [Fact]
    public void UnknownLatestVersionIsNotRepeatedInStatusLine()
    {
        var package = new PackageItem { Name = "Discord", Version = "Не определяется источником" };
        var status = InstalledVersionService.FormatStatus(package, new InstalledInfo(true, "1.2.3", ""));
        Assert.Equal("Установлено: 1.2.3", status);
        Assert.DoesNotContain("актуальная версия источником не предоставляется", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogSourceCoverageIsExplicitForInstallablePackages()
    {
        var packages = PackageCatalog.CreateDefault();
        Assert.Equal(38, packages.Count);

        // Download-only payloads (ISOs, runtimes and portable utilities) do not need an installed-app
        // update source. Every normal installable app should either have a latest-version source or be
        // explicitly known as an unsupported source.
        var installable = packages.Where(p => !p.IsDownloadOnly).ToList();
        Assert.Equal(28, installable.Count);
        var withSource = installable.Where(VersionResolver.HasKnownSource).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(24, withSource.Count);
        Assert.Contains("Discord", withSource);
        Assert.Contains("Proxifier", withSource);
        Assert.Contains("Steam", withSource);
        Assert.Contains("Google Chrome", withSource);
        Assert.Contains("Opera", withSource);
        Assert.Contains("Opera GX", withSource);
        Assert.Contains("LibreWolf", withSource);
        Assert.Contains("NVIDIA App", withSource);

        var withoutSource = installable.Where(p => !VersionResolver.HasKnownSource(p)).Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "Battle.net", "EA app", "Epic Games Launcher", "Ubisoft Connect" }.OrderBy(x => x), withoutSource);
    }

    [Fact]
    public void BrowserCategoryContainsPopularBrowsersWithOfficialSitesAndDescriptions()
    {
        var browsers = PackageCatalog.CreateDefault().Where(p => p.Category == "Браузеры").ToList();
        Assert.Equal(10, browsers.Count);
        Assert.Contains(browsers, p => p.Name == "Google Chrome");
        Assert.Contains(browsers, p => p.Name == "Mozilla Firefox");
        Assert.Contains(browsers, p => p.Name == "Яндекс Браузер");
        Assert.Contains(browsers, p => p.Name == "Opera");
        Assert.All(browsers, p =>
        {
            Assert.StartsWith("https://", p.WebsiteUrl, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(p.IconUrl));
            Assert.False(string.IsNullOrWhiteSpace(p.TooltipDescription));
        });
    }

    [Theory]
    [InlineData("<a href=\"137.0.6036.63/\">137.0.6036.63/</a><a href=\"136.0.5947.53/\">old</a>", "137.0.6036.63")]
    [InlineData("<a href='128.0.5644.60/'>128.0.5644.60/</a>", "128.0.5644.60")]
    [InlineData("Index of releases without any version folders", null)]
    public void OperaDirectoryParserSelectsHighestPublishedVersion(string html, string? expected) =>
        Assert.Equal(expected, VersionResolver.ParseOperaDirectoryVersion(html));

    [Fact]
    public void TorBrowserParserFindsOfficialWindowsX64Installer()
    {
        const string html = "<a href=\"https://dist.torproject.org/torbrowser/15.0.25/tor-browser-windows-x86_64-portable-15.0.25.exe\">Download</a>";
        var release = BrowserDownloadResolver.ParseTorBrowserDownloadPage(html);
        Assert.NotNull(release);
        Assert.Equal("15.0.25", release!.Version);
        Assert.Equal("tor-browser-windows-x86_64-portable-15.0.25.exe", release.FileName);
        Assert.StartsWith("https://dist.torproject.org/", release.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TorBrowserParserRejectsNonOfficialHosts()
    {
        const string html = "<a href=\"https://not-torproject.org/torbrowser/15.0.25/tor-browser-windows-x86_64-portable-15.0.25.exe\">Download</a>";
        Assert.Null(BrowserDownloadResolver.ParseTorBrowserDownloadPage(html));
    }

    [Fact]
    public void VivaldiParserFindsOfficialVersionedInstaller()
    {
        const string html = "<a href=\"https://downloads.vivaldi.com/stable/Vivaldi.8.2.4133.87.x64.exe\">Windows x64</a>";
        var release = BrowserDownloadResolver.ParseVivaldiDownloadPage(html);
        Assert.NotNull(release);
        Assert.Equal("8.2.4133.87", release!.Version);
        Assert.Equal("Vivaldi.8.2.4133.87.x64.exe", release.FileName);
    }

    [Fact]
    public void DiscordParserReadsWindowsHostVersion()
    {
        const string manifest = "{\"full\":{\"host_version\":[1,0,9234]}}";
        Assert.Equal("1.0.9234", VersionResolver.ParseDiscordManifestVersion(manifest));
    }

    [Fact]
    public void ProxifierParserReadsWindowsStandardEditionOnly()
    {
        const string html = "<h2>Proxifier Standard Edition v4</h2><p>Version <a>4.14</a>, 2025.04.23</p>" +
                            "<h2>Proxifier for Mac v3</h2><p>Version 3.15, 2025.09.18</p>";
        Assert.Equal("4.14", VersionResolver.ParseProxifierVersion(html));
    }

    [Fact]
    public void SteamManifestParserReadsClientBuildNumber()
    {
        const string manifest = "\"win64\"\n{\n  \"version\" \"1782866176\"\n  \"ostype\" \"win10\"\n}";
        Assert.Equal("1782866176", VersionResolver.ParseSteamManifestVersion(manifest));
    }
}
