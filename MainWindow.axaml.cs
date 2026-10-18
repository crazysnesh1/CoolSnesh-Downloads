using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CoolSnesh.Avalonia.Models;
using CoolSnesh.Avalonia.Services;

namespace CoolSnesh.Avalonia;

public partial class MainWindow : Window
{
    private readonly List<PackageItem> _packages = PackageCatalog.CreateDefault();
    private readonly ObservableCollection<DownloadTaskViewModel> _downloadTasks = new();
    private LauncherSettings _settings;
    private DownloadService _downloadService;
    private HttpClient _versionClient;

    private string _downloadFolder;
    private string _currentPage = "catalog";
    private string _selectedCategory = "Все приложения";
    private string _searchText = "";
    private bool _isDownloading;
    private CancellationTokenSource? _downloadCancellation;

    private StackPanel? _catalogRows;
    private TextBlock? _selectedCountLabel;
    private TextBlock? _catalogNotice;
    private Button? _downloadSelectedButton;
    private Button? _updateSelectedButton;
    private TextBlock? _settingsNotice;
    private ToggleSwitch? _proxyEnabledSwitch;
    private bool _isApplyingProxyPreset;
    private ComboBox? _proxyTypeCombo;
    private TextBox? _proxyHostBox;
    private TextBox? _proxyPortBox;
    private TextBox? _proxyUsernameBox;
    private TextBox? _proxyPasswordBox;
    private DispatcherTimer? _appUpdateTimer;
    private AppUpdateInfo? _availableAppUpdate;
    private bool _isCheckingAppUpdate;
    private bool _isApplyingAppUpdate;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        _downloadFolder = _settings.DownloadFolder;
        // Legacy paths remain in settings for backward compatibility, but only Flowseal tools consume manual paths.
        foreach (var package in _packages)
            if (package.IsFlowseal && _settings.InstallationPaths.TryGetValue(package.Name, out var path) &&
                (File.Exists(path) || Directory.Exists(path))) package.InstallationPath = path;

        _downloadService = new DownloadService(_settings.Proxy, maxParallelDownloads: 5);
        _versionClient = CreateVersionClient(_settings.Proxy);
        SidebarFolderLabel.Text = _downloadFolder;
        AppVersionLabel.Text = $"Версия {AppUpdateService.GetCurrentVersion()}";
        SetApplicationIcon();
        ShowCatalogPage();
        _ = RefreshAllVersionsAndInstallationsAsync();

