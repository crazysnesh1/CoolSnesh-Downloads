using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoolSnesh.Avalonia.Models;

namespace CoolSnesh.Avalonia.Services;

/// <summary>Stores settings in %AppData% and transparently upgrades missing fields from older configs.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoolSneshDownloads");
    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");
    public static string LogPath => Path.Combine(AppDataDirectory, "logs", "launcher.log");

    public static LauncherSettings Load()
    {
        Directory.CreateDirectory(AppDataDirectory);
        LauncherSettings settings;
        var backupPath = SettingsPath + ".bak";
        try
        {
            if (File.Exists(SettingsPath))
            {
                settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new LauncherSettings();
            }
            else if (File.Exists(backupPath))
            {
                settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(backupPath), JsonOptions) ?? new LauncherSettings();
                AppLog.Write("Settings", "Основной конфиг отсутствует; настройки восстановлены из settings.json.bak.");
            }
            else
            {
                settings = new LauncherSettings();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings", "Не удалось прочитать основной конфиг; проверяется резервная копия.", ex);
            try
            {
                settings = File.Exists(backupPath)
                    ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(backupPath), JsonOptions) ?? new LauncherSettings()
                    : new LauncherSettings();
                AppLog.Write("Settings", File.Exists(backupPath) ? "Настройки восстановлены из settings.json.bak." : "Резервная копия настроек отсутствует; используются значения по умолчанию.");
            }
            catch (Exception backupEx)
            {
                AppLog.Write("Settings", "Не удалось прочитать и резервную копию настроек; используются значения по умолчанию.", backupEx);
                settings = new LauncherSettings();
            }
        }

        settings.InstallationPaths ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        settings.Proxy ??= new ProxySettings();
        if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
            settings.DownloadFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        settings.SchemaVersion = 1;
        settings.Proxy.Password = Unprotect(settings.Proxy.EncryptedPassword);
        return settings;
    }

    public static void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        settings.SchemaVersion = 1;
        settings.Proxy ??= new ProxySettings();
        settings.Proxy.EncryptedPassword = Protect(settings.Proxy.Password);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(SettingsPath))
        {
            var backup = SettingsPath + ".bak";
            try { File.Replace(temp, SettingsPath, backup, ignoreMetadataErrors: true); }
            catch { File.Copy(temp, SettingsPath, overwrite: true); File.Delete(temp); }
        }
        else File.Move(temp, SettingsPath);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings", "Не удалось зашифровать пароль прокси DPAPI; пароль не сохранён.", ex);
            return "";
        }
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings", "Не удалось расшифровать пароль прокси. Его нужно ввести заново.", ex);
            return "";
        }
    }
}
