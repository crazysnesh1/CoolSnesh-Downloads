using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CoolSnesh.Avalonia.Models;

public sealed class DownloadTaskViewModel : INotifyPropertyChanged
{
    private string _status = "В очереди";
    private int _progress;
    private bool _isCompleted;

    public PackageItem Package { get; }
    public string Name => Package.Name;
    public string Glyph => Package.Glyph;
    public string Accent => Package.Accent;
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }
    public int Progress
    {
        get => _progress;
        set { var next = Math.Clamp(value, 0, 100); if (_progress != next) { _progress = next; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressLabel)); } }
    }
    public string ProgressLabel => Progress + "%";
    public bool IsCompleted
    {
        get => _isCompleted;
        set { if (_isCompleted != value) { _isCompleted = value; OnPropertyChanged(); } }
    }

    public DownloadTaskViewModel(PackageItem package) => Package = package;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
