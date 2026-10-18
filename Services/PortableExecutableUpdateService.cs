using System.Diagnostics;

namespace CoolSnesh.Avalonia.Services;

public sealed record PortableUpdateResult(bool Success, string Message, string? BackupPath = null);

/// <summary>Safe in-place update for Flowseal's portable TgWsProxy executable (not an installer).</summary>
public static class PortableExecutableUpdateService
{
    public static async Task<PortableUpdateResult> UpdateAsync(string installedExecutable, string downloadedExecutable,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installedExecutable) || !File.Exists(installedExecutable))
            return Fail("Путь установленного TgWsProxy.exe не задан или файл не найден. Укажи установленный EXE вручную.");
        if (!File.Exists(downloadedExecutable)) return Fail("Новый файл TgWsProxy не найден.");

        string target;
        try
        {
            target = Path.GetFullPath(installedExecutable);
            downloadedExecutable = Path.GetFullPath(downloadedExecutable);
        }
        catch { return Fail("Путь к EXE некорректен. Обновление отменено."); }

        if (PathEquals(target, downloadedExecutable))
            return Fail("Загруженный файл совпадает с путём установленной программы. Повторно скачай релиз в отдельную папку.");
        if (!Path.GetFileName(target).Equals("TgWsProxy_windows.exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(target).Equals("TgWsProxy.exe", StringComparison.OrdinalIgnoreCase))
            return Fail("Выбранный файл не похож на TgWsProxy_windows.exe или TgWsProxy.exe.");

        var running = FindRunningExecutable(target);
        if (running == RunningState.Unknown)
            return Fail("Не удалось проверить путь процесса TgWsProxy. Закрой TgWsProxy вручную и повтори обновление.");
        if (running == RunningState.Running)
            return Fail("TgWsProxy запущен. Для безопасного обновления закрой его вручную и повтори обновление; работающий процесс не будет завершён принудительно.");

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(target)!;
        var token = Guid.NewGuid().ToString("N");
        var staged = Path.Combine(directory, $".{Path.GetFileName(target)}.cs-new-{token}");
        var backup = Path.Combine(directory, $"{Path.GetFileName(target)}.cs-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{token[..8]}");
        var oldMoved = false;
        try
        {
            progress.Report("Подготовка обновления TgWsProxy…");
            await CopyFileAsync(downloadedExecutable, staged, cancellationToken);
            if (new FileInfo(staged).Length == 0) throw new IOException("Новый EXE пустой.");
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report("Создание резервной копии TgWsProxy…");
            File.Move(target, backup);
            oldMoved = true;
            try
            {
                File.Move(staged, target);
            }
            catch
            {
                if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
                oldMoved = false;
                throw;
            }

            AppLog.Write("PortableUpdate", $"TgWsProxy обновлён. Файл: {target}; резервная копия: {backup}");
            progress.Report("TgWsProxy обновлён.");
            return new PortableUpdateResult(true, $"TgWsProxy обновлён. Резервная копия: {backup}", backup);
        }
        catch (OperationCanceledException)
        {
            if (oldMoved && !File.Exists(target) && File.Exists(backup))
            {
                try { File.Move(backup, target); oldMoved = false; }
                catch (Exception ex) { AppLog.Write("PortableUpdate", "Не удалось восстановить TgWsProxy после отмены.", ex); }
            }
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("PortableUpdate", "Не удалось обновить TgWsProxy.", ex);
            if (oldMoved && !File.Exists(target) && File.Exists(backup))
            {
                try { File.Move(backup, target); oldMoved = false; }
                catch (Exception restoreEx) { AppLog.Write("PortableUpdate", "Не удалось восстановить резервную копию TgWsProxy.", restoreEx); }
            }
            return Fail("Не удалось обновить TgWsProxy: " + ex.Message + (File.Exists(backup) ? " Резервная копия: " + backup : ""), File.Exists(backup) ? backup : null);
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch (Exception ex) { AppLog.Write("PortableUpdate", "Не удалось удалить временный EXE.", ex); }
        }

        PortableUpdateResult Fail(string message, string? backupPath = null)
        {
            AppLog.Write("PortableUpdate", message);
            progress.Report(message);
            return new PortableUpdateResult(false, message, backupPath);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        await input.CopyToAsync(output, 131072, token);
        await output.FlushAsync(token);
    }

    private enum RunningState { NotRunning, Running, Unknown }

    private static RunningState FindRunningExecutable(string fullPath)
    {
        var exeName = Path.GetFileNameWithoutExtension(fullPath);
        foreach (var process in Process.GetProcessesByName(exeName))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) return RunningState.Unknown;
                    if (PathEquals(path, fullPath)) return RunningState.Running;
                }
                catch (Exception ex)
                {
                    AppLog.Write("PortableUpdate", $"Не удалось прочитать путь процесса {exeName}.", ex);
                    return RunningState.Unknown;
                }
            }
        }
        return RunningState.NotRunning;
    }

    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
