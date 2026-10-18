using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoolSnesh.Avalonia.Services;

public sealed record ZapretUpdateResult(bool Success, string Message, string? BackupPath = null);

/// <summary>
/// Conservative updater for Flowseal's bundle. It never replaces files while a standalone
/// strategy is running because that strategy cannot be identified/restarted reliably from
/// winws.exe alone. The Windows service mode is supported because its exact image path and
/// strategy label are recorded in the service registry key.
/// </summary>
public static class ZapretUpdateService
{
    public static async Task<ZapretUpdateResult> UpdateAsync(string installRoot, string downloadedArchive,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        string root;
        try { root = Path.GetFullPath(installRoot.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { return Fail("Путь установки Zapret некорректен."); }

        if (!InstalledVersionService.IsZapretRoot(root))
            return Fail("Не удалось подтвердить папку Zapret. Нужны service.bat и bin\\winws.exe. Обновление отменено без изменения файлов.");
        if (!File.Exists(downloadedArchive)) return Fail("Архив обновления Zapret не найден.");

        var parent = Directory.GetParent(root)?.FullName;
        var leaf = Path.GetFileName(root);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(leaf) || root.Length < 5)
            return Fail("Обновление заблокировано: путь установки выглядит небезопасным.");

        var token = Guid.NewGuid().ToString("N");
        var work = Path.Combine(parent, $".{leaf}.cs-update-work-{token}");
        var replacement = Path.Combine(parent, $".{leaf}.cs-update-new-{token}");
        var backup = Path.Combine(parent, $".{leaf}.cs-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{token[..8]}");
        var failed = Path.Combine(parent, $".{leaf}.cs-failed-{token}");
        var oldMoved = false;
        var serviceWasRunning = false;
        var serviceInfo = ReadServiceInfo();
        string? strategyName = null;

        try
        {
            progress.Report("Проверка состояния Zapret…");
            AppLog.Write("ZapretUpdate", $"Начало обновления. Путь: {root}. Архив: {downloadedArchive}");
            if (serviceInfo.Exists && serviceInfo.Running && PathEquals(serviceInfo.Root, root))
            {
                serviceWasRunning = true;
                strategyName = serviceInfo.Strategy;
                if (string.IsNullOrWhiteSpace(strategyName))
                    return Fail("Служба Zapret запущена, но не удалось определить сохранённую стратегию. Обновление не выполнялось.");
                AppLog.Write("ZapretUpdate", $"Обнаружена активная служба zapret. Стратегия: {strategyName}.");
            }

            var winwsState = FindWinwsProcesses(root);
            if (winwsState.HasUnknownProcess)
                return Fail("Не удалось определить путь запущенного winws.exe. Чтобы не нарушить работу обхода, обновление отменено. Закройте стратегию вручную и повторите.");
            if (winwsState.MatchingProcess && !serviceWasRunning)
                return Fail("Обнаружена запущенная стратегия через BAT-файл, но точное имя стратегии нельзя надёжно восстановить автоматически. Обновление не выполнялось. Остановите стратегию вручную и повторите; после этого загрузчик обновит файлы без запуска стратегии.");

            progress.Report("Распаковка обновления во временную папку…");
            Directory.CreateDirectory(work);
            ExtractZipSafely(downloadedArchive, Path.Combine(work, "unpack"));
            var unpack = Path.Combine(work, "unpack");
            var freshRoot = File.Exists(Path.Combine(unpack, "service.bat")) ? unpack :
                Directory.EnumerateDirectories(unpack, "*", SearchOption.AllDirectories).FirstOrDefault(InstalledVersionService.IsZapretRoot);
            if (string.IsNullOrWhiteSpace(freshRoot) || !InstalledVersionService.IsZapretRoot(freshRoot))
                return Fail("В архиве не найдена корректная структура Zapret (service.bat и bin\\winws.exe). Старые файлы не изменены.");

            PreserveUserLists(root, freshRoot, progress);
            Directory.Move(freshRoot, replacement);
            AppLog.Write("ZapretUpdate", "Архив проверен, пользовательские списки сохранены, подготовлена новая папка.");

            cancellationToken.ThrowIfCancellationRequested();
            if (serviceWasRunning)
            {
                progress.Report("Остановка службы Zapret…");
                var stopResult = await RunScAsync("stop", "zapret", CancellationToken.None);
                if (stopResult.ExitCode != 0 && await GetServiceStateAsync(CancellationToken.None) != 1)
                    return Fail("Не удалось остановить службу Zapret. Обновление отменено; проверьте права администратора и остановите службу вручную.");
                if (!await WaitForServiceStateAsync(1, TimeSpan.FromSeconds(25), CancellationToken.None))
                    return Fail("Служба Zapret не перешла в состояние «Остановлена» за 25 секунд. Файлы не менялись; остановите службу вручную.");
                AppLog.Write("ZapretUpdate", $"Служба остановлена. Зафиксированная стратегия: {strategyName}.");
            }

            // Re-check just before touching the live tree. A standalone strategy may have been
            // launched while the archive was being staged; never replace files underneath it.
            var preSwapProcesses = FindWinwsProcesses(root);
            if (preSwapProcesses.HasUnknownProcess)
            {
                // We stopped the service earlier but still cannot establish whether a winws
                // process belongs to it. Nothing has been replaced; best-effort restore the
                // exact registered service mode before asking for manual intervention.
                if (serviceWasRunning) await TryRestartServiceAsync();
                return Fail("Перед заменой не удалось подтвердить состояние winws.exe. Файлы не менялись; загрузчик попытался вернуть прежнюю службу. Проверь её состояние и повтори попытку после ручной проверки.");
            }
            if (preSwapProcesses.MatchingProcess)
                return Fail("Перед заменой обнаружен работающий winws.exe из этой папки. Файлы не менялись. Процесс не завершался принудительно; проверь активную стратегию и повтори обновление после её безопасной остановки.");

            progress.Report("Резервное копирование и замена файлов…");
            try
            {
                Directory.Move(root, backup);
                oldMoved = true;
                Directory.Move(replacement, root);
            }
            catch (Exception swapEx)
            {
                AppLog.Write("ZapretUpdate", "Не удалось заменить папку Zapret.", swapEx);
                if (!Directory.Exists(root) && Directory.Exists(backup)) Directory.Move(backup, root);
                oldMoved = false;
                if (serviceWasRunning) await TryRestartServiceAsync();
                return Fail("Не удалось заменить папку Zapret. Исходная папка восстановлена, если её перенос успел начаться. См. журнал.");
            }

            if (serviceWasRunning)
            {
                progress.Report("Перезапуск прежней стратегии…");
                AppLog.Write("ZapretUpdate", $"Запуск службы zapret с сохранённой конфигурацией стратегии «{strategyName}».");
                var startResult = await RunScAsync("start", "zapret", CancellationToken.None);
                var started = (startResult.ExitCode == 0 || await GetServiceStateAsync(CancellationToken.None) == 4)
                              && await WaitForServiceStateAsync(4, TimeSpan.FromSeconds(25), CancellationToken.None);
                if (!started)
                {
                    AppLog.Write("ZapretUpdate", "Новая версия не запустила прежнюю службу. Проверяется остановка перед откатом.");
                    var rollback = await RestoreBackupAsync(root, backup, failed, restartService: true);
                    if (rollback.FilesRestored) oldMoved = false;
                    if (!rollback.FilesRestored || !rollback.ServiceRunning)
                    {
                        AppLog.Write("ZapretUpdate", "КРИТИЧЕСКИЙ РЕЗУЛЬТАТ ОТКАТА: " + rollback.Message);
                        return Fail("Новая версия не запустилась. " + rollback.Message + " Резервная папка (если сохранена): " + backup, Directory.Exists(backup) ? backup : null);
                    }
                    return Fail("Новая версия не запустилась. Старые файлы восстановлены, прежняя стратегия снова запущена. " + rollback.Message);
                }
                AppLog.Write("ZapretUpdate", $"Служба запущена. Стратегия: {strategyName}. Резервная копия: {backup}");
            }

            AppLog.Write("ZapretUpdate", $"Обновление завершено. Резервная копия: {backup}. Пользовательские списки восстановлены перед переключением.");
            progress.Report("Обновление завершено.");
            return new ZapretUpdateResult(true, serviceWasRunning
                ? $"Zapret обновлён. Стратегия «{strategyName}» снова запущена. Резервная копия: {backup}"
                : $"Файлы Zapret обновлены. Активная стратегия не обнаружена, поэтому загрузчик ничего не запускал. Резервная копия: {backup}", backup);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("ZapretUpdate", "Операция отменена.");
            if (oldMoved && Directory.Exists(backup))
            {
                var rollback = await RestoreBackupAsync(root, backup, failed, serviceWasRunning);
                if (rollback.FilesRestored) oldMoved = false;
                if (!rollback.FilesRestored || (serviceWasRunning && !rollback.ServiceRunning))
                    AppLog.Write("ZapretUpdate", "Откат после отмены не завершён автоматически: " + rollback.Message);
            }
            else if (serviceWasRunning) await TryRestartServiceAsync();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("ZapretUpdate", "Ошибка обновления Zapret.", ex);
            var rollbackMessage = "";
            if (oldMoved && Directory.Exists(backup))
            {
                var rollback = await RestoreBackupAsync(root, backup, failed, serviceWasRunning);
                if (rollback.FilesRestored) oldMoved = false;
                rollbackMessage = " Откат: " + rollback.Message;
                if (!rollback.FilesRestored || (serviceWasRunning && !rollback.ServiceRunning))
                    AppLog.Write("ZapretUpdate", "Откат после ошибки требует внимания: " + rollback.Message);
            }
            else if (serviceWasRunning) await TryRestartServiceAsync();
            return Fail("Ошибка обновления Zapret: " + ex.Message + rollbackMessage + (Directory.Exists(backup) ? " Резервная папка: " + backup : ""), Directory.Exists(backup) ? backup : null);
        }
        finally
        {
            TryDeleteDirectory(work);
            if (Directory.Exists(replacement)) TryDeleteDirectory(replacement);
            // Never delete the rollback copy from this workflow. It is intentionally retained beside the installation.
        }

        ZapretUpdateResult Fail(string message, string? backupPath = null)
        {
            AppLog.Write("ZapretUpdate", message);
            progress.Report(message);
            return new ZapretUpdateResult(false, message, backupPath);
        }
    }

    private sealed record RollbackResult(bool FilesRestored, bool ServiceRunning, string Message);

    private static async Task<RollbackResult> RestoreBackupAsync(string root, string backup, string failed, bool restartService)
    {
        try
        {
            if (restartService)
            {
                await RunScAsync("stop", "zapret", CancellationToken.None);
                if (!await WaitForServiceStateAsync(1, TimeSpan.FromSeconds(20), CancellationToken.None))
                    return new RollbackResult(false, false, "Службу нельзя подтвердить как остановленную; активную папку не перемещали. Резервная копия оставлена на месте.");
            }

            var processes = FindWinwsProcesses(root);
            if (processes.HasUnknownProcess || processes.MatchingProcess)
                return new RollbackResult(false, false, "Найден запущенный или неидентифицируемый winws.exe; папки не перемещались во избежание изменения файлов работающей стратегии. Резервная копия оставлена на месте.");

            if (!Directory.Exists(backup))
                return new RollbackResult(false, false, "Резервная папка не найдена. Текущие файлы сохранены; проверь журнал.");

            if (Directory.Exists(root)) Directory.Move(root, failed);
            Directory.Move(backup, root);
            AppLog.Write("ZapretUpdate", $"Старая версия восстановлена из резервной папки в {root}; новая сохранена в {failed} (если перемещение завершилось). ");

            if (!restartService)
                return new RollbackResult(true, false, "Старые файлы восстановлены. Служба до обновления не работала, поэтому загрузчик не запускал её самовольно.");

            var start = await RunScAsync("start", "zapret", CancellationToken.None);
            var running = (start.ExitCode == 0 || await GetServiceStateAsync(CancellationToken.None) == 4)
                && await WaitForServiceStateAsync(4, TimeSpan.FromSeconds(25), CancellationToken.None);
            if (!running)
                return new RollbackResult(true, false, "Старые файлы восстановлены, но прежняя служба не запустилась. Запусти её вручную после проверки журнала.");
            AppLog.Write("ZapretUpdate", "Откат завершён: старая версия восстановлена, служба снова работает.");
            return new RollbackResult(true, true, "Старые файлы восстановлены и прежняя стратегия повторно запущена.");
        }
        catch (Exception ex)
        {
            AppLog.Write("ZapretUpdate", "Ошибка безопасного отката Zapret.", ex);
            // Emergency recovery: if the live directory was moved aside but restoring the
            // backup failed, put the old tree back whenever the target path is still empty.
            var filesRestored = false;
            try
            {
                if (!Directory.Exists(root) && Directory.Exists(backup)) Directory.Move(backup, root);
                filesRestored = Directory.Exists(root) && !Directory.Exists(backup);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write("ZapretUpdate", "Не удалось выполнить аварийное восстановление старой папки Zapret.", restoreEx);
            }

            var serviceRunning = false;
            if (restartService && filesRestored)
            {
                try
                {
                    var start = await RunScAsync("start", "zapret", CancellationToken.None);
                    serviceRunning = (start.ExitCode == 0 || await GetServiceStateAsync(CancellationToken.None) == 4)
                        && await WaitForServiceStateAsync(4, TimeSpan.FromSeconds(20), CancellationToken.None);
                }
                catch (Exception startEx) { AppLog.Write("ZapretUpdate", "Аварийный запуск прежней службы после отката не удался.", startEx); }
            }
            var message = "Автоматический откат прерван: " + ex.Message + ". " +
                (filesRestored ? "Старая папка восстановлена." : "Папки не удалось восстановить автоматически; не удаляй резервные каталоги.") +
                (restartService ? (serviceRunning ? " Служба снова работает." : " Состояние службы требует ручной проверки.") : "");
            return new RollbackResult(filesRestored, serviceRunning, message);
        }
    }

    private static void PreserveUserLists(string oldRoot, string freshRoot, IProgress<string> progress)
    {
        var oldLists = Path.Combine(oldRoot, "lists");
        var newLists = Path.Combine(freshRoot, "lists");
        if (!Directory.Exists(oldLists)) return;

        var userFiles = Directory.EnumerateFiles(oldLists, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileName(p).Contains("-user.", StringComparison.OrdinalIgnoreCase)).ToList();
        if (userFiles.Count == 0) return;
        Directory.CreateDirectory(newLists);

        foreach (var source in userFiles)
        {
            var target = Path.Combine(newLists, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            if (!File.Exists(target) || !FilesEqual(source, target))
                throw new IOException($"Не удалось проверить сохранность пользовательского списка {Path.GetFileName(source)}. Обновление отменено до остановки стратегии.");
            AppLog.Write("ZapretUpdate", $"Сохранён и проверен пользовательский список: {Path.GetFileName(source)}");
            if (Path.GetFileName(source).Equals("list-general-user.txt", StringComparison.OrdinalIgnoreCase))
                progress.Report("Пользовательский list-general-user.txt сохранён и проверен.");
        }

        var generalUser = userFiles.FirstOrDefault(p => Path.GetFileName(p).Equals("list-general-user.txt", StringComparison.OrdinalIgnoreCase));
        if (generalUser is not null && !FilesEqual(generalUser, Path.Combine(newLists, "list-general-user.txt")))
            throw new IOException("list-general-user.txt не прошёл проверку целостности. Обновление отменено.");
    }

    private static bool FilesEqual(string left, string right)
    {
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        if (a.Length != b.Length) return false;
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            var readA = a.Read(bufferA, 0, bufferA.Length);
            var readB = b.Read(bufferB, 0, bufferB.Length);
            if (readA != readB) return false;
            if (readA == 0) return true;
            for (var i = 0; i < readA; i++) if (bufferA[i] != bufferB[i]) return false;
        }
    }

    private static void ExtractZipSafely(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var destinationFull = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName)) continue;
            var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(name)) throw new InvalidDataException("В архиве найден абсолютный путь.");
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(destinationFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("В архиве найден небезопасный путь (path traversal).");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static (bool Exists, bool Running, string? Root, string? Strategy) ReadServiceInfo()
    {
        try
        {
            var result = RunScSync("query", "zapret");
            if (result.ExitCode != 0) return (false, false, null, null);
            var stateMatch = Regex.Match(result.Output, @"(?im)STATE\s*:\s*(\d+)");
            var running = stateMatch.Success && stateMatch.Groups[1].Value == "4";
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            var imagePath = key?.GetValue("ImagePath") as string;
            var strategy = key?.GetValue("zapret-discord-youtube") as string;
            string? root = null;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var executable = ExtractFirstPath(imagePath);
                var bin = Path.GetDirectoryName(executable);
                root = bin is null ? null : Directory.GetParent(bin)?.FullName;
            }
            return (true, running, root, strategy);
        }
        catch (Exception ex) { AppLog.Write("ZapretUpdate", "Не удалось прочитать состояние службы.", ex); return (false, false, null, null); }
    }

    private static (bool MatchingProcess, bool HasUnknownProcess) FindWinwsProcesses(string root)
    {
        var matching = false;
        try
        {
            foreach (var process in Process.GetProcessesByName("winws"))
            {
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch { return (matching, true); }
                    if (string.IsNullOrWhiteSpace(path)) return (matching, true);
                    var processRoot = Directory.GetParent(Path.GetDirectoryName(path) ?? "")?.FullName;
                    if (PathEquals(processRoot, root)) matching = true;
                }
            }
        }
        catch (Exception ex) { AppLog.Write("ZapretUpdate", "Не удалось перечислить процессы winws.exe.", ex); return (matching, true); }
        return (matching, false);
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(string verb, string service, CancellationToken token)
    {
        var start = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(verb); start.ArgumentList.Add(service);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить sc.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        var output = (await stdout) + (await stderr);
        AppLog.Write("ZapretUpdate", $"sc {verb} {service}: exit={process.ExitCode}; {output.Trim()}");
        return (process.ExitCode, output);
    }

    private static (int ExitCode, string Output) RunScSync(string verb, string service)
    {
        var start = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(verb); start.ArgumentList.Add(service);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить sc.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }

    private static async Task<int?> GetServiceStateAsync(CancellationToken token)
    {
        try
        {
            var result = await RunScAsync("query", "zapret", token);
            var match = Regex.Match(result.Output, @"(?im)STATE\s*:\s*(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    private static async Task<bool> WaitForServiceStateAsync(int target, TimeSpan timeout, CancellationToken token)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            token.ThrowIfCancellationRequested();
            if (await GetServiceStateAsync(token) == target) return true;
            await Task.Delay(300, token);
        }
        return false;
    }

    private static async Task TryRestartServiceAsync()
    {
        try
        {
            var state = await GetServiceStateAsync(CancellationToken.None);
            if (state != 4) await RunScAsync("start", "zapret", CancellationToken.None);
            await WaitForServiceStateAsync(4, TimeSpan.FromSeconds(15), CancellationToken.None);
        }
        catch (Exception ex) { AppLog.Write("ZapretUpdate", "Не удалось повторно запустить службу во время восстановления.", ex); }
    }

    private static string ExtractFirstPath(string value)
    {
        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); if (end > 1) return text[1..end]; }
        var space = text.IndexOf(' ');
        return space >= 0 ? text[..space] : text;
    }

    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { AppLog.Write("ZapretUpdate", $"Не удалось удалить временную папку {path}.", ex); }
    }
}
