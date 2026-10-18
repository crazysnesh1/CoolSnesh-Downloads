using System.Net;
using CoolSnesh.Avalonia.Services;
using Xunit;

namespace CoolSnesh.Avalonia.Tests;

public sealed class NewCatalogItemsTests
{
    [Fact]
    public void Catalog_ContainsWindowsTabImagesAndUsbTools()
    {
        var packages = PackageCatalog.CreateDefault();
        var windows = packages.Where(p => p.Category == "Windows").Select(p => p.Name).ToArray();

        Assert.Contains("Windows 11 26H2", windows);
        Assert.Contains("Windows 10 22H2", windows);
        Assert.Contains("Ventoy", windows);
        Assert.Contains("Rufus", windows);
        Assert.Contains("Visual C++ Redistributable x64", windows);
        Assert.Contains("Visual C++ Redistributable x86", windows);
        Assert.Contains(".NET Framework 3.5", windows);
        Assert.Contains(".NET Framework 4.8", windows);
        Assert.Contains(".NET Framework 4.8.1", windows);
        Assert.Contains(".NET Desktop Runtime (x64)", windows);
        Assert.Contains("NVIDIA App", windows);
        Assert.All(packages.Where(p => p.IsIsoImage), p => Assert.True(p.IsDownloadOnly));
    }

    [Fact]
    public void Catalog_ContainsPopularArchiveToolsAndArtCraft()
    {
        var packages = PackageCatalog.CreateDefault();
        Assert.Contains(packages, p => p.Name == "7-Zip" && p.Category == "Программы");
        Assert.Contains(packages, p => p.Name == "WinRAR" && p.Category == "Программы");
        Assert.Contains(packages, p => p.Name == "ArtCraft Launcher" && p.Category == "Программы");
    }

    [Fact]
    public void GoogleDriveResolver_ExtractsSuppliedShareIds()
    {
        Assert.True(GoogleDriveDownloadResolver.TryExtractFileId(
            "https://drive.google.com/file/d/1HgFlMfvq4ADVXxxq54ngZ4A3hl2vkm9h/view?usp=sharing", out var win11));
        Assert.Equal("1HgFlMfvq4ADVXxxq54ngZ4A3hl2vkm9h", win11);

        Assert.True(GoogleDriveDownloadResolver.TryExtractFileId(
            "https://drive.google.com/file/d/1L1lr9F6yptai7-VMA7kC87KFMQR9cW8h/view?usp=sharing", out var win10));
        Assert.Equal("1L1lr9F6yptai7-VMA7kC87KFMQR9cW8h", win10);
    }

    [Fact]
    public void WinRar_Update_IsSilentAndRequestsElevationForMachineWideInstall()
    {
        var winrar = Assert.Single(PackageCatalog.CreateDefault(), p => p.Name == "WinRAR");
        Assert.Equal("/S", winrar.SilentArguments);
        Assert.True(winrar.RequiresElevation);
    }

