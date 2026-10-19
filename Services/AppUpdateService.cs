using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace CoolSnesh.Avalonia.Services;

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string Version,
    string TagName,
    string ReleaseUrl,
    string ReleaseNotes,
    Uri ExecutableUrl,
    Uri ChecksumUrl);

public sealed record AppUpdateCheckResult(
    AppUpdateInfo? Update,
    string Message,
    bool LatestVersionKnown);

public sealed record AppUpdateLaunchResult(bool Started, string Message);

/// <summary>
/// Checks the public GitHub Releases API and safely stages a self-update.
/// The running EXE is never overwritten by the main process: a separate PowerShell
/// process waits for the app to exit, verifies the staged file again, swaps it and
/// restores the previous executable if startup fails.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    public const string RepositoryOwner = "crazysnesh1";
    public const string RepositoryName = "CoolSnesh-Downloads";
    // Canonical release filename plus historical names used by earlier releases.
    // GitHub preserves uploaded asset filenames exactly; the updater accepts these
    // known aliases while new releases are normalized to ExecutableAssetName.
    public const string ExecutableAssetName = "CoolSnesh Downloads.exe";
    public const string LegacyExecutableAssetName = "CoolSnesh.Downloads.exe";
    public const string AssemblyExecutableAssetName = "CoolSnesh.Avalonia.exe";
    public const string LegacyCompactExecutableAssetName = "CoolSneshDownloads.exe";
    public const string LegacyHyphenExecutableAssetName = "CoolSnesh-Downloads.exe";
    public const string ExecutableChecksumAssetName = "CoolSneshDownloads-win-x64.exe.sha256";
    private const string LatestReleaseApi = "https://api.github.com/repos/crazysnesh1/CoolSnesh-Downloads/releases/latest";
    private const long MinimumExecutableSize = 1_000_000;
    private const long MaximumExecutableSize = 1_500_000_000;
    private readonly HttpClient _httpClient;

    public AppUpdateService(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppUpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Do not use VersionComparison.Normalize() for the app's displayed version:
        // that helper intentionally trims trailing zero components for package comparisons,
        // so 1.0.0 would become 1. Preserve the product version's segment count here.
        return FormatDisplayVersion(informational)
            ?? FormatDisplayVersion(assembly.GetName().Version?.ToString())
            ?? "1.0.1";
    }

    /// <summary>
    /// Formats the product version for UI/logs while preserving trailing zero components
    /// (for example, 1.0.0 stays 1.0.0). Assembly version's fourth revision component is
    /// removed when it is the default zero (1.0.0.0 becomes 1.0.0). Build metadata is not shown.
    /// </summary>
    public static string? FormatDisplayVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = Regex.Match(
            value.Trim(),
            @"^(?:v)?(?<core>\d+(?:\.\d+){1,3})(?<prerelease>-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var parts = match.Groups["core"].Value.Split('.');
        if (parts.Length == 4 && parts[3] == "0")
            parts = parts[..3];

        var core = string.Join(".", parts);
        return core + match.Groups["prerelease"].Value;
    }

    public async Task<AppUpdateCheckResult> CheckLatestAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new AppUpdateCheckResult(null, "На GitHub пока нет опубликованного стабильного релиза.", false);
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            return new AppUpdateCheckResult(null, "GitHub временно ограничил запросы. Проверка обновления будет повторена позже.", false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return EvaluateLatestRelease(currentVersion, json);
    }

    /// <summary>Pure release metadata evaluation so it can be covered by unit tests.</summary>
    public static AppUpdateCheckResult EvaluateLatestRelease(string currentVersion, string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            var root = document.RootElement;
            var tagName = GetString(root, "tag_name");
            var releaseUrl = GetString(root, "html_url");
            var notes = GetString(root, "body") ?? string.Empty;
            var draft = GetBoolean(root, "draft");
            var prerelease = GetBoolean(root, "prerelease");

            if (draft || prerelease)
                return new AppUpdateCheckResult(null, "Последний релиз помечен как черновик или предварительная версия.", false);
            if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(releaseUrl))
                return new AppUpdateCheckResult(null, "GitHub вернул неполные сведения о релизе.", false);

            // Keep comparison normalization (which trims trailing zero components) separate
            // from user-facing formatting so releases like v1.0.0 are displayed as 1.0.0, not 1.
            var latestVersion = FormatDisplayVersion(tagName);
            var displayCurrentVersion = FormatDisplayVersion(currentVersion);
            var comparison = VersionComparison.Compare(currentVersion, tagName);
            if (latestVersion is null || displayCurrentVersion is null || comparison is null)
                return new AppUpdateCheckResult(null, "Не удалось сравнить текущую версию с версией GitHub.", false);

            if (comparison == 0)
                return new AppUpdateCheckResult(null, $"Установлена последняя опубликованная версия ({displayCurrentVersion}).", true);

            if (comparison > 0)
                return new AppUpdateCheckResult(
                    null,
                    $"Установленная версия ({displayCurrentVersion}) новее последнего опубликованного релиза ({latestVersion}). Обновление не требуется.",
                    true);

            if (!IsReleasePageUrl(releaseUrl))
                return new AppUpdateCheckResult(null, "GitHub вернул неожиданную ссылку на страницу релиза.", false);

            var executableUrl = FindAssetUrl(root, ExecutableAssetName)
                ?? FindAssetUrl(root, LegacyExecutableAssetName)
                ?? FindAssetUrl(root, AssemblyExecutableAssetName)
                ?? FindAssetUrl(root, LegacyCompactExecutableAssetName)
                ?? FindAssetUrl(root, LegacyHyphenExecutableAssetName);
            var checksumUrl = FindAssetUrl(root, ExecutableChecksumAssetName);
            if (executableUrl is null || checksumUrl is null)
                return new AppUpdateCheckResult(null,
                    $"Найдена версия {latestVersion}, но в релизе отсутствует EXE или его SHA-256. Переход к релизу: {releaseUrl}", false);

            return new AppUpdateCheckResult(
                new AppUpdateInfo(displayCurrentVersion, latestVersion, tagName, releaseUrl, notes, executableUrl, checksumUrl),
                $"Доступна новая версия {latestVersion}.",
                true);
        }
        catch (JsonException)
        {
            return new AppUpdateCheckResult(null, "Ответ GitHub не удалось разобрать как JSON.", false);
        }
        catch (InvalidOperationException)
        {
            return new AppUpdateCheckResult(null, "Ответ GitHub имеет неожиданную структуру.", false);
        }
    }

    /// <summary>
    /// True for the product executable names used by current and earlier releases.
    /// Kept public so the compatibility rule is covered by unit tests.
    /// </summary>
    public static bool IsSupportedExecutableFileName(string? executablePathOrName)
    {
        if (string.IsNullOrWhiteSpace(executablePathOrName)) return false;
        var name = Path.GetFileName(executablePathOrName.Trim());
        return new[]
        {
            ExecutableAssetName,
            LegacyExecutableAssetName,
            AssemblyExecutableAssetName,
            LegacyCompactExecutableAssetName,
            LegacyHyphenExecutableAssetName
        }.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsFrameworkDependentLaunch(string currentExecutablePath)
    {
        var processName = Path.GetFileName(currentExecutablePath);
        if (processName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        // In a single-file publish Assembly.Location is empty. A populated DLL path
        // indicates a framework-dependent/dev launch and should not replace the EXE.
        var entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;
        return !string.IsNullOrWhiteSpace(entryAssemblyLocation) &&
               Path.GetExtension(entryAssemblyLocation).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AppUpdateLaunchResult> DownloadAndScheduleUpdateAsync(
        AppUpdateInfo update,
        string currentExecutablePath,
        int currentProcessId,
        CancellationToken cancellationToken = default)
    {
        if (update is null) return Fail("Информация об обновлении отсутствует.");
        if (string.IsNullOrWhiteSpace(currentExecutablePath) || !File.Exists(currentExecutablePath))
            return Fail("Не удалось определить запущенный EXE приложения.");
        if (IsFrameworkDependentLaunch(currentExecutablePath))
            return Fail("Самообновление доступно только для автономной опубликованной сборки EXE. Запусти файл из релиза GitHub, а не через dotnet run.");
        if (!IsSupportedExecutableFileName(currentExecutablePath))
            return Fail($"Не распознано имя EXE «{Path.GetFileName(currentExecutablePath)}». Поддерживаются опубликованные сборки CoolSnesh Downloads; закрой приложение и запусти корректный EXE из релиза.");
        if (currentProcessId <= 0) return Fail("Не удалось определить процесс приложения.");
        if (!IsTrustedAssetUrl(update.ExecutableUrl) || !IsTrustedAssetUrl(update.ChecksumUrl))
            return Fail("Ссылка на обновление не прошла проверку доверенного домена GitHub.");

        string stagingDirectory = Path.Combine(
            Path.GetTempPath(), "CoolSneshDownloads", "updates",
            Regex.Replace(update.TagName, "[^A-Za-z0-9._-]", "_"));
        string stagedExecutable = Path.Combine(stagingDirectory, ExecutableAssetName);
        string checksumFile = Path.Combine(stagingDirectory, ExecutableChecksumAssetName);
        string scriptPath = Path.Combine(stagingDirectory, "apply-update.ps1");

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            TryDelete(stagedExecutable);
            TryDelete(checksumFile);
            TryDelete(scriptPath);

            await DownloadToFileAsync(update.ExecutableUrl, stagedExecutable, cancellationToken);
            var fileInfo = new FileInfo(stagedExecutable);
            if (fileInfo.Length < MinimumExecutableSize || fileInfo.Length > MaximumExecutableSize)
                throw new InvalidDataException($"Размер загруженного EXE выглядит некорректным ({fileInfo.Length:N0} байт).");

            var checksumText = await DownloadTextAsync(update.ChecksumUrl, cancellationToken);
            var match = Regex.Match(checksumText, @"(?i)\b(?<hash>[0-9a-f]{64})\b");
            if (!match.Success) throw new InvalidDataException("Файл SHA-256 имеет неверный формат.");
            var expectedHash = match.Groups["hash"].Value.ToLowerInvariant();

            string actualHash;
            await using (var stream = new FileStream(stagedExecutable, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true))
            using (var sha = SHA256.Create())
            {
                var hashBytes = await sha.ComputeHashAsync(stream, cancellationToken);
                actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(expectedHash)))
                throw new InvalidDataException("SHA-256 загруженного EXE не совпадает с опубликованной контрольной суммой. Обновление отменено.");

            await File.WriteAllTextAsync(scriptPath, UpdateScript, new UTF8Encoding(false), cancellationToken);

            var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(currentExecutablePath))!;
            var needsElevation = !CanWriteDirectory(targetDirectory);
            var scriptArguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass" +
                $" -File {QuoteArgument(scriptPath)}" +
                $" -ProcessId {currentProcessId}" +
                $" -SourcePath {QuoteArgument(stagedExecutable)}" +
                $" -TargetPath {QuoteArgument(Path.GetFullPath(currentExecutablePath))}" +
                $" -ExpectedSha256 {QuoteArgument(expectedHash)}";

            var startInfo = new ProcessStartInfo
            {
                FileName = GetPowerShellPath(),
                Arguments = scriptArguments,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = stagingDirectory
            };
            if (needsElevation) startInfo.Verb = "runas";

            try
            {
                var process = Process.Start(startInfo);
                if (process is null) throw new InvalidOperationException("Windows не запустил помощник обновления.");
                process.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                CleanupStaging(stagedExecutable, checksumFile, scriptPath, stagingDirectory);
                return Fail("Запрос прав администратора отменён. Текущая версия приложения не изменена.");
            }

            AppLog.Write("AppUpdate", $"Подготовлено обновление до {update.Version}; исходный EXE: {currentExecutablePath}; требуется UAC: {needsElevation}.");
            return new AppUpdateLaunchResult(true, "Обновление проверено и подготовлено. Приложение закроется, заменит EXE и запустится снова.");
        }
        catch (OperationCanceledException)
        {
            CleanupStaging(stagedExecutable, checksumFile, scriptPath, stagingDirectory);
            return Fail("Загрузка обновления отменена. Установленная версия не изменена.");
        }
        catch (Exception ex)
        {
            CleanupStaging(stagedExecutable, checksumFile, scriptPath, stagingDirectory);
            AppLog.Write("AppUpdate", "Не удалось подготовить обновление CoolSnesh Downloads.", ex);
            return Fail("Не удалось подготовить обновление: " + ex.Message);
        }
    }

    private async Task DownloadToFileAsync(Uri uri, string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        await input.CopyToAsync(output, 131072, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private async Task<string> DownloadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length > 4096) throw new InvalidDataException("Файл контрольной суммы неожиданно большой.");
        return text;
    }

    private static Uri? FindAssetUrl(JsonElement root, string assetName)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(GetString(asset, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                continue;
            var rawUrl = GetString(asset, "browser_download_url");
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && IsTrustedAssetUrl(uri))
                return uri;
            return null;
        }
        return null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static bool IsReleasePageUrl(string rawUrl) =>
        Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{RepositoryOwner}/{RepositoryName}/releases/tag/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedAssetUrl(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{RepositoryOwner}/{RepositoryName}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static string GetPowerShellPath()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    private static bool CanWriteDirectory(string directory)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, ".coolsnesh-write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            if (probe is not null) TryDelete(probe);
            return false;
        }
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void CleanupStaging(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
            }
            catch (Exception ex) { AppLog.Write("AppUpdate", $"Не удалось очистить временный путь {path}.", ex); }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* cleanup is best-effort; the updater will remove staging files later */ }
    }

    private static AppUpdateLaunchResult Fail(string message) => new(false, message);

    public void Dispose() => _httpClient.Dispose();

    private const string UpdateScript = """
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$TargetPath,
    [Parameter(Mandatory = $true)][string]$ExpectedSha256
)
$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path $env:APPDATA 'CoolSneshDownloads\logs'
$backupDirectory = Join-Path $env:LOCALAPPDATA 'CoolSneshDownloads\UpdateBackups'
New-Item -ItemType Directory -Force -Path $logDirectory, $backupDirectory | Out-Null
$logPath = Join-Path $logDirectory 'updater.log'
function Write-Log([string]$Message) {
    Add-Content -LiteralPath $logPath -Value ("[{0:u}] {1}" -f (Get-Date), $Message) -Encoding UTF8
}
$swapPath = $null
$backupPath = $null
$stagedTarget = $null
try {
    Write-Log "Updater started. Target=$TargetPath; PID=$ProcessId"
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
    }
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        throw 'The application did not exit within 90 seconds. Update was not applied.'
    }
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) { throw 'The downloaded update file is missing.' }
    $sourceHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $ExpectedSha256.ToLowerInvariant()) { throw 'SHA-256 validation failed in the updater process.' }
    if (-not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) { throw 'The current executable was not found.' }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $backupDirectory ("CoolSnesh-Downloads-{0}.exe" -f $stamp)
    Copy-Item -LiteralPath $TargetPath -Destination $backupPath -Force
    $stagedTarget = Join-Path (Split-Path -Parent $TargetPath) ('.coolsnesh-update-' + [Guid]::NewGuid().ToString('N') + '.exe')
    Copy-Item -LiteralPath $SourcePath -Destination $stagedTarget -Force
    $stagedHash = (Get-FileHash -LiteralPath $stagedTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($stagedHash -ne $ExpectedSha256.ToLowerInvariant()) { throw 'The staged executable failed SHA-256 validation.' }

    $swapPath = $TargetPath + '.swap-' + [Guid]::NewGuid().ToString('N')
    Move-Item -LiteralPath $TargetPath -Destination $swapPath
    try {
        Move-Item -LiteralPath $stagedTarget -Destination $TargetPath
        $stagedTarget = $null
    } catch {
        if (-not (Test-Path -LiteralPath $TargetPath) -and (Test-Path -LiteralPath $swapPath)) {
            Move-Item -LiteralPath $swapPath -Destination $TargetPath
            $swapPath = $null
        }
        throw
    }

    Write-Log "New executable installed. Backup=$backupPath"
    $newProcess = Start-Process -FilePath $TargetPath -PassThru
    Start-Sleep -Seconds 7
    $newProcess.Refresh()
    if ($newProcess.HasExited) {
        throw ("The updated app exited shortly after launch (exit code {0}). Rolling back." -f $newProcess.ExitCode)
    }

    if ($swapPath -and (Test-Path -LiteralPath $swapPath)) { Remove-Item -LiteralPath $swapPath -Force -ErrorAction SilentlyContinue }
    Write-Log "Update succeeded. New PID=$($newProcess.Id); backup retained at $backupPath"

    # Keep the latest backup and prune only backups older than 30 days.
    Get-ChildItem -LiteralPath $backupDirectory -Filter 'CoolSnesh-Downloads-*.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-30) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
} catch {
    Write-Log ("Update failed: " + $_.Exception.Message)
    try {
        if ($swapPath -and (Test-Path -LiteralPath $swapPath)) {
            if (Test-Path -LiteralPath $TargetPath) { Remove-Item -LiteralPath $TargetPath -Force }
            Move-Item -LiteralPath $swapPath -Destination $TargetPath
            $swapPath = $null
        } elseif ($backupPath -and (Test-Path -LiteralPath $backupPath)) {
            Copy-Item -LiteralPath $backupPath -Destination $TargetPath -Force
        }
        if (Test-Path -LiteralPath $TargetPath) { Start-Process -FilePath $TargetPath | Out-Null }
        Write-Log 'Rollback attempt completed.'
    } catch {
        Write-Log ("CRITICAL: rollback failed: " + $_.Exception.Message)
    }
} finally {
    foreach ($path in @($SourcePath, $stagedTarget, $swapPath)) {
        if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
    try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
    try {
        $parent = Split-Path -Parent $SourcePath
        if ((Test-Path -LiteralPath $parent -PathType Container) -and -not (Get-ChildItem -LiteralPath $parent -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
        }
    } catch { }
}
""";
}
