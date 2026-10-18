using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

public sealed record InstalledInfo(bool IsInstalled, string Version, string Path, string DisplayName = "");

/// <summary>Best-effort Windows installed-software detector (uninstall registry + executable metadata).</summary>
public static class InstalledVersionService
{
    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public static InstalledInfo Detect(PackageItem package, string? preferredPath)
    {
        // A valid user-selected Flowseal path must win over automatic discovery. Otherwise an old
        // leftover folder can shadow the newly selected installation when versions are refreshed.
        if (package.IsFlowseal && !string.IsNullOrWhiteSpace(preferredPath) && PathExists(preferredPath))
        {
            if (package.IsZapret)
            {
                var manualRoot = ResolveZapretRoot(preferredPath);
                if (IsZapretRoot(manualRoot))
                {
                    return new InstalledInfo(true, ReadZapretVersion(manualRoot), manualRoot, "Zapret (ручной путь)");
                }
            }
            else
            {
                var normalizedPath = Path.GetFullPath(preferredPath);
                return new InstalledInfo(true, ReadVersionFromPath(package, normalizedPath), normalizedPath, package.Name + " (ручной путь)");
            }
        }

        InstalledInfo? flowsealAutoDetectedWithoutVersion = null;

        if (package.IsSteam)
            return DetectSteam(package) ?? new InstalledInfo(false, "", "");

        if (package.IsZapret)
        {
            var servicePath = TryGetZapretServiceRoot();
            if (!string.IsNullOrWhiteSpace(servicePath) && IsZapretRoot(servicePath))
            {
                var detected = new InstalledInfo(true, ReadZapretVersion(servicePath), servicePath, "Zapret service");
                if (!string.IsNullOrWhiteSpace(detected.Version)) return detected;
                flowsealAutoDetectedWithoutVersion ??= detected;
            }

            foreach (var candidate in ZapretCandidates())
            {
                if (!IsZapretRoot(candidate)) continue;
                var detected = new InstalledInfo(true, ReadZapretVersion(candidate), candidate, "Zapret folder");
                if (!string.IsNullOrWhiteSpace(detected.Version)) return detected;
                flowsealAutoDetectedWithoutVersion ??= detected;
            }
        }
        else if (package.IsFlowseal)
        {
            // Flowseal's standalone utilities may not create an uninstall-registry entry.
            // Check conservative, common install/download roots and their immediate children first.
            foreach (var candidate in FlowsealExecutableCandidates(package))
            {
                if (!File.Exists(candidate)) continue;
                var detected = new InstalledInfo(true, ReadVersionFromPath(package, candidate), candidate, "Flowseal executable");
                if (!string.IsNullOrWhiteSpace(detected.Version)) return detected;
                flowsealAutoDetectedWithoutVersion ??= detected;
            }
        }

        var registry = FindInUninstallRegistry(package);
        if (registry is not null)
        {
            var path = registry.Value.Path;
            var version = registry.Value.Version;
            if (string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(path))
                version = ReadVersionFromPath(package, path);

            // If the uninstall record identifies Flowseal but lacks version metadata,
            // allow its Settings-only manual fallback to provide the version.
            if (package.IsFlowseal && string.IsNullOrWhiteSpace(version) &&
                !string.IsNullOrWhiteSpace(preferredPath) && PathExists(preferredPath))
            {
                var manualVersion = ReadVersionFromPath(package, preferredPath);
                if (!string.IsNullOrWhiteSpace(manualVersion))
                    return new InstalledInfo(true, manualVersion, Path.GetFullPath(preferredPath), package.Name + " (ручной путь)");
            }
            return new InstalledInfo(true, version ?? "", path ?? "", registry.Value.DisplayName ?? "");
        }

        // User-selected Flowseal paths are only a fallback after automatic discovery.
        if (package.IsFlowseal && !string.IsNullOrWhiteSpace(preferredPath) && PathExists(preferredPath))
        {
            var version = ReadVersionFromPath(package, preferredPath);
            return new InstalledInfo(true, version, Path.GetFullPath(preferredPath), package.Name + " (ручной путь)");
        }

        return flowsealAutoDetectedWithoutVersion ?? new InstalledInfo(false, "", "");
    }