    [Fact]
    public void InstallerService_DeletesTemporaryInstallerAfterUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "CoolSneshDownloads", "Installers");
        var job = Path.Combine(root, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var installer = Path.Combine(job, "installer.exe");
        File.WriteAllText(installer, "test installer placeholder");

        InstallerService.TryDeleteInstaller(installer);

        Assert.False(File.Exists(installer));
        Assert.False(Directory.Exists(job));
    }

    [Fact]
    public void BrowserDownloadResolver_ParsesVentoyLatestVersion()
    {
        var parsed = BrowserDownloadResolver.ParseVentoyDownloadPage(
            "<a href='ventoy-1.1.17-windows.zip'>ventoy-1.1.17-windows.zip</a><a href='ventoy-1.1.18-windows.zip'>ventoy-1.1.18-windows.zip</a>");

        Assert.NotNull(parsed);
        Assert.Equal("1.1.18", parsed!.Version);
        Assert.Contains("ventoy-1.1.18-windows.zip", parsed.DownloadUrl);
    }

    [Fact]
    public void BrowserDownloadResolver_ParsesRufusLatestVersion()
    {
        var parsed = BrowserDownloadResolver.ParseRufusDownloadPage(
            "<a href='rufus-4.14.exe'>rufus-4.14.exe</a><a href='rufus-4.15.exe'>rufus-4.15.exe</a><a href='rufus-4.15p.exe'>portable</a>");

        Assert.NotNull(parsed);
        Assert.Equal("4.15", parsed!.Version);
        Assert.EndsWith("rufus-4.15.exe", parsed.DownloadUrl);
    }

    [Fact]
    public void BrowserDownloadResolver_ParsesSevenZipAndWinRarVersions()
    {
        Assert.Equal("26.04", BrowserDownloadResolver.ParseSevenZipVersion("<title>7-Zip 26.04</title>"));
        Assert.Equal("7.23", BrowserDownloadResolver.ParseWinRarVersion(
            "<a href=\"https://www.rarlab.com/rar/winrar-x64-723.exe\">WinRAR x64 (64 bit) 7.23</a>"));
    }

    [Fact]
    public void UserProvidedSvgIconsAreMappedToPackages()
    {
        var packages = PackageCatalog.CreateDefault().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("chrome.png", packages["Google Chrome"].IconAsset);
        Assert.Equal("firefox.png", packages["Mozilla Firefox"].IconAsset);
        Assert.Equal("librewolf.png", packages["LibreWolf"].IconAsset);
        Assert.Equal("qbittorrent.png", packages["qBittorrent (x64)"].IconAsset);
        Assert.Equal("windows11.png", packages["Windows 11 26H2"].IconAsset);
        Assert.Equal("windows10.png", packages["Windows 10 22H2"].IconAsset);
        Assert.All(packages.Values.Where(p => !string.IsNullOrWhiteSpace(p.IconAsset)),
            p => Assert.EndsWith(".png", p.IconAsset, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TorBrowserShowsNetworkPrerequisite()
    {
        var tor = Assert.Single(PackageCatalog.CreateDefault(), p => p.Name == "Tor Browser");
        Assert.Contains("VPN", tor.DownloadNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("прокси", tor.DownloadNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(tor.DownloadNotice, tor.TooltipDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyEndpointParserSupportsHostPortLoginPasswordAndPasswordColons()
    {
        Assert.True(ProxyEndpointParser.TryParse("203.0.113.8:1080:my-login:my:secret:with:colons", out var parsed));
        Assert.Equal("203.0.113.8", parsed.Host);
        Assert.Equal(1080, parsed.Port);
        Assert.Equal("my-login", parsed.Username);
        Assert.Equal("my:secret:with:colons", parsed.Password);
        Assert.Null(parsed.Type);
    }

    [Theory]
    [InlineData("socks5://203.0.113.8:1080:user:pass", "SOCKS5")]
    [InlineData("https://proxy.example:443:user:pass", "HTTPS")]
    public void ProxyEndpointParserReadsOptionalScheme(string endpoint, string expectedType)
    {
        Assert.True(ProxyEndpointParser.TryParse(endpoint, out var parsed));
        Assert.Equal(expectedType, parsed.Type);
    }

    [Fact]
    public void ProxyEndpointParserRejectsInvalidPortOrIncompleteFields()
    {
        Assert.False(ProxyEndpointParser.TryParse("proxy:70000:user:pass", out _));
        Assert.False(ProxyEndpointParser.TryParse("proxy:80:user", out _));
        Assert.False(ProxyEndpointParser.TryParse("proxy:notaport:user:pass", out _));
    }

    [Fact]
    public void LibreWolfParserFindsOfficialWindowsX64InstallerAndVersion()
    {
        const string html = """
            <a href="https://dl.librewolf.net/librewolf/158.0-1/librewolf-158.0-1-windows-x86_64-setup.exe">Download librewolf-158.0-1-windows-x86_64-setup.exe</a>
            <a href="https://dl.librewolf.net/librewolf/158.0-1/librewolf-158.0-1-windows-arm64-setup.exe">ARM64</a>
            """;
        var parsed = BrowserDownloadResolver.ParseLibreWolfDownloadPage(html);
        Assert.NotNull(parsed);
        Assert.Equal("158.0-1", parsed!.Version);
        Assert.EndsWith("windows-x86_64-setup.exe", parsed.FileName);
        Assert.Contains("librewolf.net", new Uri(parsed.DownloadUrl).Host, StringComparison.OrdinalIgnoreCase);

        var relative = BrowserDownloadResolver.ParseLibreWolfDownloadPage(
            "<a href=\"/librewolf/157.0-1/librewolf-157.0-1-windows-x86_64-setup.exe\">Download</a>");
        Assert.NotNull(relative);
        Assert.Equal("https://dl.librewolf.net/librewolf/157.0-1/librewolf-157.0-1-windows-x86_64-setup.exe", relative!.DownloadUrl);
    }

    [Fact]
    public void VentoyParserUsesOfficialGithubReleaseAsset()
    {
        const string json = """
            {
              "tag_name": "v1.1.18",
              "assets": [
                {
                  "name": "ventoy-1.1.18-windows.zip",
                  "browser_download_url": "https://github.com/ventoy/Ventoy/releases/download/v1.1.18/ventoy-1.1.18-windows.zip"
                },
                {
                  "name": "ventoy-1.1.18-linux.tar.gz",
                  "browser_download_url": "https://github.com/ventoy/Ventoy/releases/download/v1.1.18/ventoy-1.1.18-linux.tar.gz"
                }
              ]
            }
            """;
        var parsed = BrowserDownloadResolver.ParseVentoyLatestReleaseJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("1.1.18", parsed!.Version);
        Assert.Equal("ventoy-1.1.18-windows.zip", parsed.FileName);
        Assert.Contains("github.com/ventoy/Ventoy/releases/download", parsed.DownloadUrl);
    }

    [Fact]
    public void NvidiaParserFindsCurrentOfficialInstallerUrl()
    {
        const string html = """
            <a href="https://us.download.nvidia.com/nvapp/client/11.0.9.565/NVIDIA_app_v11.0.9.565.exe">Download Now</a>
            """;
        var parsed = BrowserDownloadResolver.ParseNvidiaAppDownloadPage(html);
        Assert.NotNull(parsed);
        Assert.Equal("11.0.9.565", parsed!.Version);
        Assert.Equal("NVIDIA_app_v11.0.9.565.exe", parsed.FileName);
        Assert.Contains("download.nvidia.com", parsed.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZapretManualPathTakesPriorityOverAutoDetectedOldFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "CoolSneshTest-Zapret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        try
        {
            File.WriteAllText(Path.Combine(root, "service.bat"), "@echo off\r\nset \"LOCAL_VERSION=9.9.9\"\r\n");
            File.WriteAllText(Path.Combine(root, "bin", "winws.exe"), "test-placeholder");
            var package = PackageCatalog.CreateDefault().Single(p => p.Name == "zapret-discord-youtube");
            var detected = InstalledVersionService.Detect(package, root);
            Assert.True(detected.IsInstalled);
            Assert.Equal("9.9.9", detected.Version);
            Assert.Equal(Path.GetFullPath(root), detected.Path);
            Assert.Contains("ручной путь", detected.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temporary test cleanup */ }
        }
    }


    [Fact]
    public async Task VersionResolver_RetriesTransientServerErrorsAndSucceeds()
    {
        var attempts = 0;
        using var client = new HttpClient(new SequenceHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"tag_name\":\"v9.8.7\"}") };
        }));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CoolSneshTests/1.0");
        var package = new CoolSnesh.Avalonia.Models.PackageItem
        {
            Name = "Retry Test " + Guid.NewGuid().ToString("N"),
            Repo = "unit-tests/version-resolver",
            VersionRepo = "unit-tests/version-resolver",
            WebsiteUrl = "https://example.invalid/version-resolver-test"
        };

        var version = await VersionResolver.ResolveLatestVersionAsync(
            package, client, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        Assert.Equal("v9.8.7", version);
        Assert.Equal(2, attempts);

        // A second immediate check should use the recent successful cache, not risk another flaky request.
        var cachedVersion = await VersionResolver.ResolveLatestVersionAsync(package, client, retryDelay: TimeSpan.Zero);
        Assert.Equal("v9.8.7", cachedVersion);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void TorBrowserNotice_ContainsExplicitLineBreak()
    {
        var tor = Assert.Single(PackageCatalog.CreateDefault(), p => p.Name == "Tor Browser");
        Assert.Contains("\n", tor.DownloadNotice);
    }

    private sealed class SequenceHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

}