        // Check for the launcher itself separately from package version checks.
        Opened += (_, _) => _ = CheckForAppUpdateAsync(showResult: false);
        _appUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _appUpdateTimer.Tick += (_, _) => _ = CheckForAppUpdateAsync(showResult: false);
        _appUpdateTimer.Start();
    }

    private static HttpClient CreateVersionClient(ProxySettings proxySettings) =>
        NetworkClientFactory.Create(proxySettings, TimeSpan.FromSeconds(20));

    private void SetApplicationIcon()
    {
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CoolSnesh.Avalonia/Assets/App.ico"))); }
        catch (Exception ex) { AppLog.Write("Icon", "Не удалось загрузить иконку окна; будет использована системная иконка.", ex); }
    }

    private async Task CheckForAppUpdateAsync(bool showResult)
    {
        if (_isCheckingAppUpdate || _isApplyingAppUpdate) return;
        _isCheckingAppUpdate = true;
        try
        {
            using var checker = new AppUpdateService(NetworkClientFactory.Create(_settings.Proxy, TimeSpan.FromSeconds(30)));
            var result = await checker.CheckLatestAsync(AppUpdateService.GetCurrentVersion());
            if (result.Update is not null || result.LatestVersionKnown)
                _availableAppUpdate = result.Update;
            // Keep a previously discovered update visible if GitHub temporarily fails.
            AppUpdateButton.IsVisible = _availableAppUpdate is not null;
            AppUpdateButton.IsEnabled = true;

            if (_availableAppUpdate is not null)
            {
                AppUpdateButton.Content = $"Обновить до {_availableAppUpdate.Version}";
                ToolTip.SetTip(AppUpdateButton, $"Доступна новая версия {_availableAppUpdate.Version}. Нажми, чтобы обновить CoolSnesh Downloads.");
                AppLog.Write("AppUpdate", $"Найдена новая версия {_availableAppUpdate.Version}; установлена {AppUpdateService.GetCurrentVersion()}.");
            }
            else
            {
                AppLog.Write("AppUpdate", result.Message);
                ToolTip.SetTip(AppUpdateButton, "Доступна новая версия CoolSnesh Downloads");
                if (showResult)
                    await ShowMessageAsync("Проверка обновления CoolSnesh Downloads", result.Message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("AppUpdate", "Не удалось проверить обновление CoolSnesh Downloads.", ex);
            if (showResult)
                await ShowMessageAsync("Не удалось проверить обновление", "GitHub временно недоступен или соединение не удалось установить. Приложение продолжит работать; попробуй позже.\n\n" + ex.Message);
        }
        finally
        {
            _isCheckingAppUpdate = false;
            if (!_isApplyingAppUpdate) AppUpdateButton.IsEnabled = true;
        }
    }

    private async void OnAppUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_availableAppUpdate is null || _isApplyingAppUpdate) return;

        var update = _availableAppUpdate;
        if (!await ConfirmAppUpdateAsync(update)) return;

        _isApplyingAppUpdate = true;
        AppUpdateButton.IsEnabled = false;
        AppUpdateButton.Content = "Подготовка обновления…";
        try
        {
            var currentExecutable = Environment.ProcessPath;
            using var updater = new AppUpdateService(NetworkClientFactory.Create(_settings.Proxy, TimeSpan.FromMinutes(30)));
            var result = await updater.DownloadAndScheduleUpdateAsync(update, currentExecutable ?? string.Empty, Environment.ProcessId);
            if (!result.Started)
            {
                AppUpdateButton.Content = $"Обновить до {update.Version}";
                AppUpdateButton.IsEnabled = true;
                _isApplyingAppUpdate = false;
                await ShowMessageAsync("Не удалось подготовить обновление", result.Message);
                return;
            }

            AppLog.Write("AppUpdate", $"Помощник обновления запущен для версии {update.Version}. Приложение закрывается для замены EXE.");
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("AppUpdate", "Ошибка при запуске обновления CoolSnesh Downloads.", ex);
            AppUpdateButton.Content = $"Обновить до {update.Version}";
            AppUpdateButton.IsEnabled = true;
            _isApplyingAppUpdate = false;
            await ShowMessageAsync("Ошибка обновления", ex.Message);
        }
    }

    private async Task<bool> ConfirmAppUpdateAsync(AppUpdateInfo update)
    {
        var dialog = new Window
        {
            Title = "Обновление CoolSnesh Downloads",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brush("#141D2D")
        };

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(22) };
        body.Children.Add(Txt($"Доступна новая версия {update.Version}", 19, "#F1F5FC", FontWeight.SemiBold));
        var summary = Txt($"Текущая версия: {AppUpdateService.GetCurrentVersion()}\nНовая версия: {update.Version}\n\nEXE будет загружен во временную папку и проверен по SHA-256. Затем приложение закроется, заменит файл и запустится снова. Предыдущая версия сохранится в резервной копии.", 13, "#C7D4E7");
        summary.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(summary);

        if (!string.IsNullOrWhiteSpace(update.ReleaseNotes))
        {
            var releaseNotes = update.ReleaseNotes.Trim();
            if (releaseNotes.Length > 1800) releaseNotes = releaseNotes[..1800] + "…";
            var notesLabel = Txt("Что нового", 13, "#90CFFF", FontWeight.SemiBold);
            body.Children.Add(notesLabel);
            var notes = Txt(releaseNotes, 12, "#A9B8CD");
            notes.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(notes);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var laterButton = new Button { Content = "Позже", MinWidth = 100 };
        laterButton.Click += (_, _) => dialog.Close(false);
        var updateButton = new Button { Content = "Скачать и обновить", MinWidth = 160, Classes = { "primary" } };
        updateButton.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(laterButton);
        actions.Children.Add(updateButton);
        body.Children.Add(actions);
        dialog.Content = body;
        return await dialog.ShowDialog<bool>(this);
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text?.Trim() ?? "";
        if (_currentPage == "catalog") RefreshCatalogRows();
    }

    private void OnCatalogClick(object? sender, RoutedEventArgs e) => ShowCatalogPage();
    private void OnDownloadsClick(object? sender, RoutedEventArgs e) => ShowDownloadsPage();
    private void OnHistoryClick(object? sender, RoutedEventArgs e) => ShowHistoryPage();
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettingsPage();
    private void OnRefreshVersionsClick(object? sender, RoutedEventArgs e)
    {
        _ = RefreshAllVersionsAndInstallationsAsync();
        _ = CheckForAppUpdateAsync(showResult: false);
    }

    private void ShowCatalogPage()
    {
        _currentPage = "catalog";
        UpdateNavigation();
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 14 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new StackPanel { Spacing = 4 };
        titleBlock.Children.Add(Txt("Каталог приложений", 26, "#F1F5FC", FontWeight.SemiBold));
        titleBlock.Children.Add(Txt("Актуальные версии, состояние установки и массовое обновление программ.", 12, "#8FA0B9"));
        header.Children.Add(titleBlock);
        var countBadge = new Border { Background = Brush("#1D2B40"), BorderBrush = Brush("#30415B"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), VerticalAlignment = VerticalAlignment.Center };
        countBadge.Child = Txt($"{_packages.Count} приложений", 12, "#9BB2CD", FontWeight.Medium);
        Grid.SetColumn(countBadge, 1);
        header.Children.Add(countBadge);
        root.Children.Add(header);

        var filterGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*"), ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var category in new[] { "Все приложения", "Для России", "Программы", "Игровые лаунчеры", "Браузеры", "Windows" })
        {
            var button = new Button { Content = category, Padding = new Thickness(13, 8), Tag = category };
            ApplyFilterButtonStyle(button, category == _selectedCategory);
            button.Click += (_, _) => { _selectedCategory = category; ShowCatalogPage(); };
            filters.Children.Add(button);
        }
        var filtersScroll = new ScrollViewer
        {
            Content = filters,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        filterGrid.Children.Add(filtersScroll);
        Grid.SetRow(filterGrid, 1);
        root.Children.Add(filterGrid);

        var table = new Border { Background = Brush("#172235"), BorderBrush = Brush("#2A394F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true };
        var tableGrid = new Grid { RowDefinitions = new RowDefinitions("42,*") };
        var tableHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("36,1.8*,2.6*,1.45*,40"), Background = Brush("#202C40") };
        var selectAll = new CheckBox { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0) };
        selectAll.IsChecked = FilteredPackages().Any() && FilteredPackages().All(p => p.IsSelected);
        selectAll.IsCheckedChanged += (_, _) =>
        {
            var checkedValue = selectAll.IsChecked == true;
            foreach (var package in FilteredPackages()) package.IsSelected = checkedValue;
            RefreshCatalogRows();
        };
        tableHeader.Children.Add(selectAll);
        AddHeaderCell(tableHeader, "ПРИЛОЖЕНИЕ", 1);
        AddHeaderCell(tableHeader, "ВЕРСИЯ / СОСТОЯНИЕ", 2);
        AddHeaderCell(tableHeader, "ИСТОЧНИК", 3);
        AddHeaderCell(tableHeader, "САЙТ", 4);
        Grid.SetRow(tableHeader, 0);
        tableGrid.Children.Add(tableHeader);
        _catalogRows = new StackPanel { Spacing = 0 };
        var listScroll = new ScrollViewer { Content = _catalogRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(listScroll, 1);
        tableGrid.Children.Add(listScroll);
        table.Child = tableGrid;
        Grid.SetRow(table, 2);
        root.Children.Add(table);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var footerStatus = new StackPanel { Spacing = 4 };
        _selectedCountLabel = Txt("0 выбрано", 12, "#B8C7DA", FontWeight.SemiBold);
        _catalogNotice = Txt("Отметь приложения, которые нужно скачать или обновить.", 11, "#8192AC");
        footerStatus.Children.Add(_selectedCountLabel);
        footerStatus.Children.Add(_catalogNotice);
        footer.Children.Add(footerStatus);
        var folderButton = new Button { Content = "Папка загрузки", VerticalAlignment = VerticalAlignment.Center };
        folderButton.Click += OnChooseFolderClick;
        Grid.SetColumn(folderButton, 1);
        footer.Children.Add(folderButton);
        _updateSelectedButton = new Button { Content = "↻  Обновить выбранные", Padding = new Thickness(16, 10), VerticalAlignment = VerticalAlignment.Center };
        _updateSelectedButton.Click += OnUpdateSelectedClick;
        Grid.SetColumn(_updateSelectedButton, 2);
        footer.Children.Add(_updateSelectedButton);
        _downloadSelectedButton = new Button { Content = "↓  Скачать выбранные", Classes = { "primary" }, Padding = new Thickness(16, 10), VerticalAlignment = VerticalAlignment.Center };
        _downloadSelectedButton.Click += OnStartDownloadsClick;
        Grid.SetColumn(_downloadSelectedButton, 3);
        footer.Children.Add(_downloadSelectedButton);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        PageHost.Content = root;
        RefreshCatalogRows();
    }

    private IEnumerable<PackageItem> FilteredPackages() => _packages.Where(p =>
        (_selectedCategory == "Все приложения" || p.Category == _selectedCategory) &&
        (string.IsNullOrWhiteSpace(_searchText) || p.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
         p.Category.Contains(_searchText, StringComparison.OrdinalIgnoreCase) || p.Repo.Contains(_searchText, StringComparison.OrdinalIgnoreCase)));

    private void RefreshCatalogRows()
    {
        if (_catalogRows is null || _currentPage != "catalog") return;
        _catalogRows.Children.Clear();
        foreach (var package in FilteredPackages()) _catalogRows.Children.Add(BuildPackageRow(package));
        if (_catalogRows.Children.Count == 0)
            _catalogRows.Children.Add(new Border { Padding = new Thickness(20), Child = Txt("Ничего не найдено. Попробуй изменить запрос.", 13, "#8FA0B9") });

        var selected = _packages.Count(p => p.IsSelected);
        var updateableSelected = _packages.Count(p => p.IsSelected && !p.IsDownloadOnly);
        if (_selectedCountLabel is not null) _selectedCountLabel.Text = $"{selected} выбрано";
        if (_downloadSelectedButton is not null)
        {
            _downloadSelectedButton.IsEnabled = selected > 0 && !_isDownloading;
            _downloadSelectedButton.Content = _isDownloading ? "Выполняется…" : "↓  Скачать выбранные";
        }
        if (_updateSelectedButton is not null)
        {
            _updateSelectedButton.IsEnabled = updateableSelected > 0 && !_isDownloading;
            _updateSelectedButton.Content = _isDownloading ? "Выполняется…" : "↻  Обновить выбранные";
        }
        if (_catalogNotice is not null && !_isDownloading && selected > 0)
            _catalogNotice.Text = $"Выбрано {selected}. Папка загрузки: {_downloadFolder}";
    }

    private Control BuildPackageRow(PackageItem package)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("36,1.8*,2.6*,1.45*,40"), MinHeight = 86, Background = Brush("#172235") };
        var check = new CheckBox { IsChecked = package.IsSelected, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        check.IsCheckedChanged += (_, _) =>
        {
            package.IsSelected = check.IsChecked == true;
            if (_selectedCountLabel is not null) _selectedCountLabel.Text = $"{_packages.Count(p => p.IsSelected)} выбрано";
            var anySelected = _packages.Any(p => p.IsSelected) && !_isDownloading;
            var anyUpdateableSelected = _packages.Any(p => p.IsSelected && !p.IsDownloadOnly) && !_isDownloading;
            if (_downloadSelectedButton is not null) _downloadSelectedButton.IsEnabled = anySelected;
            if (_updateSelectedButton is not null) _updateSelectedButton.IsEnabled = anyUpdateableSelected;
        };
        row.Children.Add(check);

        // A horizontal StackPanel measures children with unconstrained width, which can let long notices
        // paint over the Version column. Use a star-sized text column so TextWrapping is effective.
        var identity = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var infoIcon = Txt("ⓘ", 14, "#8298B6", FontWeight.SemiBold);
        infoIcon.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(infoIcon, new TextBlock
        {
            Text = package.TooltipDescription,
            MaxWidth = 320,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8)
        });
        identity.Children.Add(infoIcon);

        var fallbackIcon = new TextBlock { Text = package.Glyph, FontSize = package.Glyph.Length > 2 ? 12 : 15, FontWeight = FontWeight.Bold, Foreground = Brush(package.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var logoImage = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var iconContent = new Grid();
        iconContent.Children.Add(fallbackIcon);
        iconContent.Children.Add(logoImage);
        var icon = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Child = iconContent };
        Grid.SetColumn(icon, 1);
        identity.Children.Add(icon);
        _ = LoadPackageIconAsync(package, logoImage, fallbackIcon);
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        labels.Children.Add(Txt(package.Name, 13, "#EAF1FA", FontWeight.SemiBold));
        labels.Children.Add(Txt(package.Category, 10, "#8092AD"));
        if (!string.IsNullOrWhiteSpace(package.DownloadNotice))
        {
            var notice = Txt("⚠ " + package.DownloadNotice, 9, "#F1C56C", FontWeight.Medium);
            notice.TextWrapping = TextWrapping.Wrap;
            notice.HorizontalAlignment = HorizontalAlignment.Stretch;
            labels.Children.Add(notice);
        }
        Grid.SetColumn(labels, 2);
        identity.Children.Add(labels);
        Grid.SetColumn(identity, 1);
        row.Children.Add(identity);

        var versionBlock = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        versionBlock.Children.Add(Txt(package.VersionCaption, 10, "#8294AE"));
        if (!string.IsNullOrWhiteSpace(package.VersionCheckWarning))
        {
            var warning = Txt("⚠ " + package.VersionCheckWarning, 9, "#E9BC68");
            warning.TextWrapping = TextWrapping.Wrap;
            versionBlock.Children.Add(warning);
        }
        var rowStatus = package.IsIsoImage ? "ISO-образ · доступно скачивание" : package.InstallationStatus;
        var statusColor = rowStatus.StartsWith("Актуально", StringComparison.OrdinalIgnoreCase) ? "#67D3A0"
            : rowStatus.StartsWith("Доступно обновление", StringComparison.OrdinalIgnoreCase) ? "#F3C56A"
            : rowStatus.StartsWith("Не установлено", StringComparison.OrdinalIgnoreCase) ? "#8495AE" : "#B6C8DF";
        var installStatus = Txt(rowStatus, 11, statusColor, FontWeight.Medium);
        installStatus.TextWrapping = TextWrapping.Wrap;
        versionBlock.Children.Add(installStatus);
        Grid.SetColumn(versionBlock, 2);
        row.Children.Add(versionBlock);

        var source = Txt(package.Source, 11, "#9DADC4");
        source.VerticalAlignment = VerticalAlignment.Center;
        source.TextTrimming = TextTrimming.CharacterEllipsis;
        source.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(source, 3);
        row.Children.Add(source);

        // Manual paths are intentionally absent from catalogue rows; Flowseal fallback paths live in Settings.
        var link = new Button { Content = "↗", Padding = new Thickness(5, 4), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = !string.IsNullOrWhiteSpace(package.WebsiteUrl) || !string.IsNullOrWhiteSpace(package.Repo) };
        link.Click += (_, _) => OpenUrl(!string.IsNullOrWhiteSpace(package.WebsiteUrl) ? package.WebsiteUrl : $"https://github.com/{package.Repo}/releases");
        Grid.SetColumn(link, 4);
        row.Children.Add(link);

        var wrapper = new Border { Padding = new Thickness(10, 8), BorderBrush = Brush("#25344A"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row, Background = Brush("#172235") };
        var contextMenu = new ContextMenu();
        var downloadContext = new MenuItem { Header = package.IsIsoImage ? "Скачать ISO-образ" : package.IsDownloadOnly ? "Скачать файл" : "Скачать установщик" };
        downloadContext.Click += (_, _) => _ = ProcessPackagesAsync(new[] { package }, updateSelected: false, clearSelection: false);
        var updateContext = new MenuItem
        {
            Header = package.IsDownloadOnly ? "Обновление не применяется" : "Обновить приложение",
            IsEnabled = !package.IsDownloadOnly
        };
        updateContext.Click += (_, _) => _ = ProcessPackagesAsync(new[] { package }, updateSelected: true, clearSelection: false);
        var siteContext = new MenuItem { Header = "Открыть официальный сайт", IsEnabled = !string.IsNullOrWhiteSpace(package.WebsiteUrl) };
        siteContext.Click += (_, _) => OpenUrl(package.WebsiteUrl);
        contextMenu.Items.Add(downloadContext);
        contextMenu.Items.Add(updateContext);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(siteContext);
        wrapper.ContextMenu = contextMenu;
        wrapper.PointerEntered += (_, _) => { row.Background = Brush("#1D2B40"); wrapper.Background = Brush("#1D2B40"); };
        wrapper.PointerExited += (_, _) => { row.Background = Brush("#172235"); wrapper.Background = Brush("#172235"); };
        return wrapper;
    }

    private async Task LoadPackageIconAsync(PackageItem package, Image image, TextBlock fallback)
    {
        try
        {
            var bitmap = await PackageIconService.LoadAsync(package, _versionClient);
            if (bitmap is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                image.Source = bitmap;
                image.IsVisible = true;
                fallback.IsVisible = false;
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("PackageIcon", $"Не удалось показать значок {package.Name}.", ex);
        }
    }

    private void ShowDownloadsPage()
    {
        _currentPage = "downloads";
        UpdateNavigation();
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 16 };
        root.Children.Add(PageHeader("Загрузки", "Прогресс скачивания и установки, результаты и сообщения об ошибках."));
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(Txt($"Задач в списке: {_downloadTasks.Count}", 12, "#9AABC2"));
        var cancel = new Button { Content = "Отменить активные", IsEnabled = _isDownloading, Padding = new Thickness(12, 7) };
        cancel.Click += (_, _) => _downloadCancellation?.Cancel();
        Grid.SetColumn(cancel, 1);
        summary.Children.Add(cancel);
        Grid.SetRow(summary, 1);
        root.Children.Add(summary);
        var list = new StackPanel { Spacing = 9 };
        if (_downloadTasks.Count == 0)
            list.Children.Add(EmptyState("⇩", "Пока нет загрузок", "Выбери приложения в каталоге и нажми «Скачать выбранные» или «Обновить выбранные»."));
        else foreach (var task in _downloadTasks.ToList()) list.Children.Add(BuildTaskRow(task, showProgress: true));
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        PageHost.Content = root;
    }

    private void ShowHistoryPage()
    {
        _currentPage = "history";
        UpdateNavigation();
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 16 };
        root.Children.Add(PageHeader("История", "Результаты последних загрузок и обновлений в текущем сеансе."));
        var list = new StackPanel { Spacing = 9 };
        var completed = _downloadTasks.Where(t => t.IsCompleted).ToList();
        if (completed.Count == 0) list.Children.Add(EmptyState("◷", "История пока пуста", "Здесь появятся результаты после первой загрузки или установки."));
        else foreach (var task in completed.AsEnumerable().Reverse()) list.Children.Add(BuildTaskRow(task, showProgress: false));
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        PageHost.Content = root;
    }

    private void ShowSettingsPage()
    {
        _currentPage = "settings";
        UpdateNavigation();
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(PageHeader("Настройки", "Папка загрузки, параметры обновления и сетевое соединение."));

        var folderCard = Card(new StackPanel { Spacing = 12 });
        var folderContent = (StackPanel)folderCard.Child!;
        folderContent.Children.Add(Txt("Папка сохранения", 16, "#EEF3FB", FontWeight.SemiBold));
        folderContent.Children.Add(Txt("Обычные загрузки сохраняются сюда. При обновлении установщики запускаются из временной папки и удаляются после завершения.", 12, "#8FA0B9"));
        var path = new Border { Background = Brush("#111B2B"), BorderBrush = Brush("#2C3A50"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12) };
        path.Child = Txt(_downloadFolder, 12, "#CAD6E7");
        folderContent.Children.Add(path);
        var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var change = new Button { Content = "Выбрать папку", Classes = { "primary" } };
        change.Click += OnChooseFolderClick;
        folderActions.Children.Add(change);
        var open = new Button { Content = "Открыть папку" };
        open.Click += (_, _) => OpenFolder(_downloadFolder);
        folderActions.Children.Add(open);
        folderContent.Children.Add(folderActions);
        root.Children.Add(folderCard);

        var updateCard = Card(new StackPanel { Spacing = 9 });
        var updateContent = (StackPanel)updateCard.Child!;
        updateContent.Children.Add(Txt("Обновление приложений", 16, "#EEF3FB", FontWeight.SemiBold));
        updateContent.Children.Add(Txt("Кнопка «Скачать выбранные» сохраняет установщики. Кнопка «Обновить выбранные» запускает обновление: для поддерживаемых установщиков используются известные параметры тихой установки, а для остальных открывается обычное окно установщика. Неизвестные флаги запуска не подбираются автоматически.", 11, "#8FA0B9") );
        root.Children.Add(updateCard);

        root.Children.Add(BuildFlowsealPathSettingsCard());

        var proxyCard = Card(new StackPanel { Spacing = 12 });
        var proxyContent = (StackPanel)proxyCard.Child!;
        proxyContent.Children.Add(Txt("Прокси", 16, "#EEF3FB", FontWeight.SemiBold));
        proxyContent.Children.Add(Txt("Прокси применяется к проверке версий GitHub, загрузкам и проверке соединения. Пароль сохраняется в конфиге в зашифрованном виде через Windows DPAPI.", 12, "#8FA0B9"));
        proxyContent.Children.Add(Txt("Использовать прокси", 13, "#CAD6E7", FontWeight.Medium));
        _proxyEnabledSwitch = new ToggleSwitch
        {
            OnContent = "Включено",
            OffContent = "Выключено",
            IsChecked = _settings.Proxy.Enabled,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _proxyEnabledSwitch.IsCheckedChanged += (_, _) => OnProxyEnabledSwitchChanged();
        proxyContent.Children.Add(_proxyEnabledSwitch);
        _proxyTypeCombo = new ComboBox { ItemsSource = new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" }, SelectedItem = _settings.Proxy.Type, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        proxyContent.Children.Add(LabeledControl("Тип прокси", _proxyTypeCombo));
        _proxyHostBox = new TextBox { Text = _settings.Proxy.Host, PlaceholderText = "127.0.0.1" };
        proxyContent.Children.Add(LabeledControl("Хост", _proxyHostBox));
        _proxyPortBox = new TextBox { Text = _settings.Proxy.Port.ToString(), PlaceholderText = "8080" };
        proxyContent.Children.Add(LabeledControl("Порт", _proxyPortBox));
        _proxyUsernameBox = new TextBox { Text = _settings.Proxy.Username, PlaceholderText = "Необязательно" };
        proxyContent.Children.Add(LabeledControl("Логин", _proxyUsernameBox));
        _proxyPasswordBox = new TextBox { Text = _settings.Proxy.Password, PasswordChar = '●', PlaceholderText = "Необязательно" };
        _proxyHostBox.TextChanged += OnProxyHostTextChanged;
        proxyContent.Children.Add(LabeledControl("Пароль", _proxyPasswordBox));
        proxyContent.Children.Add(Txt("Можно вставить одним текстом: ip:port:login:password. Поля заполнятся автоматически; затем нажми «Сохранить настройки».", 11, "#8294AE"));
        var proxyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var saveProxy = new Button { Content = "Сохранить настройки", Classes = { "primary" } };
        saveProxy.Click += OnSaveProxySettingsClick;
        proxyActions.Children.Add(saveProxy);
        var testProxy = new Button { Content = "Проверить соединение" };
        testProxy.Click += OnTestProxyClick;
        proxyActions.Children.Add(testProxy);
        proxyContent.Children.Add(proxyActions);
        _settingsNotice = Txt("Настройки сохраняются в %AppData%\\CoolSneshDownloads\\settings.json.", 11, "#8294AE");
        proxyContent.Children.Add(_settingsNotice);
        root.Children.Add(proxyCard);

        var networkCard = Card(new StackPanel { Spacing = 9 });
        var network = (StackPanel)networkCard.Child!;
        network.Children.Add(Txt("Диагностика и ограничения", 16, "#EEF3FB", FontWeight.SemiBold));
        network.Children.Add(SettingLine("Проверка версии", "Для GitHub-пакетов используется Releases API; для Proxifier читается официальная страница, для Steam — манифест клиента Valve."));
        network.Children.Add(SettingLine("Реестр Windows", "Версия установленных приложений определяется по uninstall-реестру и метаданным EXE."));
        network.Children.Add(SettingLine("Прямые загрузки", "Если сайт не публикует версию отдельно от установщика, сравнение обновления не может быть подтверждено до запуска установщика."));
        root.Children.Add(networkCard);
        PageHost.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Control LabeledControl(string title, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), ColumnSpacing = 12, Margin = new Thickness(0, 1) };
        grid.Children.Add(Txt(title, 12, "#AAB9CE"));
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(control);
        return grid;
    }

    private async void OnSaveProxySettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_isDownloading || !RefreshButton.IsEnabled)
        {
            if (_settingsNotice is not null) _settingsNotice.Text = "Дождись завершения текущих сетевых операций перед сохранением прокси.";
            return;
        }
        try
        {
            var proxy = ReadProxyControls();
            // Saving connection fields must not change the independent on/off switch.
            var validation = proxy.Clone();
            validation.Enabled = true;
            NetworkClientFactory.Validate(validation);
            proxy.Enabled = _settings.Proxy.Enabled;

            // Construct a valid client with the provided connection data before replacing active clients.
            using var validationClient = NetworkClientFactory.Create(validation, TimeSpan.FromSeconds(10));
            _settings.Proxy = proxy;
            _settings.DownloadFolder = _downloadFolder;
            PersistSettings();

            var oldVersionClient = _versionClient;
            var oldDownloadService = _downloadService;
            _versionClient = CreateVersionClient(_settings.Proxy);
            _downloadService = new DownloadService(_settings.Proxy, maxParallelDownloads: 5);
            oldVersionClient.Dispose();
            oldDownloadService.Dispose();

            if (_settingsNotice is not null)
                _settingsNotice.Text = _settings.Proxy.Enabled
                    ? "Данные прокси сохранены. Прокси включён и применён."
                    : "Данные прокси сохранены. Переключатель не изменён — прокси остаётся выключен.";
            await RefreshAllVersionsAndInstallationsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Proxy", "Не удалось сохранить настройки прокси.", ex);
            if (_settingsNotice is not null) _settingsNotice.Text = "Ошибка: " + ex.Message;
        }
    }

    private async void OnTestProxyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var proxy = ReadProxyControls();
            // The connection test uses the currently entered fields even if the global switch is off.
            proxy.Enabled = true;
            NetworkClientFactory.Validate(proxy);
            using var client = NetworkClientFactory.Create(proxy, TimeSpan.FromSeconds(15));
            if (_settingsNotice is not null) _settingsNotice.Text = "Проверяем соединение через указанные параметры прокси…";
            using var response = await client.GetAsync("https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest");
            response.EnsureSuccessStatusCode();
            if (_settingsNotice is not null) _settingsNotice.Text = $"Соединение работает. GitHub ответил: HTTP {(int)response.StatusCode}.";
            AppLog.Write("Proxy", $"Проверка прокси завершена успешно: HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            AppLog.Write("Proxy", "Проверка соединения через прокси не удалась.", ex);
            if (_settingsNotice is not null) _settingsNotice.Text = "Проверка не пройдена: " + ex.Message;
        }
    }

    private ProxySettings ReadProxyControls()
    {
        if (_proxyTypeCombo is null || _proxyHostBox is null || _proxyPortBox is null || _proxyUsernameBox is null || _proxyPasswordBox is null)
            return _settings.Proxy.Clone();
        if (!int.TryParse(_proxyPortBox.Text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port))
            throw new InvalidOperationException("Порт должен быть целым числом от 1 до 65535.");
        return new ProxySettings
        {
            // Enabled belongs exclusively to the toggle and is intentionally not read from text fields.
            Enabled = _settings.Proxy.Enabled,
            Type = _proxyTypeCombo.SelectedItem?.ToString() ?? "HTTP",
            Host = _proxyHostBox.Text?.Trim() ?? "",
            Port = port,
            Username = _proxyUsernameBox.Text?.Trim() ?? "",
            Password = _proxyPasswordBox.Text ?? ""
        };
    }

    private void OnProxyHostTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isApplyingProxyPreset || _proxyHostBox is null || _proxyPortBox is null ||
            _proxyUsernameBox is null || _proxyPasswordBox is null)
            return;

        if (!ProxyEndpointParser.TryParse(_proxyHostBox.Text, out var parsed)) return;
        _isApplyingProxyPreset = true;
        try
        {
            _proxyHostBox.Text = parsed.Host;
            _proxyPortBox.Text = parsed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _proxyUsernameBox.Text = parsed.Username;
            _proxyPasswordBox.Text = parsed.Password;
            if (!string.IsNullOrWhiteSpace(parsed.Type) && _proxyTypeCombo is not null)
                _proxyTypeCombo.SelectedItem = parsed.Type;
            if (_settingsNotice is not null)
                _settingsNotice.Text = "Параметры прокси распознаны. Нажми «Сохранить настройки», чтобы сохранить их.";
        }
        finally { _isApplyingProxyPreset = false; }
    }

    private void OnProxyEnabledSwitchChanged()
    {
        if (_proxyEnabledSwitch is null || _isApplyingProxyPreset) return;
        var requestedEnabled = _proxyEnabledSwitch.IsChecked == true;
        if (requestedEnabled == _settings.Proxy.Enabled) return;

        if (_isDownloading || !RefreshButton.IsEnabled)
        {
            _isApplyingProxyPreset = true;
            _proxyEnabledSwitch.IsChecked = _settings.Proxy.Enabled;
            _isApplyingProxyPreset = false;
            if (_settingsNotice is not null) _settingsNotice.Text = "Дождись завершения текущих сетевых операций перед переключением прокси.";
            return;
        }

        try
        {
            var candidate = _settings.Proxy.Clone();
            candidate.Enabled = requestedEnabled;
            if (requestedEnabled) NetworkClientFactory.Validate(candidate);

            var newVersionClient = CreateVersionClient(candidate);
            DownloadService newDownloadService;
            try { newDownloadService = new DownloadService(candidate, maxParallelDownloads: 5); }
            catch { newVersionClient.Dispose(); throw; }

            var oldVersionClient = _versionClient;
            var oldDownloadService = _downloadService;
            _settings.Proxy = candidate;
            PersistSettings();
            _versionClient = newVersionClient;
            _downloadService = newDownloadService;
            oldVersionClient.Dispose();
            oldDownloadService.Dispose();

            if (_settingsNotice is not null)
                _settingsNotice.Text = requestedEnabled
                    ? "Прокси включён. Используются последние сохранённые параметры."
                    : "Прокси выключен. Сохранённые параметры не удалены.";
        }
        catch (Exception ex)
        {
            _isApplyingProxyPreset = true;
            _proxyEnabledSwitch.IsChecked = _settings.Proxy.Enabled;
            _isApplyingProxyPreset = false;
            AppLog.Write("Proxy", "Не удалось переключить прокси.", ex);
            if (_settingsNotice is not null) _settingsNotice.Text = "Не удалось применить прокси: " + ex.Message;
        }
    }

    private Control BuildTaskRow(DownloadTaskViewModel task, bool showProgress)
    {
        var panel = new StackPanel { Spacing = 10 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("50,*,Auto"), ColumnSpacing = 10 };
        var fallbackIcon = new TextBlock { Text = task.Glyph, FontSize = task.Glyph.Length > 2 ? 12 : 15, FontWeight = FontWeight.Bold, Foreground = Brush(task.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var logoImage = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var iconContent = new Grid();
        iconContent.Children.Add(fallbackIcon);
        iconContent.Children.Add(logoImage);
        var icon = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Child = iconContent };
        top.Children.Add(icon);
        _ = LoadPackageIconAsync(task.Package, logoImage, fallbackIcon);
        var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Txt(task.Name, 13, "#E8EEF8", FontWeight.SemiBold));
        var status = Txt(task.Status, 11, "#91A3BD");
        status.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadTaskViewModel.Status)) { Source = task });
        details.Children.Add(status);
        Grid.SetColumn(details, 1);
        top.Children.Add(details);
        var percent = Txt(task.ProgressLabel, 12, "#B5C6DB", FontWeight.SemiBold);
        percent.Bind(TextBlock.TextProperty, new Binding(nameof(DownloadTaskViewModel.ProgressLabel)) { Source = task });
        percent.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(percent, 2);
        top.Children.Add(percent);
        panel.Children.Add(top);
        if (showProgress)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Value = task.Progress, Background = Brush("#2A394F"), Foreground = Brush("#41A5EB") };
            bar.Bind(RangeBase.ValueProperty, new Binding(nameof(DownloadTaskViewModel.Progress)) { Source = task });
            panel.Children.Add(bar);
        }
        var card = Card(panel);
        card.Margin = new Thickness(0, 0, 0, 1);
        return card;
    }

    private static Control PageHeader(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(Txt(title, 26, "#F1F5FC", FontWeight.SemiBold));
        stack.Children.Add(Txt(subtitle, 12, "#8FA0B9"));
        return stack;
    }

    private static Control EmptyState(string glyph, string title, string description)
    {
        var stack = new StackPanel { Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        var badge = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(16), Background = Brush("#1D2C42"), HorizontalAlignment = HorizontalAlignment.Center };
        badge.Child = Txt(glyph, 26, "#8ED0FF", FontWeight.SemiBold);
        stack.Children.Add(badge);
        var titleBlock = Txt(title, 17, "#EAF1FA", FontWeight.SemiBold);
        titleBlock.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(titleBlock);
        var descriptionBlock = Txt(description, 12, "#8FA0B9");
        descriptionBlock.TextAlignment = TextAlignment.Center;
        descriptionBlock.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(descriptionBlock);
        return new Border { Padding = new Thickness(34), Child = stack, Background = Brush("#141F30"), BorderBrush = Brush("#27374F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 4) };
    }

    private void OnStartDownloadsClick(object? sender, RoutedEventArgs e) =>
        _ = ProcessPackagesAsync(_packages.Where(p => p.IsSelected).ToList(), updateSelected: false);

    private void OnUpdateSelectedClick(object? sender, RoutedEventArgs e)
    {
        var selected = _packages.Where(p => p.IsSelected).ToList();
        var updateable = selected.Where(p => !p.IsDownloadOnly).ToList();
        var skippedDownloadOnly = selected.Count - updateable.Count;
        if (updateable.Count == 0)
        {
            if (_catalogNotice is not null)
                _catalogNotice.Text = "Выбранные элементы предназначены только для скачивания; обновление к ним не применяется.";
            return;
        }
        _ = ProcessPackagesAsync(updateable, updateSelected: true, skippedDownloadOnlyCount: skippedDownloadOnly);
    }

    private async Task ProcessPackagesAsync(IEnumerable<PackageItem> packages, bool updateSelected, bool clearSelection = true, int skippedDownloadOnlyCount = 0)
    {
        if (_isDownloading) return;
        var selected = packages.Distinct().ToList();
        if (selected.Count == 0)
        {
            if (_catalogNotice is not null) _catalogNotice.Text = "Сначала отметь хотя бы одно приложение.";
            return;
        }

        if (updateSelected)
        {
            var invalidZapret = selected.FirstOrDefault(p => p.IsZapret && !InstalledVersionService.IsZapretRoot(p.InstallationPath));
            if (invalidZapret is not null)
            {
                if (_catalogNotice is not null) _catalogNotice.Text = "Для безопасного обновления Zapret укажи его папку установки (service.bat + bin\\winws.exe). Обновление отменено.";
                return;
            }
            var invalidPortableFlowseal = selected.FirstOrDefault(p => p.IsFlowsealTgWsProxy &&
                (string.IsNullOrWhiteSpace(p.InstallationPath) || !File.Exists(p.InstallationPath)));
            if (invalidPortableFlowseal is not null)
            {
                if (_catalogNotice is not null) _catalogNotice.Text = "Для обновления TG WS Proxy укажи путь к установленному TgWsProxy_windows.exe. Обновление отменено.";
                return;
            }
        }

        _isDownloading = true;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        if (clearSelection)
            foreach (var package in selected) package.IsSelected = false;
        if (_catalogNotice is not null)
            _catalogNotice.Text = updateSelected
                ? skippedDownloadOnlyCount > 0
                    ? $"Обновление запущено; элементы только для скачивания ({skippedDownloadOnlyCount}) пропущены."
                    : "Обновление запущено. Установщики обрабатываются последовательно."
                : "Загрузка запущена. Прогресс доступен в разделе «Загрузки».";
        RefreshCatalogRows();
        ShowDownloadsPage();
        var cancellationToken = _downloadCancellation.Token;
        try
        {
            foreach (var package in selected)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await RunDownloadOrUpdateAsync(package, cancellationToken, updateSelected);
            }
        }
        finally
        {
            _isDownloading = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            RefreshCatalogRows();
            if (_currentPage == "downloads") ShowDownloadsPage();
            else if (_currentPage == "history") ShowHistoryPage();
        }
        await CheckInstalledVersionsAsync();
    }

    private async Task RunDownloadOrUpdateAsync(PackageItem package, CancellationToken cancellationToken, bool installAfterDownload)
    {
        var task = new DownloadTaskViewModel(package);
        _downloadTasks.Insert(0, task);
        if (_currentPage == "downloads") ShowDownloadsPage();
        var downloadProgress = new Progress<DownloadProgress>(update =>
        {
            task.Status = update.Status;
            task.Progress = update.Percent;
        });
        var textProgress = new Progress<string>(status => task.Status = status);
        string? downloadedPath = null;
        string? stagingFolder = null;
        try
        {
            // Install/update payloads are staged in a unique TEMP folder, never in the user's
            // chosen download folder (which may be Desktop). Normal "Download" keeps the file.
            var targetFolder = _downloadFolder;
            if (installAfterDownload)
            {
                stagingFolder = Path.Combine(Path.GetTempPath(), "CoolSneshDownloads", "Installers", Guid.NewGuid().ToString("N"));
                targetFolder = stagingFolder;
            }

            task.Status = installAfterDownload ? "Получение временного установщика…" : "Получение установщика…";
            downloadedPath = await _downloadService.DownloadAsync(package, targetFolder, downloadProgress, cancellationToken);
            task.Progress = 100;
            if (!installAfterDownload)
            {
                task.Status = "Готово · файл сохранён";
                AppLog.Write("Download", $"Скачан файл для {package.Name}: {downloadedPath}");
            }
            else if (package.IsZapret)
            {
                task.Status = "Проверка стратегии Zapret…";
                var result = await ZapretUpdateService.UpdateAsync(package.InstallationPath, downloadedPath, textProgress, cancellationToken);
                task.Status = result.Success ? result.Message : "Ошибка обновления: " + result.Message;
                AppLog.Write("Update", $"Zapret: {result.Message}");
            }
            else if (package.IsFlowsealTgWsProxy)
            {
                var result = await PortableExecutableUpdateService.UpdateAsync(package.InstallationPath, downloadedPath, textProgress, cancellationToken);
                task.Status = result.Message;
                AppLog.Write("Update", $"TG WS Proxy: {result.Message}");
            }
            else
            {
                var result = await InstallerService.InstallOrUpdateAsync(package, downloadedPath, downloadProgress, cancellationToken);
                task.Status = result.Message;
                AppLog.Write("Update", $"{package.Name}: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = "Отменено пользователем";
            AppLog.Write("Download", $"Задача {package.Name} отменена.");
        }
        catch (Exception ex)
        {
            task.Status = "Ошибка: " + ex.Message;
            AppLog.Write("Download", $"Ошибка задачи {package.Name}.", ex);
        }
        finally
        {
            if (installAfterDownload)
            {
                // Temporary installer files are not retained after an update attempt. If an
                // installer timed out but is still alive, InstallerService schedules a second
                // deletion attempt when its process exits.
                InstallerService.TryDeleteInstaller(downloadedPath);
                if (!string.IsNullOrWhiteSpace(stagingFolder))
                {
                    try
                    {
                        if (Directory.Exists(stagingFolder) && !Directory.EnumerateFileSystemEntries(stagingFolder).Any())
                            Directory.Delete(stagingFolder);
                        var parent = Directory.GetParent(stagingFolder)?.FullName;
                        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                            Directory.Delete(parent);
                    }
                    catch (Exception cleanupEx)
                    {
                        AppLog.Write("Installer", $"Не удалось удалить временную папку {stagingFolder}.", cleanupEx);
                    }
                }
            }

            task.IsCompleted = true;
            if (_currentPage == "downloads") ShowDownloadsPage();
            else if (_currentPage == "history") ShowHistoryPage();
        }
    }

    private async void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Выбери папку для установщиков", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _downloadFolder = path;
        _settings.DownloadFolder = path;
        SidebarFolderLabel.Text = _downloadFolder;
        PersistSettings();
        if (_catalogNotice is not null) _catalogNotice.Text = $"Папка сохранения: {_downloadFolder}";
        if (_currentPage == "settings") ShowSettingsPage();
    }

    private async Task ChooseInstallFileAsync(PackageItem package)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = package.IsZapret ? "Укажи service.bat или bin\\winws.exe из папки Zapret" : $"Укажи исполняемый файл {package.Name}",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Исполняемые файлы") { Patterns = new[] { "*.exe", "*.bat" } }, FilePickerFileTypes.All }
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (package.IsZapret)
        {
            if (Path.GetFileName(path).Equals("service.bat", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
            else if (Path.GetFileName(path).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)) path = Directory.GetParent(Path.GetDirectoryName(path)!)?.FullName ?? path;
            if (!InstalledVersionService.IsZapretRoot(path))
            {
                await ShowMessageAsync("Путь Zapret не распознан", "Выбранный файл не находится в структуре с service.bat и bin\\winws.exe. Выбери папку установки.");
                return;
            }
        }
        SaveManualInstallationPath(package, path);
    }

    private async Task ChooseInstallFolderAsync(PackageItem package)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Выбери папку установки {package.Name}", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (package.IsZapret && !InstalledVersionService.IsZapretRoot(path))
        {
            var nested = Directory.Exists(path) ? Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(InstalledVersionService.IsZapretRoot) : null;
            if (nested is not null) path = nested;
            else
            {
                await ShowMessageAsync("Папка Zapret не распознана", "В выбранной папке не найдены service.bat и bin\\winws.exe.");
                return;
            }
        }
        SaveManualInstallationPath(package, path);
    }

    private void SaveManualInstallationPath(PackageItem package, string path)
    {
        if (!package.IsFlowseal) return;
        package.InstallationPath = path;
        _settings.InstallationPaths[package.Name] = path;
        PersistSettings();
        _ = CheckInstalledVersionsAsync();
        if (_currentPage == "settings") ShowSettingsPage();
        if (_currentPage == "catalog") RefreshCatalogRows();
    }

    private Control BuildFlowsealPathSettingsCard()
    {
        var card = Card(new StackPanel { Spacing = 12 });
        var content = (StackPanel)card.Child!;
        content.Children.Add(Txt("Пути Flowseal (резервное определение)", 16, "#EEF3FB", FontWeight.SemiBold));
        content.Children.Add(Txt("Если задан корректный ручной путь в настройках, при проверке используется именно он. Если путь не задан или устарел, применяется автоопределение.", 12, "#8FA0B9"));

        foreach (var package in _packages.Where(p => p.IsFlowseal))
        {
            var group = new StackPanel { Spacing = 6 };
            group.Children.Add(Txt(package.Name, 13, "#DCE7F7", FontWeight.SemiBold));
            var hasSaved = _settings.InstallationPaths.TryGetValue(package.Name, out var configuredPath) &&
                           (File.Exists(configuredPath) || Directory.Exists(configuredPath));
            var displayPath = hasSaved ? configuredPath! : package.InstallationPath;
            var pathLabel = Txt(string.IsNullOrWhiteSpace(displayPath)
                ? "Ручной путь не задан · используется автоопределение"
                : (hasSaved ? displayPath : $"Автоопределено: {displayPath}"), 10, "#8194AE");
            pathLabel.TextWrapping = TextWrapping.Wrap;
            group.Children.Add(pathLabel);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var choose = new Button { Content = package.IsZapret ? "Выбрать папку установки" : "Выбрать EXE" };
            choose.Click += async (_, _) =>
            {
                if (package.IsZapret) await ChooseInstallFolderAsync(package);
                else await ChooseInstallFileAsync(package);
            };
            actions.Children.Add(choose);
            var clear = new Button { Content = "Сбросить ручной путь", IsEnabled = hasSaved };
            clear.Click += (_, _) => ClearManualInstallationPath(package);
            actions.Children.Add(clear);
            group.Children.Add(actions);
            content.Children.Add(group);
        }

        return card;
    }

    private void ClearManualInstallationPath(PackageItem package)
    {
        if (!package.IsFlowseal) return;
        _settings.InstallationPaths.Remove(package.Name);
        package.InstallationPath = "";
        PersistSettings();
        ShowSettingsPage();
        _ = CheckInstalledVersionsAsync();
    }

    private async Task RefreshAllVersionsAndInstallationsAsync()
    {
        await LoadVersionsAsync();
        await CheckInstalledVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        if (!RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Проверка…";
        var targets = _packages.Where(VersionResolver.HasKnownSource).ToList();
        foreach (var package in _packages.Where(p => !VersionResolver.HasKnownSource(p)))
        {
            package.Version = VersionResolver.UnknownVersion;
            package.VersionCheckWarning = "";
        }
        // Keep endpoint pressure low; each source now has its own bounded retry policy.
        using var semaphore = new SemaphoreSlim(3);
        try
        {
            var tasks = targets.Select(async package =>
            {
                await semaphore.WaitAsync();
                var previousVersion = package.Version;
                try
                {
                    var resolved = await VersionResolver.ResolveLatestVersionAsync(package, _versionClient);
                    if (IsUsableVersionResult(resolved))
                    {
                        package.Version = resolved;
                        package.VersionCheckWarning = "";
                    }
                    else if (IsUsableVersionResult(previousVersion))
                    {
                        // Do not replace a known-good value with a transient blank/blocked response.
                        package.VersionCheckWarning = "Не удалось перепроверить; показана последняя успешная версия.";
                        AppLog.Write("VersionCheck", $"{package.Name}: источник временно не вернул версию; оставляем последнюю успешную {previousVersion}.");
                    }
                    else
                    {
                        package.Version = resolved;
                        package.VersionCheckWarning = "";
                    }
                }
                catch (Exception ex)
                {
                    if (IsUsableVersionResult(previousVersion))
                    {
                        package.VersionCheckWarning = "Не удалось перепроверить; показана последняя успешная версия.";
                        AppLog.Write("VersionCheck", $"Не удалось обновить версию {package.Name}; оставляем {previousVersion}.", ex);
                    }
                    else
                    {
                        package.Version = "Ошибка проверки";
                        package.VersionCheckWarning = "";
                        AppLog.Write("VersionCheck", $"Не удалось проверить версию {package.Name} после повторных попыток.", ex);
                    }
                }
                finally { semaphore.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshButton.IsEnabled = true;
                RefreshButton.Content = "↻  Проверить версии";
                if (_currentPage == "catalog") RefreshCatalogRows();
            });
        }
    }

    private static bool IsUsableVersionResult(string? version) =>
        !string.IsNullOrWhiteSpace(version) &&
        !version.Equals("Не проверена", StringComparison.OrdinalIgnoreCase) &&
        !version.Equals("Ошибка проверки", StringComparison.OrdinalIgnoreCase) &&
        !version.Equals(VersionResolver.UnknownVersion, StringComparison.OrdinalIgnoreCase) &&
        !version.Equals("Версия не указана", StringComparison.OrdinalIgnoreCase);

    private async Task CheckInstalledVersionsAsync()
    {
        var results = await Task.Run(() => _packages.Where(package => !package.IsDownloadOnly).Select(package =>
        {
            var manualPath = package.IsFlowseal && _settings.InstallationPaths.TryGetValue(package.Name, out var configuredManualPath)
                ? configuredManualPath : null;
            var detected = InstalledVersionService.Detect(package, manualPath);
            return (Package: package, Info: detected, Status: InstalledVersionService.FormatStatus(package, detected));
        }).ToList());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var downloadOnly in _packages.Where(p => p.IsDownloadOnly))
            {
                downloadOnly.InstalledVersion = "";
                downloadOnly.InstallationPath = "";
                downloadOnly.InstallationStatus = downloadOnly.IsIsoImage
                    ? "ISO-образ · доступно скачивание"
                    : "Портативная утилита · доступно скачивание";
            }
            var removedStalePath = false;
            foreach (var result in results)
            {
                result.Package.InstalledVersion = result.Info.Version;
                result.Package.InstallationStatus = result.Status;
                if (result.Package.IsFlowseal && _settings.InstallationPaths.TryGetValue(result.Package.Name, out var configuredPath) &&
                    !File.Exists(configuredPath) && !Directory.Exists(configuredPath))
                {
                    _settings.InstallationPaths.Remove(result.Package.Name);
                    removedStalePath = true;
                }
                result.Package.InstallationPath = result.Package.IsFlowseal && _settings.InstallationPaths.TryGetValue(result.Package.Name, out var validPath)
                    ? validPath : result.Info.Path;
            }
            if (removedStalePath) PersistSettings();
            if (_currentPage == "catalog") RefreshCatalogRows();
            else if (_currentPage == "settings") ShowSettingsPage();
        });
    }

    private void PersistSettings()
    {
        _settings.DownloadFolder = _downloadFolder;
        try { SettingsService.Save(_settings); }
        catch (Exception ex) { AppLog.Write("Settings", "Не удалось сохранить настройки.", ex); }
    }

    private void UpdateNavigation()
    {
        SetNavState(CatalogNav, _currentPage == "catalog");
        SetNavState(DownloadsNav, _currentPage == "downloads");
        SetNavState(HistoryNav, _currentPage == "history");
        SetNavState(SettingsNav, _currentPage == "settings");
    }

    private static void SetNavState(Button button, bool selected)
    {
        if (selected) { if (!button.Classes.Contains("selected")) button.Classes.Add("selected"); }
        else button.Classes.Remove("selected");
    }

    private static void ApplyFilterButtonStyle(Button button, bool selected)
    {
        button.Background = Brush(selected ? "#263B56" : "#1D293B");
        button.Foreground = Brush(selected ? "#8ED0FF" : "#AEBBD0");
        button.BorderBrush = Brush(selected ? "#38618A" : "#2D3B52");
    }

    private static void AddHeaderCell(Grid grid, string label, int column)
    {
        var text = Txt(label, 10, "#8293AD", FontWeight.SemiBold);
        text.Margin = new Thickness(7, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, column);
        grid.Children.Add(text);
    }

    private static Border Card(Control content) => new()
    {
        Padding = new Thickness(16), Child = content, Background = Brush("#172235"),
        BorderBrush = Brush("#2A394F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10)
    };

    private static Control SettingLine(string title, string description)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*"), ColumnSpacing = 12 };
        grid.Children.Add(Txt(title, 12, "#CAD6E7", FontWeight.Medium));
        var desc = Txt(description, 12, "#8294AE");
        desc.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(desc, 1);
        grid.Children.Add(desc);
        return grid;
    }

    private static TextBlock Txt(string text, double size, string color, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write("Browser", $"Не удалось открыть ссылку {url}.", ex); }
    }

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write("Explorer", $"Не удалось открыть папку {path}.", ex); }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title, Width = 470, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Background = Brush("#141D2D")
        };
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(22) };
        var text = Txt(message, 13, "#DCE7F7"); text.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(text);
        var ok = new Button { Content = "Понятно", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        ok.Click += (_, _) => dialog.Close();
        body.Children.Add(ok);
        dialog.Content = body;
        await dialog.ShowDialog(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _appUpdateTimer?.Stop();
        _downloadCancellation?.Cancel();
        _downloadCancellation?.Dispose();
        _downloadService.Dispose();
        _versionClient.Dispose();
        base.OnClosed(e);
    }
}
