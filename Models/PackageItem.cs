using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CoolSnesh.Avalonia.Models;

public sealed class PackageItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _version = "Не проверена";
    private string _installedVersion = "";
    private string _installationStatus = "Проверка установки…";
    private string _installationPath = "";
    private string _versionCheckWarning = "";

    public string Name { get; init; } = "";
    public string Category { get; init; } = "Программы";
    public string Repo { get; init; } = "";
    public string FilePattern { get; init; } = "";
    public string DirectUrl { get; init; } = "";
    public string WebsiteUrl { get; init; } = "";
    public string VersionRepo { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Items intended for downloading only (ISOs and portable media tools) are not installed or updated by this app.</summary>
    public bool IsDownloadOnly { get; init; }
    public bool IsIsoImage { get; init; }
    /// <summary>Request UAC for installers known to update machine-wide installations.</summary>
    public bool RequiresElevation { get; init; }
    public string IconUrlOverride { get; init; } = "";
    /// <summary>Embedded high-resolution PNG logo. When present, it takes precedence over the remote favicon.</summary>
    public string IconAsset { get; init; } = "";
    /// <summary>Visible caution displayed under the package name (for example, network prerequisites).</summary>
    public string DownloadNotice { get; init; } = "";
    public string IconUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(IconUrlOverride)) return IconUrlOverride;
            // A repository's favicon is GitHub's logo, not the packaged application's icon.
            // If there is no official product page or explicit icon URL, keep the branded glyph fallback.
            var officialUrl = WebsiteUrl;
            return string.IsNullOrWhiteSpace(officialUrl)
                ? ""
                : "https://www.google.com/s2/favicons?domain_url=" + Uri.EscapeDataString(officialUrl) + "&sz=128";
        }
    }
    public string TooltipDescription
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(Description)
                ? $"{Name}. Загрузка последней доступной версии с официального источника и проверка установленной версии."
                : Description;
            return string.IsNullOrWhiteSpace(DownloadNotice) ? description : description + Environment.NewLine + Environment.NewLine + DownloadNotice;
        }
    }
    public string FixedFileName { get; init; } = "";
    public string OutputName { get; init; } = "";
    public string Exclude { get; init; } = "";
    public string Accent { get; init; } = "#277FC2";
    public string Glyph { get; init; } = "APP";
    public string RegistryAliases { get; init; } = "";
    public string ExecutableNames { get; init; } = "";
    public string SilentArguments { get; init; } = "";
    public string Source => IsIsoImage ? "Google Drive · ссылка пользователя" : !string.IsNullOrWhiteSpace(Repo) ? "GitHub · " + Repo : !string.IsNullOrWhiteSpace(WebsiteUrl) ? new Uri(WebsiteUrl).Host : "Прямая ссылка";
    public bool IsFlowseal => Repo.StartsWith("Flowseal/", StringComparison.OrdinalIgnoreCase);
    public bool IsZapret => Repo.Equals("Flowseal/zapret-discord-youtube", StringComparison.OrdinalIgnoreCase);
    public bool IsFlowsealTgWsProxy => Repo.Equals("Flowseal/tg-ws-proxy", StringComparison.OrdinalIgnoreCase);
    public bool IsSteam => Name.Equals("Steam", StringComparison.OrdinalIgnoreCase);
    public bool HasKnownSilentInstall => IsZapret || IsFlowsealTgWsProxy || OutputName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(SilentArguments);

    public string Version
    {
        get => _version;
        set { if (_version != value) { _version = value; OnPropertyChanged(); OnPropertyChanged(nameof(VersionCaption)); } }
    }
    public string VersionCaption => IsIsoImage ? "Образ Windows · ISO" : string.IsNullOrWhiteSpace(Version) || Version == "Не определяется источником" ? "Актуальная версия источником не предоставляется" : Version == "Ошибка проверки" ? "Не удалось проверить релиз" : "Последняя версия: " + Version;

    /// <summary>Non-fatal refresh warning; a previously successful version may still be shown.</summary>
    public string VersionCheckWarning
    {
        get => _versionCheckWarning;
        set { if (_versionCheckWarning != value) { _versionCheckWarning = value; OnPropertyChanged(); } }
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        set { if (_installedVersion != value) { _installedVersion = value; OnPropertyChanged(); } }
    }
    public string InstallationStatus
    {
        get => _installationStatus;
        set { if (_installationStatus != value) { _installationStatus = value; OnPropertyChanged(); } }
    }
    public string InstallationPath
    {
        get => _installationPath;
        set { if (_installationPath != value) { _installationPath = value; OnPropertyChanged(); } }
    }
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
