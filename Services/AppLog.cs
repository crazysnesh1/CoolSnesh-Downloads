using System.Text;

namespace CoolSnesh.Avalonia.Services;

public static class AppLog
{
    private static readonly object Sync = new();

    public static void Write(string area, string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.LogPath)!);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [{area}] {message}" +
                       (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine;
            lock (Sync) File.AppendAllText(SettingsService.LogPath, line, new UTF8Encoding(false));
        }
        catch { /* Logging must never crash the application. */ }
    }
}
