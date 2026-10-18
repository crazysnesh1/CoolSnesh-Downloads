using CoolSnesh.Avalonia.Services;
using Xunit;

namespace CoolSnesh.Avalonia.Tests;

public sealed class AppUpdateServiceTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+abc123", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.0.4-beta.1+abc123", "1.0.4-beta.1")]
    public void DisplayVersionPreservesProductVersionSegments(string value, string expected) =>
        Assert.Equal(expected, AppUpdateService.FormatDisplayVersion(value));

    [Fact]
    public void DetectsNewerStableReleaseWithDirectExeAndChecksum()
    {
        const string json = """
        {
          "tag_name": "v0.1.1",
          "html_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/tag/v0.1.1",
          "name": "CoolSnesh Downloads v0.1.1",
          "body": "Bug fixes and improvements",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "CoolSnesh Downloads.exe",
              "browser_download_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/download/v0.1.1/CoolSnesh%20Downloads.exe"
            },
            {
              "name": "CoolSneshDownloads-win-x64.exe.sha256",
              "browser_download_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/download/v0.1.1/CoolSneshDownloads-win-x64.exe.sha256"
            }
          ]
        }
        """;

        var result = AppUpdateService.EvaluateLatestRelease("0.1.0", json);

        Assert.NotNull(result.Update);
        Assert.Equal("0.1.1", result.Update.Version);
        Assert.Equal("v0.1.1", result.Update.TagName);
        Assert.Equal("https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/tag/v0.1.1", result.Update.ReleaseUrl);
        Assert.EndsWith("CoolSnesh%20Downloads.exe", result.Update.ExecutableUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LatestReleaseVersionPreservesTrailingZeroSegmentsInDisplay()
    {
        var result = AppUpdateService.EvaluateLatestRelease("0.9.9", ReleaseJson("v1.0.0"));

        Assert.NotNull(result.Update);
        Assert.Equal("1.0.0", result.Update.Version);
        Assert.Equal("0.9.9", result.Update.CurrentVersion);
    }

    [Fact]
    public void DoesNotOfferUpdateWhenInstalledVersionMatchesLatestRelease()
    {
        var result = AppUpdateService.EvaluateLatestRelease("0.1.1", ReleaseJson("v0.1.1"));

        Assert.Null(result.Update);
        Assert.True(result.LatestVersionKnown);
        Assert.Contains("последняя", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotOfferUpdateWhenInstalledVersionIsNewerThanLatestRelease()
    {
        var result = AppUpdateService.EvaluateLatestRelease("0.2.0", ReleaseJson("v0.1.1"));

        Assert.Null(result.Update);
        Assert.True(result.LatestVersionKnown);
        Assert.Contains("новее", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotOfferUpdateForUntrustedExecutableHost()
    {
        var json = ReleaseJson("v0.1.1").Replace(
            "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/download/",
            "https://github-downloads.example/releases/download/",
            StringComparison.Ordinal);

        var result = AppUpdateService.EvaluateLatestRelease("0.1.0", json);

        Assert.Null(result.Update);
        Assert.Contains("отсутствует EXE", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotOfferUpdateWhenChecksumAssetIsMissing()
    {
        const string json = """
        {
          "tag_name": "v0.1.1",
          "html_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/tag/v0.1.1",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "CoolSnesh Downloads.exe",
              "browser_download_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/download/v0.1.1/CoolSnesh%20Downloads.exe"
            }
          ]
        }
        """;

        var result = AppUpdateService.EvaluateLatestRelease("0.1.0", json);

        Assert.Null(result.Update);
        Assert.Contains("SHA-256", result.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData("CoolSnesh Downloads.exe")]
    [InlineData("CoolSnesh.Downloads.exe")]
    [InlineData("CoolSnesh.Avalonia.exe")]
    [InlineData("CoolSneshDownloads.exe")]
    [InlineData("CoolSnesh-Downloads.exe")]
    public void AcceptsCurrentAndLegacyProductExecutableNames(string executableName)
    {
        Assert.True(AppUpdateService.IsSupportedExecutableFileName(executableName));
    }

    [Theory]
    [InlineData("dotnet.exe")]
    [InlineData("not-coolsnesh.exe")]
    [InlineData("")]
    public void RejectsUnsupportedExecutableNames(string executableName)
    {
        Assert.False(AppUpdateService.IsSupportedExecutableFileName(executableName));
    }

    [Fact]
    public void DetectsNewerReleaseWithLegacyDottedExecutableAssetName()
    {
        var json = ReleaseJson("v1.0.4")
            .Replace("CoolSnesh Downloads.exe", "CoolSnesh.Downloads.exe", StringComparison.Ordinal)
            .Replace("CoolSnesh%20Downloads.exe", "CoolSnesh.Downloads.exe", StringComparison.Ordinal);

        var result = AppUpdateService.EvaluateLatestRelease("1.0.3", json);

        Assert.NotNull(result.Update);
        Assert.Equal("1.0.4", result.Update.Version);
        Assert.EndsWith("CoolSnesh.Downloads.exe", result.Update.ExecutableUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReleaseJson(string tag) => $$"""
    {
      "tag_name": "{{tag}}",
      "html_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/tag/{{tag}}",
      "body": "",
      "draft": false,
      "prerelease": false,
      "assets": [
        {
          "name": "CoolSnesh Downloads.exe",
          "browser_download_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/download/{{tag}}/CoolSnesh%20Downloads.exe"
        },
        {
          "name": "CoolSneshDownloads-win-x64.exe.sha256",
          "browser_download_url": "https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/download/{{tag}}/CoolSneshDownloads-win-x64.exe.sha256"
        }
      ]
    }
    """;
}
