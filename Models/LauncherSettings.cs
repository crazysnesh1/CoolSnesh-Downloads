namespace CoolSnesh.Avalonia.Models;

public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string DownloadFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public bool SilentUpdateEnabled { get; set; }
    public Dictionary<string, string> InstallationPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ProxySettings Proxy { get; set; } = new();
}
