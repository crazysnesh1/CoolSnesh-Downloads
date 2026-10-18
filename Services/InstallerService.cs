using System.ComponentModel;
using System.Diagnostics;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

public sealed record InstallResult(bool Supported, bool Succeeded, bool RestartRequired, string Message, int? ExitCode = null);

/// <summary>
/// Launches vendor installers for a user-requested update. Only the installer process is elevated;
/// the catalog UI remains unelevated. Installers are expected to finish within the timeout, and
/// timeout cleanup is deferred until the installer process actually exits.
/// </summary>
public static class InstallerService
{
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(20);
    private const int ErrorElevationRequired = 740;
    private const int ErrorCancelledByUser = 1223;

    public static async Task<InstallResult> InstallOrUpdateAsync(
        PackageItem package,
        string installerPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (package.IsZapret)
            return new InstallResult(false, false, false, "Для Zapret используется отдельный безопасный сценарий обновления.");
        if (!File.Exists(installerPath))
            return new InstallResult(false, false, false, "Файл установщика не найден.");

        var extension = Path.GetExtension(installerPath);
        var workingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath();
        var isMsi = extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
        var hasSilentArguments = !string.IsNullOrWhiteSpace(package.SilentArguments);
        var isSilent = isMsi || hasSilentArguments;

        ProcessStartInfo start;
        if (isMsi)
        {
            var logDirectory = Path.GetDirectoryName(SettingsService.LogPath)!;
            Directory.CreateDirectory(logDirectory);
            var msiLog = Path.Combine(logDirectory, $"installer-{Sanitize(package.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = $"/i {Quote(installerPath)} /qn /norestart /L*v {Quote(msiLog)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
        }
        else
        {
            start = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = package.SilentArguments ?? string.Empty,
                UseShellExecute = !hasSilentArguments,
                CreateNoWindow = hasSilentArguments,
                WorkingDirectory = workingDirectory
            };
        }

        bool elevateUpFront = ShouldRequestElevation(package);
        if (elevateUpFront)
            ConfigureElevation(start);

        progress.Report(new DownloadProgress(isSilent ? $"Запуск тихого обновления: {package.Name}…" : $"Открытие установщика {package.Name}…", 100));
        AppLog.Write("Installer", $"Запуск установки/обновления {package.Name}: {start.FileName} {start.Arguments}; silent={isSilent}; elevated={elevateUpFront}");

        Process? process = null;
        Task? processWaitTask = null;
        bool cleanupTransferred = false;
        try
        {
            try
            {
                process = StartProcess(start);
            }
            catch (Win32Exception ex) when (!elevateUpFront && ex.NativeErrorCode == ErrorElevationRequired)
            {
                // Follow Windows' elevation-required signal and request UAC only for this installer.
                AppLog.Write("Installer", $"{package.Name}: Windows требует права администратора; повторный запуск с UAC.");
                ConfigureElevation(start);
                process = StartProcess(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
            {
                return new InstallResult(true, false, false, "Запуск от имени администратора отменён. Обновление не выполнено.");
            }

            var activeProcess = process ?? throw new InvalidOperationException("Не удалось получить дескриптор процесса установщика.");
            var stopwatch = Stopwatch.StartNew();
            // The process wait itself is not tied to the UI task token. Cancellation is monitored
            // by the short delay below, which lets cleanup retain a valid process wait if needed.
            var waitTask = activeProcess.WaitForExitAsync(CancellationToken.None);
            processWaitTask = waitTask;
            while (!waitTask.IsCompleted)
            {
                var nextTick = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var completed = await Task.WhenAny(waitTask, nextTick);
                if (completed == waitTask)
                    break;

                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= InstallerTimeout)
                {
                    AppLog.Write("Installer", $"{package.Name}: установщик всё ещё работает после {InstallerTimeout.TotalMinutes:0} минут; его файл будет удалён после завершения процесса.");
                    _ = CleanupAfterProcessExitAsync(activeProcess, installerPath, waitTask);
                    cleanupTransferred = true;
                    return new InstallResult(true, false, false,
                        $"Установщик {package.Name} работает дольше 20 минут. Проверь окна установщика и UAC; не запускай обновление повторно, пока процесс не завершится. Временный файл будет удалён после завершения процесса.");
                }

                var elapsed = stopwatch.Elapsed;
                progress.Report(new DownloadProgress(
                    isSilent
                        ? $"Тихое обновление {package.Name}… {elapsed:mm\\:ss}"
                        : $"Ожидание установщика {package.Name}… {elapsed:mm\\:ss}",
                    100));
            }

            await waitTask;
            var code = activeProcess.ExitCode;
            if (code == 0)
            {
                AppLog.Write("Installer", $"{package.Name}: установщик завершился с кодом 0.");
                return new InstallResult(true, true, false, "Установка/обновление завершено.", code);
            }
            if (code == 3010)
            {
                AppLog.Write("Installer", $"{package.Name}: установка завершена, требуется перезагрузка (3010).");
                return new InstallResult(true, true, true, "Установлено; требуется перезагрузка Windows.", code);
            }

            AppLog.Write("Installer", $"{package.Name}: установщик вернул код {code}.");
            return new InstallResult(true, false, false, $"Установщик завершился с кодом {code}. См. журнал.", code);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("Installer", $"Задача установки {package.Name} отменена пользователем.");
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    // Keep the process handle alive and remove the installer after exit if kill is asynchronous.
                    if (!process.HasExited && processWaitTask is not null)
                    {
                        _ = CleanupAfterProcessExitAsync(process, installerPath, processWaitTask);
                        cleanupTransferred = true;
                    }
                }
            }
            catch (Exception killEx)
            {
                AppLog.Write("Installer", $"Не удалось остановить установщик {package.Name}.", killEx);
                try
                {
                    if (process is { HasExited: false } && processWaitTask is not null)
                    {
                        _ = CleanupAfterProcessExitAsync(process, installerPath, processWaitTask);
                        cleanupTransferred = true;
                    }
                }
                catch (Exception waitEx)
                {
                    AppLog.Write("Installer", $"Не удалось передать очистку установщика {package.Name} фоновому наблюдателю.", waitEx);
                }
            }
            return new InstallResult(true, false, false, "Установка отменена. Если установщик продолжает работать, дождись его завершения перед повторной попыткой.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
        {
            return new InstallResult(true, false, false, "Запрос UAC отменён. Обновление не выполнено.");
        }
        catch (Exception ex)
        {
            AppLog.Write("Installer", $"Ошибка установки/обновления {package.Name}.", ex);
            return new InstallResult(true, false, false, "Ошибка запуска установщика: " + ex.Message);
        }
        finally
        {
            if (!cleanupTransferred)
                process?.Dispose();
        }
    }

    private static Process StartProcess(ProcessStartInfo start) =>
        Process.Start(start) ?? throw new InvalidOperationException("Windows не смог запустить установщик.");

    private static void ConfigureElevation(ProcessStartInfo start)
    {
        start.UseShellExecute = true;
        start.Verb = "runas";
        start.CreateNoWindow = false;
        start.WindowStyle = ProcessWindowStyle.Normal;
    }

    private static bool ShouldRequestElevation(PackageItem package)
    {
        if (package.RequiresElevation)
            return true;

        // If registry detection points inside Program Files, the installed copy is almost
        // certainly machine-scoped. Ask for UAC before running an installer against that location.
        if (string.IsNullOrWhiteSpace(package.InstallationPath))
            return false;

        try
        {
            var path = Path.GetFullPath(package.InstallationPath.Trim());
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Installer", $"Не удалось определить область установки для {package.Name}; будет выполнен обычный запуск с fallback на UAC.", ex);
        }
        return false;
    }

    private static async Task CleanupAfterProcessExitAsync(Process process, string installerPath, Task waitTask)
    {
        try
        {
            await waitTask;
        }
        catch (Exception ex)
        {
            AppLog.Write("Installer", "Не удалось дождаться завершения длительно работающего установщика.", ex);
        }
        finally
        {
            process.Dispose();
            TryDeleteInstaller(installerPath);
        }
    }

    public static void TryDeleteInstaller(string? installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
            return;
        try
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
                AppLog.Write("Installer", $"Временный установщик удалён: {installerPath}");
            }
            CleanupTemporaryStagingFolders(Path.GetDirectoryName(installerPath));
        }
        catch (Exception ex)
        {
            AppLog.Write("Installer", $"Не удалось удалить временный установщик {installerPath}; потребуется повторная очистка.", ex);
        }
    }


    private static void CleanupTemporaryStagingFolders(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var stagingRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CoolSneshDownloads", "Installers"))
                .TrimEnd(Path.DirectorySeparatorChar);
            var current = Path.GetFullPath(directory);
            while (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                var isStagingChild = current.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var isStagingRoot = current.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase);
                if (!isStagingChild && !isStagingRoot) break;
                Directory.Delete(current);
                if (isStagingRoot) break;
                current = Directory.GetParent(current)?.FullName ?? string.Empty;
                if (string.IsNullOrEmpty(current)) break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Installer", "Не удалось полностью очистить временные папки установщика.", ex);
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static string Sanitize(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