    public static string FormatStatus(PackageItem package, InstalledInfo installed)
    {
        if (!installed.IsInstalled) return "Не установлено";
        if (string.IsNullOrWhiteSpace(installed.Version)) return "Установлено · версия не определена";

        var displayedInstalledVersion = VersionComparison.Normalize(installed.Version) ?? installed.Version.Trim();
        // The first line (PackageItem.VersionCaption) already explains when no latest version is available.
        // Do not repeat that message in the installation-status line.
        if (!VersionComparison.IsAvailableLatestVersion(package.Version))
            return $"Установлено: {displayedInstalledVersion}";

        var displayedLatestVersion = VersionComparison.Normalize(package.Version) ?? package.Version.Trim();
        var comparison = VersionComparison.Compare(installed.Version, package.Version);
        if (comparison == 0) return $"Актуально · {displayedInstalledVersion}";
        if (comparison < 0) return $"Доступно обновление {displayedInstalledVersion} → {displayedLatestVersion}";
        if (comparison > 0) return $"Установлена более новая версия · {displayedInstalledVersion}";
        return $"Установлено: {displayedInstalledVersion} · сравнение версий недоступно";
    }

    // Compatibility wrapper retained for existing callers; comparison logic lives in VersionComparison.
    public static int? CompareVersions(string installed, string latest) => VersionComparison.Compare(installed, latest);

    private static string ResolveZapretRoot(string path)
    {
        var root = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        if (File.Exists(path))
        {
            var name = Path.GetFileName(path);
            if (name.Equals("winws.exe", StringComparison.OrdinalIgnoreCase))
                root = Directory.GetParent(root)?.FullName ?? root;
        }

        if (IsZapretRoot(root)) return root;
        if (!Directory.Exists(root)) return root;
        try
        {
            var nested = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(IsZapretRoot);
            if (!string.IsNullOrWhiteSpace(nested)) return nested;
        }
        catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось найти папку Zapret в ручном пути {root}.", ex); }
        return root;
    }

    public static bool IsZapretRoot(string? path) => !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "service.bat")) && File.Exists(Path.Combine(path, "bin", "winws.exe"));

    public static string ReadZapretVersion(string root)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(root, "service.bat"));
            // Flowseal uses both `set LOCAL_VERSION=...` and `set "LOCAL_VERSION=..."` styles.
            var match = Regex.Match(text, @"(?im)^\s*set\s+""?LOCAL_VERSION\s*=\s*([^""\r\n ]+)");
            if (match.Success) return match.Groups[1].Value.Trim().Trim('"');
            var versionFile = Path.Combine(root, ".service", "version.txt");
            if (File.Exists(versionFile)) return File.ReadAllText(versionFile).Trim();
        }
        catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось прочитать версию Zapret в {root}.", ex); }
        return "";
    }

    private static InstalledInfo? DetectSteam(PackageItem package)
    {
        var roots = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var keyPath in new[] { @"SOFTWARE\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(keyPath);
                if (key is null) continue;
                foreach (var valueName in new[] { "SteamPath", "InstallPath" })
                {
                    var value = key.GetValue(valueName) as string;
                    if (!string.IsNullOrWhiteSpace(value)) roots.Add(value.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));
                }
            }
            catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось прочитать путь Steam из {hive}\\{keyPath}.", ex); }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        roots.AddRange(new[]
        {
            Path.Combine(programFilesX86, "Steam"), Path.Combine(programFiles, "Steam"),
            @"C:\Steam", @"D:\Steam", @"D:\Games\Steam"
        });

        // A running Steam process is a useful last-resort locator when registry values are missing.
        var steamProcessDetected = false;
        InstalledInfo? steamDetectedWithoutVersion = null;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            steamProcessDetected = true;
            try
            {
                var processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath)) roots.Add(Path.GetDirectoryName(processPath)!);
            }
            catch (Exception ex) { AppLog.Write("VersionDetection", "Не удалось прочитать путь активного процесса Steam.", ex); }
            finally { process.Dispose(); }
        }

        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
                var exe = Path.Combine(fullRoot, "steam.exe");
                if (!File.Exists(exe)) continue;
                var detected = new InstalledInfo(true, ReadSteamVersion(fullRoot), fullRoot, "Steam");
                if (!string.IsNullOrWhiteSpace(detected.Version)) return detected;
                steamDetectedWithoutVersion ??= detected;
            }
            catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось проверить путь Steam {root}.", ex); }
        }
        return steamDetectedWithoutVersion ?? (steamProcessDetected ? new InstalledInfo(true, "", "", "Steam process") : null);
    }

    private static string ReadSteamVersion(string steamRoot)
    {
        try
        {
            // The local package manifest has the same build-id format as Valve's latest-client manifest.
            var packageManifest = Path.Combine(steamRoot, "package", "steam_client_win64.manifest");
            if (File.Exists(packageManifest))
            {
                var localBuild = VersionResolver.ParseSteamManifestVersion(File.ReadAllText(packageManifest));
                if (!string.IsNullOrWhiteSpace(localBuild)) return localBuild;
            }

            var inf = Path.Combine(steamRoot, "steam.inf");
            if (File.Exists(inf))
            {
                var content = File.ReadAllText(inf);
                foreach (var key in new[] { "ClientVersionSteam", "ClientVersion" })
                {
                    var match = Regex.Match(content, $@"(?im)^\s*{Regex.Escape(key)}\s*=\s*(\d+)\s*$");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
        }
        catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось прочитать локальную версию Steam в {steamRoot}.", ex); }
        return "";
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string ReadVersionFromPath(PackageItem package, string path)
    {
        try
        {
            if (package.IsZapret)
            {
                var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                if (File.Exists(path) && Path.GetFileName(path).Equals("winws.exe", StringComparison.OrdinalIgnoreCase))
                    root = Directory.GetParent(root)?.FullName ?? root;
                if (!IsZapretRoot(root) && Directory.Exists(root))
                {
                    var nested = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(IsZapretRoot);
                    if (!string.IsNullOrWhiteSpace(nested)) root = nested;
                }
                if (IsZapretRoot(root)) return ReadZapretVersion(root);
            }

            if (File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var version = ReadExecutableVersion(path);
                if (!string.IsNullOrWhiteSpace(version)) return version;
                var adjacentVersion = ReadFlowsealManifestVersion(package, Path.GetDirectoryName(path)!);
                if (!string.IsNullOrWhiteSpace(adjacentVersion)) return adjacentVersion;
                return "";
            }

            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return "";
            var names = package.ExecutableNames.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in names)
            {
                var file = Directory.EnumerateFiles(folder, name, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (file is not null)
                {
                    var version = ReadExecutableVersion(file);
                    if (!string.IsNullOrWhiteSpace(version)) return version;
                }
            }
            foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Take(80))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(info.ProductName) && package.RegistryAliases.Split('|').Any(a => info.ProductName.Contains(a, StringComparison.OrdinalIgnoreCase)))
                {
                    var version = ReadExecutableVersion(exe);
                    if (!string.IsNullOrWhiteSpace(version)) return version;
                }
            }
            var manifestVersion = ReadFlowsealManifestVersion(package, folder);
            if (!string.IsNullOrWhiteSpace(manifestVersion)) return manifestVersion;
        }
        catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось определить версию {package.Name} по пути {path}.", ex); }
        return "";
    }

    private static string ReadExecutableVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion;
        return info.FileVersion ?? "";
    }

    private static (string? Version, string? Path, string DisplayName)? FindInUninstallRegistry(PackageItem package)
    {
        var aliases = package.RegistryAliases.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(package.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<(int Score, string? Version, string? Path, string DisplayName)>();

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var subPath in UninstallKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(subPath);
                if (uninstall is null) continue;
                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subName);
                    if (entry is null) continue;
                    var display = entry.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;
                    var alias = aliases.FirstOrDefault(a => display.Equals(a, StringComparison.OrdinalIgnoreCase))
                                ?? aliases.FirstOrDefault(a => display.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(display, StringComparison.OrdinalIgnoreCase));
                    if (alias is null) continue;
                    var score = display.Equals(alias, StringComparison.OrdinalIgnoreCase) ? 100 : 60;
                    if (display.Contains("update", StringComparison.OrdinalIgnoreCase) || display.Contains("webview", StringComparison.OrdinalIgnoreCase)) score -= 30;
                    var version = entry.GetValue("DisplayVersion") as string ?? "";
                    var installLocation = entry.GetValue("InstallLocation") as string ?? "";
                    var icon = entry.GetValue("DisplayIcon") as string ?? "";
                    var iconPath = ExtractExecutablePath(icon);
                    var path = Directory.Exists(installLocation) ? installLocation : (File.Exists(iconPath) ? iconPath : installLocation);
                    candidates.Add((score, version, path, display));
                }
            }
            catch (Exception ex) { AppLog.Write("VersionDetection", $"Ошибка чтения uninstall-реестра {hive}\\{subPath}.", ex); }
        }
        var best = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Version).FirstOrDefault();
        return best == default ? null : (best.Version, best.Path, best.DisplayName);
    }

    private static string ExtractExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1) return value[1..end];
        }
        var comma = value.IndexOf(',');
        return (comma >= 0 ? value[..comma] : value).Trim();
    }

    private static IEnumerable<string> FlowsealExecutableCandidates(PackageItem package)
    {
        var names = package.ExecutableNames.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "Desktop"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) yield return direct;
            }
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(500).ToArray(); }
            catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось просмотреть каталог {root} для {package.Name}.", ex); continue; }
            foreach (var child in children)
            foreach (var name in names)
            {
                var direct = Path.Combine(child, name);
                if (File.Exists(direct)) yield return direct;
            }
        }
    }

    private static string ReadFlowsealManifestVersion(PackageItem package, string folder)
    {
        if (!package.Repo.StartsWith("Flowseal/", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder)) return "";
        var files = new[]
        {
            Path.Combine(folder, ".service", "version.txt"),
            Path.Combine(folder, "version.txt"),
            Path.Combine(folder, "VERSION"),
            Path.Combine(folder, "manifest.json")
        };
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file)) continue;
                var content = File.ReadAllText(file);
                var match = Regex.Match(content, @"(?im)(?:^\s*(?:version|appVersion|tag)\s*[:=]\s*|""(?:version|appVersion|tag)""\s*:\s*"")v?(\d+(?:\.\d+){1,3}(?:[-+][\w.-]+)?)", RegexOptions.CultureInvariant);
                if (match.Success) return match.Groups[1].Value;
                var simple = Regex.Match(content.Trim(), @"^v?\d+(?:\.\d+){1,3}(?:[-+][\w.-]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (simple.Success) return simple.Value.TrimStart('v', 'V');
            }
            catch (Exception ex) { AppLog.Write("VersionDetection", $"Не удалось прочитать манифест Flowseal {file}.", ex); }
        }
        return "";
    }

    private static string? TryGetZapretServiceRoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            var imagePath = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var exe = ExtractExecutablePath(imagePath);
            if (!Path.GetFileName(exe).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)) return null;
            var bin = Path.GetDirectoryName(exe);
            var root = bin is null ? null : Directory.GetParent(bin)?.FullName;
            return IsZapretRoot(root) ? root : null;
        }
        catch (Exception ex) { AppLog.Write("VersionDetection", "Не удалось определить путь установленной службы Zapret.", ex); return null; }
    }

    private static IEnumerable<string> ZapretCandidates()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            @"C:\zapret", @"C:\zapret-discord-youtube",
            Path.Combine(user, "Downloads", "zapret-discord-youtube"),
            Path.Combine(user, "Downloads", "zapret"),
            Path.Combine(user, "Desktop", "zapret-discord-youtube"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "zapret-discord-youtube")
        };
        return roots.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
