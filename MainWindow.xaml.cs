using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.IO.Compression;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AxfsExplorer;

public class CommandEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string Icon { get; set; } = "\uE756";
    public Action? Execute { get; set; }
}

public class PartitionItem
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Index { get; set; }
    public uint Offset { get; set; }
    public bool IsEfi { get; set; }
    public bool IsLocked { get; set; }
    public string IconGlyph => IsEfi ? "\uE977" : "\uEDA2";
    public Visibility LockVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;
    public Windows.UI.Color AccentColor => IsEfi ? ColorHelper.FromArgb(255, 239, 68, 68) : ColorHelper.FromArgb(255, 124, 92, 231);
    public SolidColorBrush AccentBrush => new(AccentColor);
    public bool IsHealthy { get; set; } = true;
    public SolidColorBrush HealthBrush => new(IsHealthy ? ColorHelper.FromArgb(255, 52, 211, 153) : ColorHelper.FromArgb(255, 239, 68, 68));
    public Visibility HealthVisibility => Visibility.Visible;
    public string HealthTooltip => IsHealthy ? "Healthy" : "Issues detected";
}

class ExplorerTab : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "New Tab";
    public DiskImage? Disk { get; set; }
    public RdbDisk? Rdb { get; set; }
    public AxfsVolume? Vol { get; set; }
    public string CurrentPath { get; set; } = "/";
    public List<string> ScanLog { get; } = new();
    public string? TempExtractedPath { get; set; }
    public bool BinWasGzipped { get; set; }
    public int MountedPartitionIndex { get; set; } = -1;
    public void CloseImage()
    {
        Vol = null; Rdb = null; Disk?.Dispose(); Disk = null;
        CurrentPath = "/"; BinWasGzipped = false; MountedPartitionIndex = -1;
        ScanLog.Clear(); DisplayName = "New Tab";
        if (TempExtractedPath != null) { try { File.Delete(TempExtractedPath); } catch { } TempExtractedPath = null; }
    }
    public void Dispose() => CloseImage();
}

public sealed partial class MainWindow : Window
{
    readonly List<ExplorerTab> _tabs = new();
    ExplorerTab? _activeTab;
    SoundManager? _sounds;
    AppSettings _settings = null!;
    bool _isLoadingSettings, _isTabSwitching;
    DispatcherTimer? _notifTimer, _animationTimer, _ledTimer;
    int _animFrame;
    string _filterText = "";
    readonly List<FileItem> _allFiles = new();
    readonly ObservableCollection<PartitionItem> _partitions = new();
    readonly ObservableCollection<FileItem> _files = new();

    // New feature state
    readonly ActivityLogger _logger = new();
    readonly UndoManager _undoManager = new();
    string _sortColumn = "Name";
    bool _sortAscending = true;
    bool _deepSearch = false;
    bool _previewPaneVisible = false;
    bool _activityLogVisible = false;
    List<CommandEntry> _commands = new();

    IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _isLoadingSettings = true;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "AXFS Explorer";
        SystemBackdrop = new MicaBackdrop();
        PartitionList.ItemsSource = _partitions;
        FileList.ItemsSource = _files;
        var soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "sounds");
        _sounds = new SoundManager(soundsDir);
        ApplySoundSettings();
        var firstTab = new ExplorerTab();
        _tabs.Add(firstTab); _activeTab = firstTab;
        _sortColumn = _settings.SortColumn;
        _sortAscending = _settings.SortAscending;
        _previewPaneVisible = _settings.PreviewPaneVisible;
        _activityLogVisible = _settings.ActivityLogVisible;
        ApplyTheme(_settings.Theme);
        ResizeWindow(_settings.WindowWidth, _settings.WindowHeight);
        InitializeSettingsUI();
        InitCommands();
        RebuildTabStrip();
        UpdateBreadcrumb();
        UpdateConnectionIndicator();
        UpdatePartitionToolbar();
        UpdateBookmarksUI();
        UpdateRecentFilesUI();
        UpdateSortIndicators();
        ApplyPreviewPaneState();
        ApplyActivityLogState();
        StartWelcomeAnimation();
        StartLedAnimation();
        _logger.EntryAdded += e => DispatcherQueue.TryEnqueue(() =>
        {
            ActivityLogText.Text = _logger.Format();
            ActivityLogScroll?.ChangeView(null, double.MaxValue, null);
        });
        _logger.Log("APP", "AXFS Explorer started");
        this.Closed += OnWindowClosed;
    }

    // ═══════════════════ ANIMATIONS ═══════════════════

    void StartWelcomeAnimation()
    {
        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _animFrame = 0;
        _animationTimer.Tick += (s, e) =>
        {
            _animFrame++;
            if (_animFrame >= 20 && _animFrame <= 40) ShortcutHints.Opacity = (_animFrame - 20) / 20.0 * 0.5;
            if (FloatingDiskIcon != null) FloatingDiskIcon.RenderTransform = new TranslateTransform { Y = Math.Sin(_animFrame * 0.04) * 5 };
            if (OrbitDot1 != null) { double a = _animFrame * 0.025, r = 52, cx = 56, cy = 56; Canvas.SetLeft(OrbitDot1, cx + Math.Cos(a) * r - 3.5); Canvas.SetTop(OrbitDot1, cy + Math.Sin(a) * r - 3.5); }
            if (OrbitDot2 != null) { double a = _animFrame * 0.018 + Math.PI, r = 46, cx = 56, cy = 56; Canvas.SetLeft(OrbitDot2, cx + Math.Cos(a) * r - 2.5); Canvas.SetTop(OrbitDot2, cy + Math.Sin(a) * r - 2.5); }
        };
        _animationTimer.Start();
    }

    void StartLedAnimation()
    {
        _ledTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        var idle = new SolidColorBrush(ColorHelper.FromArgb(25, 128, 128, 128));
        _ledTimer.Tick += (s, e) => { foreach (var l in new[] { Led1, Led2, Led3, Led4, Led5 }) l.Background = idle; };
        _ledTimer.Start();
    }

    void FlashActivityLeds()
    {
        var active = new SolidColorBrush(ColorHelper.FromArgb(255, 52, 211, 153));
        var f1 = new SolidColorBrush(ColorHelper.FromArgb(100, 52, 211, 153));
        var f2 = new SolidColorBrush(ColorHelper.FromArgb(35, 52, 211, 153));
        var idle = new SolidColorBrush(ColorHelper.FromArgb(25, 128, 128, 128));
        var leds = new[] { Led1, Led2, Led3, Led4, Led5 };
        int a = new Random().Next(leds.Length);
        for (int i = 0; i < leds.Length; i++) leds[i].Background = Math.Abs(i - a) switch { 0 => active, 1 => f1, 2 => f2, _ => idle };
        var reset = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        reset.Tick += (_, _) => { foreach (var l in leds) l.Background = idle; reset.Stop(); };
        reset.Start();
    }

    // ═══════════════════ THEME ═══════════════════

    void OnQuickThemeToggle(object s, RoutedEventArgs e)
    {
        var cur = (Content as FrameworkElement)?.ActualTheme;
        string next = cur == ElementTheme.Dark ? "Light" : "Dark";
        _settings.Theme = next; _settings.Save(); ApplyTheme(next); RebuildTabStrip(); UpdateThemeToggleIcon();
    }
    void UpdateThemeToggleIcon() => ThemeToggleIcon.Glyph = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark ? "\uE793" : "\uE706";

    // ═══════════════════ SORT & FILTER ═══════════════════

    void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _filterText = sender.Text?.Trim() ?? "";
        if (_deepSearch && !string.IsNullOrEmpty(_filterText)) PerformDeepSearch(_filterText);
        else ApplySortAndFilter();
    }

    void ApplySortAndFilter()
    {
        _files.Clear();
        var sorted = _allFiles.AsEnumerable();
        // Keep ".." always first
        var dotdot = sorted.Where(f => f.Name == "..").ToList();
        var rest = sorted.Where(f => f.Name != "..").ToList();
        // Folders first, then sort
        var dirs = rest.Where(f => f.IsDir);
        var files = rest.Where(f => !f.IsDir);
        dirs = SortItems(dirs); files = SortItems(files);
        foreach (var f in dotdot) _files.Add(f);
        foreach (var f in dirs) if (MatchesFilter(f)) _files.Add(f);
        foreach (var f in files) if (MatchesFilter(f)) _files.Add(f);
        ItemCountText.Text = $"{_files.Count} item(s)";
        if (!string.IsNullOrEmpty(_filterText)) ItemCountText.Text += $" (filtered from {_allFiles.Count})";
    }

    bool MatchesFilter(FileItem fi) => string.IsNullOrEmpty(_filterText) || fi.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase);

    IEnumerable<FileItem> SortItems(IEnumerable<FileItem> items) => (_sortColumn, _sortAscending) switch
    {
        ("Name", true) => items.OrderBy(f => f.Name),
        ("Name", false) => items.OrderByDescending(f => f.Name),
        ("Size", true) => items.OrderBy(f => f.Size),
        ("Size", false) => items.OrderByDescending(f => f.Size),
        ("Type", true) => items.OrderBy(f => f.TypeText),
        ("Type", false) => items.OrderByDescending(f => f.TypeText),
        ("Date", true) => items.OrderBy(f => f.Mtime),
        ("Date", false) => items.OrderByDescending(f => f.Mtime),
        _ => items.OrderBy(f => f.Name),
    };

    void SetSort(string col) { if (_sortColumn == col) _sortAscending = !_sortAscending; else { _sortColumn = col; _sortAscending = true; } _settings.SortColumn = _sortColumn; _settings.SortAscending = _sortAscending; _settings.Save(); UpdateSortIndicators(); ApplySortAndFilter(); }
    void OnSortByName(object s, RoutedEventArgs e) => SetSort("Name");
    void OnSortBySize(object s, RoutedEventArgs e) => SetSort("Size");
    void OnSortByType(object s, RoutedEventArgs e) => SetSort("Type");
    void OnSortByDate(object s, RoutedEventArgs e) => SetSort("Date");

    void UpdateSortIndicators()
    {
        string up = "\uE70E", down = "\uE70D";
        SortNameIcon.Glyph = _sortColumn == "Name" ? (_sortAscending ? up : down) : "";
        SortSizeIcon.Glyph = _sortColumn == "Size" ? (_sortAscending ? up : down) : "";
        SortTypeIcon.Glyph = _sortColumn == "Type" ? (_sortAscending ? up : down) : "";
        SortDateIcon.Glyph = _sortColumn == "Date" ? (_sortAscending ? up : down) : "";
    }

    // ═══════════════════ DEEP SEARCH ═══════════════════

    void OnDeepSearchToggle(object s, RoutedEventArgs e) { _deepSearch = DeepSearchToggle.IsChecked == true; if (_deepSearch && !string.IsNullOrEmpty(_filterText)) PerformDeepSearch(_filterText); else if (!_deepSearch) RefreshFileList(); }

    void PerformDeepSearch(string query)
    {
        var vol = _activeTab?.Vol; if (vol == null) return;
        _allFiles.Clear();
        var results = new List<FileItem>();
        DeepSearchRecursive(vol, "/", query, results, 200);
        foreach (var fi in results) _allFiles.Add(fi);
        ApplySortAndFilter();
        _logger.Log("SEARCH", $"Deep search '{query}' → {results.Count} results");
    }

    void DeepSearchRecursive(AxfsVolume vol, string path, string query, List<FileItem> results, int limit)
    {
        if (results.Count >= limit) return;
        var entries = vol.ListDir(path);
        foreach (var entry in entries)
        {
            if (results.Count >= limit) return;
            string full = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;
            if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var stat = vol.Stat(full);
                results.Add(new FileItem { Name = full, Icon = entry.IType == 2 ? "\uE8B7" : GetFileIcon(entry.Name), IsDir = entry.IType == 2, Size = stat?.Size ?? 0, Mtime = stat?.Mtime ?? 0, InodeNum = entry.Inode });
            }
            if (entry.IType == 2) DeepSearchRecursive(vol, full, query, results, limit);
        }
    }

    // ═══════════════════ FILE STATS ═══════════════════

    void UpdateFileStatsBar()
    {
        FileStatsBadges.Children.Clear();
        var nonMeta = _allFiles.Where(f => f.Name != ".." && !f.IsDir).ToList();
        var dirs = _allFiles.Count(f => f.IsDir && f.Name != "..");
        if (nonMeta.Count == 0 && dirs == 0) { FileStatsBar.Visibility = Visibility.Collapsed; return; }
        FileStatsBar.Visibility = Visibility.Visible;
        if (dirs > 0) AddStatBadge($"\uE8B7 {dirs}", ColorHelper.FromArgb(30, 251, 191, 36), "Folders");
        foreach (var g in nonMeta.GroupBy(f => Path.GetExtension(f.Name).ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(5))
        {
            string ext = string.IsNullOrEmpty(g.Key) ? "file" : g.Key.TrimStart('.'); var c = GetFileTypeColor(g.Key);
            AddStatBadge($".{ext} ×{g.Count()}", ColorHelper.FromArgb(30, c.R, c.G, c.B), $"{ext} files");
        }
        ulong total = 0; foreach (var f in nonMeta) total += f.Size;
        TotalSizeText.Text = total < 1024 ? $"{total} B total" : total < 1024 * 1024 ? $"{total / 1024.0:F1} KB total" : $"{total / (1024.0 * 1024):F1} MB total";
    }

    void AddStatBadge(string text, Windows.UI.Color bg, string tooltip)
    {
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Background = new SolidColorBrush(bg), Child = new TextBlock { Text = text, FontSize = 10, Opacity = 0.65, FontFamily = new FontFamily("Cascadia Code,Consolas") } };
        ToolTipService.SetToolTip(b, tooltip); FileStatsBadges.Children.Add(b);
    }
    static Windows.UI.Color GetFileTypeColor(string ext) => ext switch { ".lua" => ColorHelper.FromArgb(255, 96, 165, 250), ".cfg" or ".ini" or ".toml" => ColorHelper.FromArgb(255, 251, 191, 36), ".txt" or ".md" => ColorHelper.FromArgb(255, 52, 211, 153), ".log" or ".vbl" => ColorHelper.FromArgb(255, 192, 132, 252), ".sig" => ColorHelper.FromArgb(255, 239, 68, 68), _ => ColorHelper.FromArgb(255, 148, 163, 184) };

    // ═══════════════════ TAB MANAGEMENT ═══════════════════

    void RebuildTabStrip()
    {
        TabStripPanel.Children.Clear();
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var activeTabBg = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 40, 40, 42) : ColorHelper.FromArgb(255, 251, 251, 253));
        var activeTabBorder = new SolidColorBrush(isDark ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(25, 0, 0, 0));
        var hoverBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(14, 255, 255, 255) : ColorHelper.FromArgb(14, 0, 0, 0));
        var accentBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 124, 92, 231));
        foreach (var tab in _tabs)
        {
            bool active = tab == _activeTab;
            var outer = new StackPanel { Spacing = 0 };
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = tab.Vol != null ? "\uEDA2" : "\uE160", FontSize = 11, Opacity = active ? 0.9 : 0.35, Margin = new Thickness(0, 0, 7, 0), Foreground = active ? accentBrush : null }; Grid.SetColumn(icon, 0); grid.Children.Add(icon);
            var text = new TextBlock { Text = tab.DisplayName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = active ? 0.95 : 0.45, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal }; Grid.SetColumn(text, 1); grid.Children.Add(text);
            if (_tabs.Count > 1) { var ci = new FontIcon { Glyph = "\uE711", FontSize = 8, Opacity = 0.4 }; var close = new Button { Content = ci, Padding = new Thickness(4, 2, 4, 2), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(4) }; var tabRef = tab; close.Click += (_, _) => CloseTab(tabRef); Grid.SetColumn(close, 2); grid.Children.Add(close); }
            var tabBorder = new Border { Child = grid, CornerRadius = new CornerRadius(8, 8, 0, 0), Padding = new Thickness(14, 7, 8, 7) };
            if (active) { tabBorder.Background = activeTabBg; tabBorder.BorderBrush = activeTabBorder; tabBorder.BorderThickness = new Thickness(1, 1, 1, 0); } else { var nb = new SolidColorBrush(Colors.Transparent); tabBorder.Background = nb; tabBorder.PointerEntered += (_, _) => tabBorder.Background = hoverBrush; tabBorder.PointerExited += (_, _) => tabBorder.Background = nb; }
            outer.Children.Add(tabBorder);
            if (active) outer.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(10, 0, 10, 0), Background = accentBrush });
            var tabRef2 = tab; tabBorder.Tapped += (_, _) => ActivateTab(tabRef2); TabStripPanel.Children.Add(outer);
        }
    }

    void ActivateTab(ExplorerTab tab) { if (tab == _activeTab) return; _isTabSwitching = true; _activeTab = tab; RebuildTabStrip(); RefreshForActiveTab(); _isTabSwitching = false; }
    void OnAddTab(object s, RoutedEventArgs e) { var tab = new ExplorerTab(); _tabs.Add(tab); ActivateTab(tab); }
    void CloseTab(ExplorerTab tab) { if (_tabs.Count <= 1) { UnloadActiveImage(); return; } int idx = _tabs.IndexOf(tab); tab.Dispose(); _tabs.Remove(tab); if (tab == _activeTab) _activeTab = _tabs[Math.Min(idx, _tabs.Count - 1)]; RebuildTabStrip(); RefreshForActiveTab(); if (!_tabs.Any(t => t.Vol != null)) _sounds?.StopAmbient(); }

    void RefreshForActiveTab()
    {
        _partitions.Clear(); _files.Clear(); _allFiles.Clear();
        var tab = _activeTab; if (tab == null) return;
        if (tab.Disk != null)
        {
            DiskNameText.Text = tab.DisplayName;
            DiskSizeText.Text = $"{tab.Disk.SectorCount * tab.Disk.SectorSize / 1024} KB · {tab.Disk.SectorCount} sectors";
            DiskFormatText.Text = tab.Rdb != null ? "RDB Partition Table" : tab.Vol != null ? "AXFS v2" : "Unknown";
            FormatBadge.Visibility = tab.Vol != null ? Visibility.Visible : Visibility.Collapsed;
            FormatBadgeText.Text = tab.Rdb != null ? "RDB" : "AXFS";
            if (tab.Rdb != null) foreach (var (p, i) in tab.Rdb.Partitions.Select((p, i) => (p, i)))
                _partitions.Add(new PartitionItem { Name = p.IsEfi ? $"{p.DeviceName}: SYSTEM" : $"{p.DeviceName}: {p.FsLabel}", Detail = $"{p.FsTypeName} — {p.SizeSectors * tab.Disk.SectorSize / 1024} KB", Index = i, Offset = p.StartSector, IsEfi = p.IsEfi, IsLocked = p.IsReadOnly, IsHealthy = true });
            else if (tab.Vol != null)
                _partitions.Add(new PartitionItem { Name = $"AXFS: {tab.Vol.Super.Label}", Detail = "Primary", Index = -1, Offset = 0, IsHealthy = tab.Vol.Super.CrcValid });
            WelcomeState.Visibility = Visibility.Collapsed;
            RefreshFileList();
        }
        else { DiskNameText.Text = "No image loaded"; DiskSizeText.Text = ""; DiskFormatText.Text = ""; FormatBadge.Visibility = Visibility.Collapsed; WelcomeState.Visibility = Visibility.Visible; EmptyDirState.Visibility = Visibility.Collapsed; FileStatsBar.Visibility = Visibility.Collapsed; UpdateRecentFilesUI(); }
        UpdateBreadcrumb(); UpdateVolumeInfo(); UpdateConnectionIndicator(); UpdatePartitionToolbar(); UpdatePartitionMap(); UpdateWindowTitle(); UpdateThemeToggleIcon();
    }

    void UpdateWindowTitle() { var n = _activeTab?.DisplayName ?? "AXFS Explorer"; Title = _activeTab?.Disk != null ? $"AXFS Explorer — {n}" : "AXFS Explorer"; TitleBarText.Text = Title; }

    // ═══════════════════ PARTITION MANAGEMENT ═══════════════════

    bool IsActivePartitionLocked() { var tab = _activeTab; if (tab?.Rdb == null || tab.MountedPartitionIndex < 0) return false; return tab.MountedPartitionIndex < tab.Rdb.Partitions.Count && tab.Rdb.Partitions[tab.MountedPartitionIndex].IsReadOnly; }

    void UpdatePartitionToolbar()
    {
        bool hasRdb = _activeTab?.Rdb != null;
        PartitionToolbar.Visibility = hasRdb ? Visibility.Visible : Visibility.Collapsed;
        if (hasRdb && PartitionList.SelectedItem is PartitionItem pi && pi.Index >= 0 && pi.Index < _activeTab!.Rdb!.Partitions.Count)
        { var p = _activeTab.Rdb.Partitions[pi.Index]; PartPropsBtn.IsEnabled = true; PartLockBtn.IsEnabled = true; PartLockIcon.Glyph = p.IsReadOnly ? "\uE785" : "\uE72E"; PartLockText.Text = p.IsReadOnly ? "Unlock" : "Lock"; }
        else { PartPropsBtn.IsEnabled = false; PartLockBtn.IsEnabled = false; PartLockText.Text = "Lock"; PartLockIcon.Glyph = "\uE72E"; }
    }

    async void OnPartitionProperties(object s, RoutedEventArgs e)
    {
        var tab = _activeTab; if (tab?.Disk == null || tab.Rdb == null) return;
        if (PartitionList.SelectedItem is not PartitionItem pi || pi.Index < 0 || pi.Index >= tab.Rdb.Partitions.Count) return;
        var part = tab.Rdb.Partitions[pi.Index];
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "Partition Information", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 15 });
        AddPropRow(panel, "Device", part.DeviceName); AddPropRow(panel, "Filesystem", part.FsTypeName);
        AddPropRow(panel, "Start sector", part.StartSector.ToString("N0"));
        AddPropRow(panel, "Size", $"{part.SizeSectors:N0} sectors ({part.SizeSectors * tab.Disk.SectorSize / 1024} KB)");
        AddPropRow(panel, "Boot priority", part.BootPriority.ToString());
        panel.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Brush });
        panel.Children.Add(new TextBlock { Text = "Volume Label", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
        var labelBox = new TextBox { Text = part.FsLabel, MaxLength = 16, IsEnabled = !part.IsReadOnly };
        panel.Children.Add(labelBox);
        panel.Children.Add(new TextBlock { Text = "Flags", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
        var bootCheck = new CheckBox { Content = "Bootable", IsChecked = part.IsBootable };
        var autoCheck = new CheckBox { Content = "Auto-mount", IsChecked = part.IsAutoMount };
        var readonlyCheck = new CheckBox { Content = "Read-only (Locked)", IsChecked = part.IsReadOnly };
        panel.Children.Add(bootCheck); panel.Children.Add(autoCheck); panel.Children.Add(readonlyCheck);
        var dlg = new ContentDialog { Title = $"Partition: {part.DeviceName}", Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        uint newFlags = 0; if (bootCheck.IsChecked == true) newFlags |= RdbPartition.FLAG_BOOTABLE; if (autoCheck.IsChecked == true) newFlags |= RdbPartition.FLAG_AUTOMOUNT; if (readonlyCheck.IsChecked == true) newFlags |= RdbPartition.FLAG_READONLY;
        bool changed = false;
        if (newFlags != part.Flags && RdbWriter.WritePartitionFlags(tab.Disk, part, newFlags)) changed = true;
        if (labelBox.Text.Trim() != part.FsLabel && !part.IsReadOnly && RdbWriter.WritePartitionLabel(tab.Disk, part, labelBox.Text.Trim())) changed = true;
        if (changed) { var nr = RdbDisk.Read(tab.Disk); if (nr != null) tab.Rdb = nr; RefreshForActiveTab(); ShowNotification("Partition updated", InfoBarSeverity.Success); _sounds?.PlayHddAccess(); FlashActivityLeds(); _logger.Log("PART", "Partition properties updated"); }
    }

    void OnPartitionToggleLock(object s, RoutedEventArgs e)
    {
        var tab = _activeTab; if (tab?.Disk == null || tab.Rdb == null) return;
        if (PartitionList.SelectedItem is not PartitionItem pi || pi.Index < 0 || pi.Index >= tab.Rdb.Partitions.Count) return;
        var part = tab.Rdb.Partitions[pi.Index]; uint nf = part.IsReadOnly ? part.Flags & ~RdbPartition.FLAG_READONLY : part.Flags | RdbPartition.FLAG_READONLY;
        if (RdbWriter.WritePartitionFlags(tab.Disk, part, nf)) { var nr = RdbDisk.Read(tab.Disk); if (nr != null) tab.Rdb = nr; RefreshForActiveTab(); ShowNotification($"{part.DeviceName}: {(part.IsReadOnly ? "Locked" : "Unlocked")}", InfoBarSeverity.Informational); }
    }

    static void AddPropRow(StackPanel p, string l, string v) { var r = new Grid(); r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); var lb = new TextBlock { Text = l, FontSize = 12, Opacity = 0.5 }; var vl = new TextBlock { Text = v, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }; Grid.SetColumn(vl, 1); r.Children.Add(lb); r.Children.Add(vl); p.Children.Add(r); }

    // ═══════════════════ PARTITION MAP ═══════════════════

    void UpdatePartitionMap()
    {
        PartitionMapStack.Children.Clear();
        var tab = _activeTab;
        if (tab?.Rdb == null || tab.Rdb.Partitions.Count == 0 || tab.Disk == null) { PartitionMapBar.Visibility = Visibility.Collapsed; return; }
        PartitionMapBar.Visibility = Visibility.Visible;
        int total = tab.Disk.SectorCount;
        foreach (var part in tab.Rdb.Partitions)
        {
            double w = Math.Max(6, (double)part.SizeSectors / total * 240);
            var c = part.FsType == 0x41584546 ? ColorHelper.FromArgb(255, 239, 68, 68) : ColorHelper.FromArgb(255, 124, 92, 231);
            var seg = new Border { Width = w, Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c), Margin = new Thickness(1, 0, 1, 0), Opacity = 0.7 };
            ToolTipService.SetToolTip(seg, $"{part.DeviceName}: {part.FsLabel}\n{part.SizeSectors * tab.Disk.SectorSize / 1024} KB");
            PartitionMapStack.Children.Add(seg);
        }
    }

    // ═══════════════════ NAVIGATION ═══════════════════

    void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        ExplorerView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Collapsed; HealthView.Visibility = Visibility.Collapsed;
        if (args.IsSettingsSelected) { SettingsView.Visibility = Visibility.Visible; return; }
        if (args.SelectedItem is NavigationViewItem item)
        {
            string tag = item.Tag?.ToString() ?? "explorer";
            if (tag == "health") { HealthView.Visibility = Visibility.Visible; RefreshHealthDashboard(); }
            else ExplorerView.Visibility = Visibility.Visible;
        }
    }

    void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (_activeTab?.Vol == null) return;
        if (args.Index == 0) _activeTab.CurrentPath = "/";
        else { var parts = _activeTab.CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries); _activeTab.CurrentPath = "/" + string.Join("/", parts.Take(args.Index)); }
        _filterText = ""; FilterBox.Text = ""; _deepSearch = false; DeepSearchToggle.IsChecked = false; RefreshFileList();
    }

    void UpdateBreadcrumb()
    {
        var vol = _activeTab?.Vol;
        if (vol == null) { PathBreadcrumb.ItemsSource = new List<string> { "No image loaded" }; return; }
        var items = new List<string> { vol.Super.Label ?? "Root" };
        foreach (var p in (_activeTab?.CurrentPath ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries)) items.Add(p);
        PathBreadcrumb.ItemsSource = items;
    }

    // ═══════════════════ OPEN / NEW / UNLOAD ═══════════════════

    async void OnOpenImage(object s, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".img"); picker.FileTypeFilter.Add(".bin"); picker.FileTypeFilter.Add(".vhd"); picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync(); if (file == null) return;
        var tab = _activeTab!; tab.CloseImage(); _undoManager.Clear();
        try
        {
            string openPath = file.Path;
            if (Path.GetExtension(file.Path).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Analyzing .bin file..."); var (tp, el) = BinExtractor.TryExtract(file.Path); tab.ScanLog.AddRange(el);
                if (tp != null) { openPath = tp; tab.TempExtractedPath = tp; try { var hdr = new byte[2]; using var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read); fs.Read(hdr, 0, 2); tab.BinWasGzipped = hdr[0] == 0x1F && hdr[1] == 0x8B; } catch { tab.BinWasGzipped = true; } }
            }
            try { tab.Disk = DiskImage.Open(openPath); } catch { tab.Disk = DiskImage.OpenReadOnly(openPath); }
            tab.Disk.ExtractedFromBin = Path.GetExtension(file.Path).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? file.Path : null;
            var result = ImageScanner.Scan(tab.Disk); tab.ScanLog.AddRange(result.Log); tab.Rdb = result.Rdb; tab.Vol = result.Vol;
            if (tab.Rdb != null && tab.Vol != null) for (int i = 0; i < tab.Rdb.Partitions.Count; i++) if (tab.Rdb.Partitions[i].StartSector == result.AxfsOffset) { tab.MountedPartitionIndex = i; break; }
            tab.DisplayName = Path.GetFileName(tab.Disk.ExtractedFromBin ?? file.Path); tab.CurrentPath = "/"; _filterText = ""; FilterBox.Text = "";
            _settings.AddRecentFile(file.Path);
            RebuildTabStrip(); RefreshForActiveTab();
            if (tab.Vol != null) { SetStatus("Opened — AXFS mounted"); ShowNotification($"Opened {tab.DisplayName}", InfoBarSeverity.Success); _ = _sounds?.PlayImageOpenSequence(); FlashActivityLeds(); _logger.Log("OPEN", $"Opened {tab.DisplayName}"); ToastHelper.Show("AXFS Explorer", $"Opened {tab.DisplayName}"); }
            else { SetStatus("No filesystem found"); ShowNotification("No filesystem found", InfoBarSeverity.Warning); _ = _sounds?.PlayErrorSequence(); _logger.Log("OPEN", "No filesystem found"); await ShowDiagnosticsDialog(); }
        }
        catch (Exception ex) { tab.ScanLog.Add($"Error: {ex}"); SetStatus($"Error: {ex.Message}"); ShowNotification($"Failed: {ex.Message}", InfoBarSeverity.Error); _ = _sounds?.PlayErrorSequence(); }
    }

    async void OnNewImage(object s, RoutedEventArgs e)
    {
        var dlg = new ContentDialog { Title = "Create New Disk Image", PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        var panel = new StackPanel { Spacing = 12 }; var sizeBox = new NumberBox { Header = "Size (KB)", Value = _settings.DefaultImageSizeKB, Minimum = 64, Maximum = 65536, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact }; var labelBox = new TextBox { Header = "Volume Label", Text = _settings.DefaultVolumeLabel, MaxLength = 16 };
        panel.Children.Add(sizeBox); panel.Children.Add(labelBox); dlg.Content = panel;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var picker = new FileSavePicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeChoices.Add("Disk Image", new List<string> { ".img" }); picker.SuggestedFileName = "axis_disk.img";
        var file = await picker.PickSaveFileAsync(); if (file == null) return;
        CachedFileManager.DeferUpdates(file);
        try
        {
            int sizeKB = (int)sizeBox.Value, ss = 512, ts = sizeKB * 1024 / ss; var tab = _activeTab!; tab.CloseImage(); _undoManager.Clear();
            tab.Disk = DiskImage.Create(file.Path, ts, ss);
            var rdbBlock = new byte[256]; Encoding.ASCII.GetBytes("RDSK").CopyTo(rdbBlock, 0); BE.W32(rdbBlock, 4, 64); BE.W32(rdbBlock, 12, 7); BE.W32(rdbBlock, 16, (uint)ss); BE.W32(rdbBlock, 20, 0); BE.WI32(rdbBlock, 24, 1); BE.WI32(rdbBlock, 28, -1); BE.WI32(rdbBlock, 32, -1); BE.WI32(rdbBlock, 36, -1); BE.W32(rdbBlock, 40, 1); BE.W32(rdbBlock, 44, (uint)ts); BE.W32(rdbBlock, 48, 1); BE.WStr(rdbBlock, 64, "AxisOS", 16); BE.WStr(rdbBlock, 80, "VirtualDrive", 16); BE.WStr(rdbBlock, 96, "v2", 4); BE.WStr(rdbBlock, 100, labelBox.Text, 16); BE.W32(rdbBlock, 116, (uint)ts); BE.W32(rdbBlock, 120, 1);
            var padded = BE.Pad(rdbBlock, 256); BE.W32(padded, 8, Crc.AmigaChecksum(padded, 8)); tab.Disk.WriteSector(0, BE.Pad(padded, ss));
            int partStart = 17, partSize = ts - partStart; var partBlock = new byte[256]; Encoding.ASCII.GetBytes("PART").CopyTo(partBlock, 0); BE.W32(partBlock, 4, 64); BE.W32(partBlock, 12, 7); BE.WI32(partBlock, 16, -1); BE.W32(partBlock, 20, 0x02); partBlock[24] = 3; Encoding.ASCII.GetBytes("DH0").CopyTo(partBlock, 25); BE.W32(partBlock, 56, (uint)partStart); BE.W32(partBlock, 60, (uint)partSize); BE.W32(partBlock, 64, 0x41584632); BE.WStr(partBlock, 80, labelBox.Text, 16); partBlock = BE.Pad(partBlock, 256); BE.W32(partBlock, 8, Crc.AmigaChecksum(partBlock, 8)); tab.Disk.WriteSector(1, BE.Pad(partBlock, ss));
            FormatAxfsPartition(tab.Disk, partStart, partSize, ss, labelBox.Text); tab.Disk.Save();
            var path = file.Path; tab.CloseImage(); tab.Disk = DiskImage.Open(path); var result = ImageScanner.Scan(tab.Disk); tab.ScanLog.AddRange(result.Log); tab.Rdb = result.Rdb; tab.Vol = result.Vol; tab.DisplayName = Path.GetFileName(path); tab.CurrentPath = "/"; _filterText = ""; FilterBox.Text = "";
            _settings.AddRecentFile(file.Path); await CachedFileManager.CompleteUpdatesAsync(file); RebuildTabStrip(); RefreshForActiveTab();
            SetStatus($"Created new {sizeKB} KB disk image"); ShowNotification($"Created {sizeKB} KB image", InfoBarSeverity.Success); _ = _sounds?.PlayImageOpenSequence(); FlashActivityLeds(); _logger.Log("CREATE", $"Created {sizeKB} KB image");
        }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); ShowNotification($"Creation failed: {ex.Message}", InfoBarSeverity.Error); }
    }

    static void FormatAxfsPartition(DiskImage disk, int offset, int size, int ss, string label)
    {
        var zeros = new byte[ss]; for (int i = 0; i < size; i++) disk.WriteSector(offset + i, zeros);
        int maxInodes = 512, ips = ss / 80, its = (maxInodes + ips - 1) / ips;
        int bbmpStart = 3, estBlocks = size - (2 + 1 + its) - 1;
        int bbmpSec = Math.Max(1, (estBlocks + ss * 8 - 1) / (ss * 8));
        int itableStart = bbmpStart + bbmpSec, dataStart = itableStart + its;
        int maxBlocks = size - dataStart; if (maxBlocks < 1) return;
        bbmpSec = Math.Max(1, (maxBlocks + ss * 8 - 1) / (ss * 8)); itableStart = bbmpStart + bbmpSec; dataStart = itableStart + its; maxBlocks = size - dataStart;
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new byte[ss]; Encoding.ASCII.GetBytes("AXF2").CopyTo(sb, 0); sb[4] = 2; BE.W16(sb, 5, (ushort)ss); BE.W32(sb, 7, (uint)size); BE.W16(sb, 11, (ushort)maxInodes); BE.W16(sb, 13, (ushort)maxBlocks); BE.W16(sb, 15, (ushort)(maxInodes - 2)); BE.W16(sb, 17, (ushort)(maxBlocks - 1)); BE.W16(sb, 19, (ushort)dataStart); BE.W16(sb, 21, (ushort)itableStart); BE.W16(sb, 23, (ushort)bbmpStart); sb[25] = (byte)bbmpSec; BE.WStr(sb, 26, label, 16); BE.W32(sb, 42, now); BE.W32(sb, 46, now); BE.W32(sb, 50, 1); BE.W32(sb, 56, Crc.Crc32(sb, 0, 56)); disk.WriteSector(offset, sb); disk.WriteSector(offset + 1, sb);
        var ibmp = new byte[ss]; ibmp[0] = 0x03; disk.WriteSector(offset + 2, ibmp);
        var bbmp = new byte[ss]; bbmp[0] = 0x01; disk.WriteSector(offset + bbmpStart, bbmp);
        int rootSec = itableStart + 1 / ips, rootOff = (1 % ips) * 80; var rootSector = new byte[ss]; BE.W16(rootSector, rootOff, 2); BE.W16(rootSector, rootOff + 2, 0x1FF); BE.W32(rootSector, rootOff + 8, 64); BE.W32(rootSector, rootOff + 12, now); BE.W32(rootSector, rootOff + 16, now); BE.W16(rootSector, rootOff + 20, 2); rootSector[rootOff + 23] = 1; BE.W16(rootSector, rootOff + 24, 0); BE.W16(rootSector, rootOff + 26, 1); disk.WriteSector(offset + rootSec, rootSector);
        var dirBlock = new byte[ss]; BE.W16(dirBlock, 0, 1); dirBlock[2] = 2; dirBlock[3] = 1; Encoding.ASCII.GetBytes(".").CopyTo(dirBlock, 4); BE.W16(dirBlock, 32, 1); dirBlock[34] = 2; dirBlock[35] = 2; Encoding.ASCII.GetBytes("..").CopyTo(dirBlock, 36); disk.WriteSector(offset + dataStart, dirBlock);
    }

    void OnUnloadImage(object s, RoutedEventArgs e) => UnloadActiveImage();
    void UnloadActiveImage() { var tab = _activeTab; if (tab?.Disk == null) { SetStatus("No image to unload"); return; } _sounds?.PlayImageClose(); _logger.Log("EJECT", $"Ejected {tab.DisplayName}"); tab.CloseImage(); _undoManager.Clear(); _filterText = ""; FilterBox.Text = ""; RebuildTabStrip(); RefreshForActiveTab(); SetStatus("Image ejected"); ShowNotification("Image ejected", InfoBarSeverity.Informational); if (!_tabs.Any(t => t.Vol != null)) _sounds?.StopAmbient(); }

    // ═══════════════════ RECENT FILES ═══════════════════

    void UpdateRecentFilesUI()
    {
        RecentFilesList.Children.Clear();
        var recent = _settings.RecentFiles.Where(File.Exists).Take(5).ToList();
        RecentFilesPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var path in recent)
        {
            var btn = new Button { Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Colors.Transparent) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            sp.Children.Add(new FontIcon { Glyph = "\uEDA2", FontSize = 14, Opacity = 0.35 });
            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            try { info.Children.Add(new TextBlock { Text = $"{new FileInfo(path).Length / 1024} KB", FontSize = 10, Opacity = 0.3 }); } catch { }
            sp.Children.Add(info); btn.Content = sp;
            var p = path; btn.Click += (_, _) => OpenRecentFile(p);
            RecentFilesList.Children.Add(btn);
        }
    }

    void OpenRecentFile(string path)
    {
        var tab = _activeTab!; tab.CloseImage(); _undoManager.Clear();
        try
        {
            try { tab.Disk = DiskImage.Open(path); } catch { tab.Disk = DiskImage.OpenReadOnly(path); }
            var result = ImageScanner.Scan(tab.Disk); tab.ScanLog.AddRange(result.Log); tab.Rdb = result.Rdb; tab.Vol = result.Vol;
            if (tab.Rdb != null && tab.Vol != null) for (int i = 0; i < tab.Rdb.Partitions.Count; i++) if (tab.Rdb.Partitions[i].StartSector == result.AxfsOffset) { tab.MountedPartitionIndex = i; break; }
            tab.DisplayName = Path.GetFileName(path); tab.CurrentPath = "/"; _filterText = ""; FilterBox.Text = "";
            _settings.AddRecentFile(path); RebuildTabStrip(); RefreshForActiveTab();
            if (tab.Vol != null) { SetStatus("Opened from recent"); _ = _sounds?.PlayImageOpenSequence(); FlashActivityLeds(); _logger.Log("OPEN", $"Opened recent: {tab.DisplayName}"); }
            else { SetStatus("No filesystem found"); ShowNotification("No filesystem found", InfoBarSeverity.Warning); }
        }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); ShowNotification($"Failed: {ex.Message}", InfoBarSeverity.Error); }
    }

    // ═══════════════════ PARTITION SELECTION ═══════════════════

    void OnPartitionSelected(object s, SelectionChangedEventArgs e)
    {
        if (_isTabSwitching) return; UpdatePartitionToolbar();
        var tab = _activeTab; if (PartitionList.SelectedItem is not PartitionItem pi || tab?.Disk == null) return;
        if (pi.Index >= 0 && pi.Index < (tab.Rdb?.Partitions.Count ?? 0) && tab.Rdb!.Partitions[pi.Index].FsType != 0x41584632)
        { _files.Clear(); _allFiles.Clear(); WelcomeState.Visibility = Visibility.Collapsed; EmptyDirState.Visibility = Visibility.Visible; VolumeInfoPanel.Visibility = Visibility.Collapsed; FileStatsBar.Visibility = Visibility.Collapsed; SetStatus($"{pi.Name}: not mountable"); return; }
        try { var nv = AxfsVolume.Mount(tab.Disk, pi.Offset); if (nv == null) { SetStatus($"Cannot mount"); return; } tab.Vol = nv; tab.CurrentPath = "/"; tab.MountedPartitionIndex = pi.Index; _filterText = ""; FilterBox.Text = ""; RefreshFileList(); UpdateVolumeInfo(); UpdateConnectionIndicator(); SetStatus($"Mounted \"{tab.Vol.Super.Label}\""); FlashActivityLeds(); _logger.Log("MOUNT", $"Mounted partition {pi.Name}"); }
        catch (Exception ex) { SetStatus($"Mount error: {ex.Message}"); }
    }

    void UpdateVolumeInfo()
    {
        var vol = _activeTab?.Vol; var disk = _activeTab?.Disk;
        if (vol == null || disk == null) { VolumeInfoPanel.Visibility = Visibility.Collapsed; return; }
        VolumeInfoPanel.Visibility = Visibility.Visible; VolumeLabelText.Text = vol.Super.Label;
        int used = Math.Max(0, vol.Super.MaxBlocks - vol.Super.FreeBlocks);
        int totalKB = vol.Super.MaxBlocks * disk.SectorSize / 1024, usedKB = used * disk.SectorSize / 1024;
        VolumeUsageText.Text = $"{usedKB} / {totalKB} KB used";
        VolumeUsageBar.Maximum = vol.Super.MaxBlocks; VolumeUsageBar.Value = Math.Max(0, Math.Min(used, vol.Super.MaxBlocks));
        VolumeDetailText.Text = $"gen={vol.Super.Generation} inodes={vol.Super.MaxInodes - vol.Super.FreeInodes}/{vol.Super.MaxInodes}";
        double pct = vol.Super.MaxBlocks > 0 ? (double)used / vol.Super.MaxBlocks * 100 : 0;
        VolumeRing.Value = pct; VolumePercentText.Text = $"{pct:F0}%";
    }

    // ═══════════════════ FILE LIST ═══════════════════

    void RefreshFileList()
    {
        _files.Clear(); _allFiles.Clear(); WelcomeState.Visibility = Visibility.Collapsed; EmptyDirState.Visibility = Visibility.Collapsed;
        var tab = _activeTab; var vol = tab?.Vol;
        if (vol == null) { WelcomeState.Visibility = Visibility.Visible; UpdateBreadcrumb(); FileStatsBar.Visibility = Visibility.Collapsed; UpdateRecentFilesUI(); return; }
        UpdateBreadcrumb(); var curPath = tab!.CurrentPath;
        if (curPath != "/") _allFiles.Add(new FileItem { Name = "..", Icon = "\uE74A", IsDir = true });
        var entries = vol.ListDir(curPath);
        foreach (var entry in entries)
        {
            string full = curPath == "/" ? "/" + entry.Name : curPath + "/" + entry.Name;
            var stat = vol.Stat(full); string preview = ""; bool showPreview = false;
            if (entry.IType == 1 && stat != null && stat.Size > 0 && stat.Size <= 200 && LuaSyntaxHighlighter.IsTextExt(entry.Name))
            { try { var d = vol.ReadFile(full); if (d != null && !IsBinaryContent(d)) { preview = Encoding.UTF8.GetString(d).Replace('\n', ' ').Replace('\r', ' ').Trim(); if (preview.Length > 80) preview = preview[..80] + "…"; showPreview = preview.Length > 0; } } catch { } }
            _allFiles.Add(new FileItem { Name = entry.Name, Icon = entry.IType == 2 ? "\uE8B7" : GetFileIcon(entry.Name), IsDir = entry.IType == 2, Size = stat?.Size ?? 0, Mtime = stat?.Mtime ?? 0, InodeNum = entry.Inode, PreviewText = preview, HasPreview = showPreview });
        }
        ApplySortAndFilter();
        EmptyDirState.Visibility = entries.Count == 0 && curPath == "/" ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = ""; UpdateFileStatsBar(); _sounds?.PlayHddAccess(); FlashActivityLeds();
    }

    static string GetFileIcon(string n) => Path.GetExtension(n).ToLowerInvariant() switch { ".lua" => "\uE943", ".cfg" => "\uE713", ".txt" => "\uE8A5", ".sig" => "\uE8D7", ".log" or ".vbl" => "\uE9F9", _ => "\uE8A5" };
    void OnNavigateUp(object s, RoutedEventArgs e) { if (_activeTab == null || _activeTab.CurrentPath == "/") return; int i = _activeTab.CurrentPath.LastIndexOf('/'); _activeTab.CurrentPath = i <= 0 ? "/" : _activeTab.CurrentPath[..i]; _filterText = ""; FilterBox.Text = ""; _deepSearch = false; DeepSearchToggle.IsChecked = false; RefreshFileList(); }
    void OnRefresh(object s, RoutedEventArgs e) { RefreshFileList(); _logger.Log("NAV", "Refreshed"); }

    void OnFileDoubleTapped(object s, DoubleTappedRoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileItem fi) return;
        if (fi.Name == "..") { OnNavigateUp(s, e); return; }
        if (fi.IsDir) { var p = _activeTab!.CurrentPath; _activeTab.CurrentPath = p == "/" ? "/" + fi.Name : p + "/" + fi.Name; _filterText = ""; FilterBox.Text = ""; _deepSearch = false; DeepSearchToggle.IsChecked = false; RefreshFileList(); _logger.Log("NAV", $"Entered {fi.Name}"); }
        else ShowFileContent(fi);
    }

    void OnFileSelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_isTabSwitching) return;
        int count = FileList.SelectedItems.Count;
        if (count == 1 && FileList.SelectedItem is FileItem fi && fi.Name != "..") SelectionText.Text = fi.IsDir ? fi.Name : $"{fi.Name} ({fi.SizeText})";
        else if (count > 1) SelectionText.Text = $"{count} items selected";
        else SelectionText.Text = "";
        UpdatePreviewPane();
    }

    void OnContextOpen(object s, RoutedEventArgs e) { if (FileList.SelectedItem is not FileItem fi || fi.Name == "..") return; if (fi.IsDir) { var p = _activeTab!.CurrentPath; _activeTab.CurrentPath = p == "/" ? "/" + fi.Name : p + "/" + fi.Name; _filterText = ""; FilterBox.Text = ""; RefreshFileList(); } else ShowFileContent(fi); }

    // ═══════════════════ KEYBOARD NAV ═══════════════════

    void OnFileListKeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { if (FileList.SelectedItem is FileItem fi) { if (fi.Name == "..") OnNavigateUp(s, e); else if (fi.IsDir) { var p = _activeTab!.CurrentPath; _activeTab.CurrentPath = p == "/" ? "/" + fi.Name : p + "/" + fi.Name; RefreshFileList(); } else ShowFileContent(fi); } e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Back) { OnNavigateUp(s, e); e.Handled = true; }
    }

    // ═══════════════════ PREVIEW PANE ═══════════════════

    void OnTogglePreviewPane(object s, RoutedEventArgs e) { _previewPaneVisible = !_previewPaneVisible; _settings.PreviewPaneVisible = _previewPaneVisible; _settings.Save(); ApplyPreviewPaneState(); UpdatePreviewPane(); }

    void ApplyPreviewPaneState()
    {
        if (_previewPaneVisible) { PreviewDividerCol.Width = GridLength.Auto; PreviewPaneCol.Width = new GridLength(300); PreviewDivider.Width = 1; PreviewPane.Visibility = Visibility.Visible; PreviewPaneIcon.Glyph = "\uE89F"; }
        else { PreviewDividerCol.Width = new GridLength(0); PreviewPaneCol.Width = new GridLength(0); PreviewDivider.Width = 0; PreviewPane.Visibility = Visibility.Collapsed; PreviewPaneIcon.Glyph = "\uE89F"; }
    }

    void UpdatePreviewPane()
    {
        if (!_previewPaneVisible) return;
        if (FileList.SelectedItem is not FileItem fi || fi.Name == ".." || FileList.SelectedItems.Count != 1)
        { PreviewTitle.Text = "Preview"; PreviewContentText.Text = "Select a file to preview"; PreviewContentText.Opacity = 0.3; PreviewScroll.Content = PreviewContentText; return; }
        PreviewTitle.Text = fi.Name;
        if (fi.IsDir) { PreviewContentText.Text = $"📁 Folder: {fi.Name}"; PreviewContentText.Opacity = 0.4; PreviewScroll.Content = PreviewContentText; return; }
        var vol = _activeTab?.Vol; if (vol == null) return;
        string path = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name;
        byte[]? data = vol.ReadFile(path); if (data == null) { PreviewContentText.Text = "Cannot read"; PreviewContentText.Opacity = 0.3; PreviewScroll.Content = PreviewContentText; return; }
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        if (!IsBinaryContent(data))
        {
            int bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            string text = Encoding.UTF8.GetString(data, bom, data.Length - bom);
            if (LuaSyntaxHighlighter.IsLuaFile(fi.Name) && _settings.SyntaxHighlighting && text.Split('\n').Length <= 200) PreviewScroll.Content = LuaSyntaxHighlighter.Highlight(text, isDark, _settings.ShowLineNumbers, 11, 200);
            else PreviewScroll.Content = new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 11, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, Foreground = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 212, 212, 212) : ColorHelper.FromArgb(255, 30, 30, 30)) };
        }
        else
        {
            int shown = Math.Min(data.Length, 16 * 64); var sb = new StringBuilder();
            for (int i = 0; i < shown; i += 16) { sb.Append($"{i:X6}  "); for (int j = 0; j < 16; j++) sb.Append(i + j < data.Length ? $"{data[i + j]:X2} " : "   "); sb.AppendLine(); }
            PreviewScroll.Content = new TextBlock { Text = sb.ToString(), FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 10, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, Opacity = 0.7 };
        }
    }

    // ═══════════════════ FILE VIEWER / EDITOR ═══════════════════

    static bool IsBinaryContent(byte[] d) { int c = Math.Min(d.Length, 8192); for (int i = 0; i < c; i++) { if (i == 0 && d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) { i = 2; continue; } if (d[i] == 0) return true; } return false; }

    async void ShowFileContent(FileItem fi)
    {
        var vol = _activeTab?.Vol; if (vol == null) return;
        string fullPath = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name;
        byte[]? data = vol.ReadFile(fullPath); if (data == null) { SetStatus("Cannot read file"); return; }
        _sounds?.PlayHddAccess(); FlashActivityLeds(); _logger.Log("READ", $"Opened {fi.Name} ({data.Length} B)");
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        bool isBinary = IsBinaryContent(data);
        string textContent = ""; if (!isBinary) { int bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0; textContent = Encoding.UTF8.GetString(data, bom, data.Length - bom); }
        var root = new Grid { MinWidth = 700 }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(460) });
        // Info badges
        var infoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        infoRow.Children.Add(MakeInfoBadge($"{data.Length:N0} bytes", isDark)); if (!isBinary) infoRow.Children.Add(MakeInfoBadge($"{textContent.Split('\n').Length:N0} lines", isDark));
        infoRow.Children.Add(MakeInfoBadge(GetFileTypeName(fi.Name), isDark)); if (IsActivePartitionLocked()) infoRow.Children.Add(MakeInfoBadge("READ-ONLY", isDark));
        // Lua syntax check
        // if (LuaSyntaxHighlighter.IsLuaFile(fi.Name) && !isBinary) { try { var fn = new NLua.Lua(); } catch { } /* Placeholder - basic check */ }
        Grid.SetRow(infoRow, 0); root.Children.Add(infoRow);
        var contentBorder = new Border { Background = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 30, 30, 30) : ColorHelper.FromArgb(255, 252, 252, 252)), CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(isDark ? ColorHelper.FromArgb(20, 255, 255, 255) : ColorHelper.FromArgb(20, 0, 0, 0)), BorderThickness = new Thickness(1) };
        Grid.SetRow(contentBorder, 3); root.Children.Add(contentBorder);
        string currentText = textContent; TextBox? editBox = null; bool isEditing = false;
        // Find/Replace bar
        TextBox? findBox = null, replaceBox = null;

        void ShowPreview() { isEditing = false; if (isBinary) { contentBorder.Child = CreateHexView(data, isDark, _settings.EditorFontSize); return; } int lc = currentText.Split('\n').Length; bool hl = LuaSyntaxHighlighter.IsLuaFile(fi.Name) && _settings.SyntaxHighlighting && lc <= 300; contentBorder.Child = hl ? new ScrollViewer { Content = LuaSyntaxHighlighter.Highlight(currentText, isDark, _settings.ShowLineNumbers, _settings.EditorFontSize, 300), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) } : CreateTextPreview(currentText, isDark, _settings.EditorFontSize, _settings.ShowLineNumbers); }
        void ShowEdit() { isEditing = true; editBox = new TextBox { Text = currentText, FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = _settings.EditorFontSize, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8) }; ScrollViewer.SetVerticalScrollBarVisibility(editBox, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(editBox, ScrollBarVisibility.Auto); var bg = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 30, 30, 30) : ColorHelper.FromArgb(255, 252, 252, 252)); editBox.Resources["TextControlBackground"] = bg; editBox.Resources["TextControlBackgroundPointerOver"] = bg; editBox.Resources["TextControlBackgroundFocused"] = bg; editBox.Foreground = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 212, 212, 212) : ColorHelper.FromArgb(255, 30, 30, 30)); contentBorder.Child = editBox; }
        void ShowHex() { isEditing = false; contentBorder.Child = CreateHexView(isBinary ? data : Encoding.UTF8.GetBytes(currentText), isDark, _settings.EditorFontSize); }

        if (!isBinary)
        {
            var tabRow = new Grid { Margin = new Thickness(0, 0, 0, 4) }; tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var tPrev = new ToggleButton { Content = "Preview", IsChecked = true, Padding = new Thickness(14, 6, 14, 6), FontSize = 12, CornerRadius = new CornerRadius(6) };
            var tEdit = new ToggleButton { Content = "Edit", Padding = new Thickness(14, 6, 14, 6), FontSize = 12, CornerRadius = new CornerRadius(6) };
            var tHex = new ToggleButton { Content = "Hex", Padding = new Thickness(14, 6, 14, 6), FontSize = 12, CornerRadius = new CornerRadius(6) };
            void SetTab(string m) { if (isEditing && editBox != null) currentText = editBox.Text; tPrev.IsChecked = m == "preview"; tEdit.IsChecked = m == "edit"; tHex.IsChecked = m == "hex"; switch (m) { case "preview": ShowPreview(); break; case "edit": ShowEdit(); break; case "hex": ShowHex(); break; } }
            tPrev.Click += (_, _) => SetTab("preview"); tEdit.Click += (_, _) => SetTab("edit"); tHex.Click += (_, _) => SetTab("hex");
            tabs.Children.Add(tPrev); tabs.Children.Add(tEdit); tabs.Children.Add(tHex);
            Grid.SetColumn(tabs, 0); tabRow.Children.Add(tabs);
            bool locked = IsActivePartitionLocked();
            var saveBtn = new Button { Padding = new Thickness(16, 7, 16, 7), Style = Application.Current.Resources["AccentButtonStyle"] as Style, IsEnabled = !locked, CornerRadius = new CornerRadius(6) };
            var saveSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; saveSp.Children.Add(new FontIcon { Glyph = "\uE74E", FontSize = 12 }); saveSp.Children.Add(new TextBlock { Text = locked ? "Locked" : "Save", FontSize = 12 }); saveBtn.Content = saveSp;
            saveBtn.Click += (_, _) => { if (IsActivePartitionLocked()) { ShowNotification("Locked", InfoBarSeverity.Warning); return; } if (isEditing && editBox != null) currentText = editBox.Text; var oldData = data; var nd = Encoding.UTF8.GetBytes(currentText); if (vol.WriteFile(fullPath, nd)) { _undoManager.Record(new UndoAction(UndoActionType.ModifyFile, fullPath, OldData: oldData)); data = nd; _sounds?.PlayHddAccess(); FlashActivityLeds(); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo(); ShowNotification($"Saved {fi.Name}", InfoBarSeverity.Success); _logger.Log("WRITE", $"Saved {fi.Name}"); } else ShowNotification($"Failed to save", InfoBarSeverity.Error); };
            Grid.SetColumn(saveBtn, 2); tabRow.Children.Add(saveBtn);
            Grid.SetRow(tabRow, 1); root.Children.Add(tabRow);

            // Find/Replace row
            var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
            findBox = new TextBox { PlaceholderText = "Find…", Width = 200, Height = 30, FontSize = 12, CornerRadius = new CornerRadius(6) };
            replaceBox = new TextBox { PlaceholderText = "Replace…", Width = 200, Height = 30, FontSize = 12, CornerRadius = new CornerRadius(6) };
            var replaceBtn = new Button { Content = "Replace All", Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(6), FontSize = 11 };
            replaceBtn.Click += (_, _) => { if (isEditing && editBox != null && !string.IsNullOrEmpty(findBox.Text)) { editBox.Text = editBox.Text.Replace(findBox.Text, replaceBox.Text); currentText = editBox.Text; } };
            findRow.Children.Add(findBox); findRow.Children.Add(replaceBox); findRow.Children.Add(replaceBtn);
            Grid.SetRow(findRow, 2); root.Children.Add(findRow);
        }
        ShowPreview();
        var dialog = new ContentDialog { Title = fi.Name, Content = root, CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
        await dialog.ShowAsync();
    }

    static ScrollViewer CreateTextPreview(string text, bool dk, int fs, bool ln, int mx = 2000) { var lines = text.Split('\n'); int total = Math.Min(lines.Length, mx); int nw = Math.Max(3, lines.Length.ToString().Length); var sb = new StringBuilder(); for (int i = 0; i < total; i++) { if (ln) { sb.Append((i + 1).ToString().PadLeft(nw)); sb.Append("  "); } sb.AppendLine(lines[i].TrimEnd('\r')); } if (lines.Length > mx) sb.AppendLine($"\n… {lines.Length - mx:N0} more lines"); return new ScrollViewer { Content = new TextBlock { Text = sb.ToString(), FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = fs, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, Foreground = new SolidColorBrush(dk ? ColorHelper.FromArgb(255, 212, 212, 212) : ColorHelper.FromArgb(255, 30, 30, 30)) }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) }; }
    static ScrollViewer CreateHexView(byte[] data, bool dk, int fs = 12) { int shown = Math.Min(data.Length, 16 * 512); var sb = new StringBuilder(); for (int i = 0; i < shown; i += 16) { sb.Append($"{i:X8}  "); for (int j = 0; j < 16; j++) { sb.Append(i + j < data.Length ? $"{data[i + j]:X2} " : "   "); if (j == 7) sb.Append(' '); } sb.Append(" │ "); for (int j = 0; j < 16 && i + j < data.Length; j++) { byte b = data[i + j]; sb.Append(b >= 32 && b < 127 ? (char)b : '·'); } sb.AppendLine(); } if (data.Length > shown) sb.AppendLine($"\n… {data.Length - shown:N0} more bytes"); return new ScrollViewer { Content = new TextBlock { Text = sb.ToString(), FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = fs, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, Foreground = new SolidColorBrush(dk ? ColorHelper.FromArgb(255, 212, 212, 212) : ColorHelper.FromArgb(255, 30, 30, 30)) }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) }; }
    Border MakeInfoBadge(string t, bool dk) => new() { Background = new SolidColorBrush(dk ? ColorHelper.FromArgb(15, 255, 255, 255) : ColorHelper.FromArgb(15, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), Child = new TextBlock { Text = t, FontSize = 11, Opacity = 0.65, FontFamily = new FontFamily("Cascadia Code,Consolas") } };
    static string GetFileTypeName(string n) => Path.GetExtension(n).ToLowerInvariant() switch { ".lua" => "Lua", ".cfg" => "Config", ".txt" => "Text", ".log" or ".vbl" => "Log", ".sig" => "Signature", "" => "File", var ext => ext.TrimStart('.').ToUpperInvariant() };

    // ═══════════════════ FILE OPERATIONS ═══════════════════

    async void OnImportFile(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) { SetStatus("No volume"); return; }
        if (IsActivePartitionLocked()) { ShowNotification("Locked", InfoBarSeverity.Warning); return; }
        var dlg = new ContentDialog { Title = "Import", PrimaryButtonText = "Files", SecondaryButtonText = "Folder", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot, Content = new TextBlock { Text = "Import files or folder?" } };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var picker = new FileOpenPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd); picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync(); if (files == null || files.Count == 0) return;
            int count = 0; foreach (var file in files) { try { var d = await File.ReadAllBytesAsync(file.Path); string target = _activeTab!.CurrentPath == "/" ? "/" + file.Name : _activeTab.CurrentPath + "/" + file.Name; if (vol.WriteFile(target, d)) count++; } catch { } }
            _sounds?.PlayHddAccess(); FlashActivityLeds(); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo(); SetStatus($"Imported {count} file(s)"); ShowNotification($"Imported {count} file(s)", InfoBarSeverity.Success); _logger.Log("IMPORT", $"Imported {count} file(s)");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var picker = new FolderPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd); picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync(); if (folder == null) return;
            int count = await ImportFolderRecursive(folder, _activeTab!.CurrentPath);
            _sounds?.PlayHddAccess(); FlashActivityLeds(); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo(); ShowNotification($"Imported {count} item(s)", InfoBarSeverity.Success); _logger.Log("IMPORT", $"Imported folder: {count} items");
        }
    }

    async Task<int> ImportFolderRecursive(StorageFolder folder, string basePath) { var vol = _activeTab?.Vol; if (vol == null) return 0; string td = basePath == "/" ? "/" + folder.Name : basePath + "/" + folder.Name; vol.Mkdir(td); int c = 1; foreach (var item in await folder.GetItemsAsync()) { if (item is StorageFile file) { try { var buf = await FileIO.ReadBufferAsync(file); var d = new byte[buf.Length]; using (var r = Windows.Storage.Streams.DataReader.FromBuffer(buf)) r.ReadBytes(d); if (vol.WriteFile(td + "/" + file.Name, d)) c++; } catch { } } else if (item is StorageFolder sub) c += await ImportFolderRecursive(sub, td); } return c; }

    async void OnExportFile(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) return;
        var selected = FileList.SelectedItems.Cast<FileItem>().Where(f => !f.IsDir && f.Name != "..").ToList();
        if (selected.Count == 0) { SetStatus("Select file(s) to export"); return; }
        if (selected.Count == 1)
        {
            var fi = selected[0]; string fp = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name;
            byte[]? d = vol.ReadFile(fp); if (d == null) { SetStatus("Cannot read"); return; }
            var picker = new FileSavePicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            string ext = Path.GetExtension(fi.Name); picker.FileTypeChoices.Add("File", new List<string> { string.IsNullOrEmpty(ext) ? "." : ext }); picker.SuggestedFileName = fi.Name;
            var file = await picker.PickSaveFileAsync(); if (file == null) return;
            await File.WriteAllBytesAsync(file.Path, d); SetStatus($"Exported {fi.Name}"); ShowNotification($"Exported {fi.Name}", InfoBarSeverity.Success); _logger.Log("EXPORT", fi.Name);
        }
        else
        {
            var picker = new FolderPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd); picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync(); if (folder == null) return;
            int count = 0; foreach (var fi in selected) { string fp = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name; byte[]? d = vol.ReadFile(fp); if (d != null) { await File.WriteAllBytesAsync(Path.Combine(folder.Path, fi.Name), d); count++; } }
            ShowNotification($"Exported {count} files", InfoBarSeverity.Success); _logger.Log("EXPORT", $"Batch exported {count} files");
        }
        _sounds?.PlayHddAccess(); FlashActivityLeds();
    }

    async void OnNewFolder(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) return; if (IsActivePartitionLocked()) { ShowNotification("Locked", InfoBarSeverity.Warning); return; }
        var dlg = new ContentDialog { Title = "New Folder", PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot }; var tb = new TextBox { PlaceholderText = "Folder name", MaxLength = 27 }; dlg.Content = tb;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(tb.Text)) return;
        string target = _activeTab!.CurrentPath == "/" ? "/" + tb.Text.Trim() : _activeTab.CurrentPath + "/" + tb.Text.Trim();
        if (vol.Mkdir(target)) { _sounds?.PlayHddAccess(); FlashActivityLeds(); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo(); SetStatus($"Created: {tb.Text.Trim()}"); _logger.Log("MKDIR", tb.Text.Trim()); } else SetStatus("Failed to create folder");
    }

    async void OnDelete(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) return; if (IsActivePartitionLocked()) { ShowNotification("Locked", InfoBarSeverity.Warning); return; }
        var selected = FileList.SelectedItems.Cast<FileItem>().Where(f => f.Name != "..").ToList(); if (selected.Count == 0) return;
        if (_settings.ConfirmDelete) { var desc = selected.Count == 1 ? (selected[0].IsDir ? $"Delete folder '{selected[0].Name}' and ALL contents?" : $"Delete '{selected[0].Name}'?") : $"Delete {selected.Count} items?"; var dlg = new ContentDialog { Title = "Confirm Delete", Content = desc, PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot }; if (await dlg.ShowAsync() != ContentDialogResult.Primary) return; }
        int count = 0;
        foreach (var fi in selected)
        {
            string target = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name;
            byte[]? oldData = fi.IsDir ? null : vol.ReadFile(target);
            if (vol.Remove(target)) { _undoManager.Record(new UndoAction(fi.IsDir ? UndoActionType.DeleteDir : UndoActionType.DeleteFile, target, OldData: oldData)); count++; }
        }
        if (count > 0) { _sounds?.PlayHddAccess(); FlashActivityLeds(); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo(); SetStatus($"Deleted {count} item(s)"); ShowNotification($"Deleted {count} item(s)", InfoBarSeverity.Informational); _logger.Log("DELETE", $"Deleted {count} item(s)"); }
    }

    void OnSave(object s, RoutedEventArgs e) { SaveImageToDisk(); SetStatus("Saved"); FlashActivityLeds(); _logger.Log("SAVE", "Saved to disk"); }
    void SaveImageToDisk()
    {
        var tab = _activeTab; if (tab?.Disk == null) return; tab.Vol?.Flush(); tab.Disk.Save(); _sounds?.PlayHddAccess();
        if (tab.Disk.ExtractedFromBin == null || tab.TempExtractedPath == null) return;
        try { byte[] imgData; using (var fs = new FileStream(tab.TempExtractedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { using var ms = new MemoryStream(); fs.CopyTo(ms); imgData = ms.ToArray(); } if (tab.BinWasGzipped) { using var outFs = new FileStream(tab.Disk.ExtractedFromBin, FileMode.Create, FileAccess.Write); using var gzip = new GZipStream(outFs, CompressionLevel.Optimal); gzip.Write(imgData, 0, imgData.Length); } else File.WriteAllBytes(tab.Disk.ExtractedFromBin, imgData); ShowNotification($"Saved to {Path.GetFileName(tab.Disk.ExtractedFromBin)}", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowNotification($"Failed to save .bin: {ex.Message}", InfoBarSeverity.Error); }
    }

    // ═══════════════════ RENAME ═══════════════════

    async void OnRename(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) return; if (IsActivePartitionLocked()) { ShowNotification("Locked", InfoBarSeverity.Warning); return; }
        if (FileList.SelectedItem is not FileItem fi || fi.Name == ".." || fi.IsDir) { SetStatus("Select a file to rename"); return; }
        var dlg = new ContentDialog { Title = "Rename", PrimaryButtonText = "Rename", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        var tb = new TextBox { Text = fi.Name, MaxLength = 27 }; int dot = fi.Name.LastIndexOf('.'); if (dot > 0) tb.Select(0, dot);
        dlg.Content = tb;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string newName = tb.Text.Trim(); if (newName == fi.Name || string.IsNullOrEmpty(newName)) return;
        string curDir = _activeTab!.CurrentPath; string oldPath = curDir == "/" ? "/" + fi.Name : curDir + "/" + fi.Name; string newPath = curDir == "/" ? "/" + newName : curDir + "/" + newName;
        byte[]? data = vol.ReadFile(oldPath); if (data == null) return;
        if (vol.WriteFile(newPath, data) && vol.Remove(oldPath)) { _undoManager.Record(new UndoAction(UndoActionType.Rename, newPath, OldData: data, AltPath: oldPath)); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); ShowNotification($"Renamed to {newName}", InfoBarSeverity.Success); _logger.Log("RENAME", $"{fi.Name} → {newName}"); }
    }

    // ═══════════════════ UNDO / REDO ═══════════════════

    void OnUndo(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null || !_undoManager.CanUndo) { SetStatus("Nothing to undo"); return; }
        var action = _undoManager.PopUndo(); if (action == null) return;
        switch (action.Type)
        {
            case UndoActionType.DeleteFile: if (action.OldData != null) vol.WriteFile(action.Path, action.OldData); break;
            case UndoActionType.DeleteDir: vol.Mkdir(action.Path); break;
            case UndoActionType.Rename: if (action.OldData != null && action.AltPath != null) { vol.WriteFile(action.AltPath, action.OldData); vol.Remove(action.Path); } break;
            case UndoActionType.ModifyFile: if (action.OldData != null) vol.WriteFile(action.Path, action.OldData); break;
        }
        _undoManager.PushRedo(action);
        if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo();
        SetStatus("Undone"); ShowNotification("Undone", InfoBarSeverity.Informational); _logger.Log("UNDO", action.Path);
    }

    void OnRedo(object s, RoutedEventArgs e) { SetStatus("Redo not fully implemented yet"); }

    // ═══════════════════ BOOKMARKS ═══════════════════

    void OnAddBookmark(object s, RoutedEventArgs e) { var path = _activeTab?.CurrentPath ?? "/"; if (!_settings.Bookmarks.Contains(path)) { _settings.Bookmarks.Add(path); _settings.Save(); UpdateBookmarksUI(); _logger.Log("BOOKMARK", $"Added {path}"); } }
    void OnAddBookmarkFromContext(object s, RoutedEventArgs e) => OnAddBookmark(s, e);

    void UpdateBookmarksUI()
    {
        BookmarkStack.Children.Clear();
        BookmarkSection.Visibility = _settings.Bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var bm in _settings.Bookmarks)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var btn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new FontIcon { Glyph = "\uE728", FontSize = 11, Opacity = 0.4 });
            sp.Children.Add(new TextBlock { Text = bm == "/" ? "Root" : bm.Split('/').Last(s => s.Length > 0), FontSize = 11, Opacity = 0.6 });
            btn.Content = sp;
            var bmPath = bm; btn.Click += (_, _) => { if (_activeTab != null) { _activeTab.CurrentPath = bmPath; _filterText = ""; FilterBox.Text = ""; RefreshFileList(); } };
            Grid.SetColumn(btn, 0); grid.Children.Add(btn);
            var del = new Button { Style = Application.Current.Resources["MuteButton"] as Style, Padding = new Thickness(3), Opacity = 0.3, MinWidth = 0, MinHeight = 0 }; del.Content = new FontIcon { Glyph = "\uE711", FontSize = 8 };
            del.Click += (_, _) => { _settings.Bookmarks.Remove(bmPath); _settings.Save(); UpdateBookmarksUI(); };
            Grid.SetColumn(del, 1); grid.Children.Add(del);
            BookmarkStack.Children.Add(grid);
        }
    }

    // ═══════════════════ COMMAND PALETTE ═══════════════════

    void InitCommands()
    {
        _commands = new List<CommandEntry>
        {
            new() { Name = "Open Image", Description = "Open a disk image file", Shortcut = "Ctrl+O", Icon = "\uE838", Execute = () => OnOpenImage(this, new RoutedEventArgs()) },
            new() { Name = "New Image", Description = "Create a new disk image", Shortcut = "Ctrl+N", Icon = "\uE710", Execute = () => OnNewImage(this, new RoutedEventArgs()) },
            new() { Name = "Save", Description = "Save changes to disk", Shortcut = "Ctrl+S", Icon = "\uE74E", Execute = () => OnSave(this, new RoutedEventArgs()) },
            new() { Name = "Eject Image", Description = "Unload current image", Icon = "\uF12B", Execute = () => UnloadActiveImage() },
            new() { Name = "Navigate Up", Description = "Go to parent directory", Shortcut = "Backspace", Icon = "\uE74A", Execute = () => OnNavigateUp(this, new RoutedEventArgs()) },
            new() { Name = "Refresh", Description = "Refresh file list", Shortcut = "F5", Icon = "\uE72C", Execute = () => RefreshFileList() },
            new() { Name = "New Folder", Description = "Create a new folder", Icon = "\uE8F4", Execute = () => OnNewFolder(this, new RoutedEventArgs()) },
            new() { Name = "Toggle Preview Pane", Description = "Show/hide file preview", Icon = "\uE89F", Execute = () => OnTogglePreviewPane(this, new RoutedEventArgs()) },
            new() { Name = "Toggle Activity Log", Description = "Show/hide activity log", Icon = "\uE9F9", Execute = () => OnToggleActivityLog(this, new RoutedEventArgs()) },
            new() { Name = "Toggle Theme", Description = "Switch dark/light mode", Icon = "\uE793", Execute = () => OnQuickThemeToggle(this, new RoutedEventArgs()) },
            new() { Name = "Undo", Description = "Undo last action", Shortcut = "Ctrl+Z", Icon = "\uE7A7", Execute = () => OnUndo(this, new RoutedEventArgs()) },
            new() { Name = "Health Dashboard", Description = "View volume health", Icon = "\uE9D9", Execute = () => { foreach (NavigationViewItem item in NavView.MenuItems) if (item.Tag?.ToString() == "health") { NavView.SelectedItem = item; break; } } },
            new() { Name = "Settings", Description = "Open settings", Icon = "\uE713", Execute = () => NavView.SelectedItem = NavView.SettingsItem },
            new() { Name = "Diagnostics", Description = "Show scan diagnostics", Icon = "\uE9D9", Execute = () => OnShowDiagnostics(this, new RoutedEventArgs()) },
            new() { Name = "Sector Viewer", Description = "View raw disk sectors", Icon = "\uE7B8", Execute = () => OnShowSectorViewer(this, new RoutedEventArgs()) },
        };
    }

    void OnShowCommandPalette(object s, RoutedEventArgs e) { CmdPaletteOverlay.Visibility = Visibility.Visible; CmdPaletteInput.Text = ""; CmdPaletteResults.ItemsSource = _commands; CmdPaletteInput.Focus(FocusState.Programmatic); }
    void OnCmdPaletteDismiss(object s, TappedRoutedEventArgs e) { CmdPaletteOverlay.Visibility = Visibility.Collapsed; }
    void OnCmdPaletteTextChanged(object s, TextChangedEventArgs e) { var q = CmdPaletteInput.Text.Trim(); CmdPaletteResults.ItemsSource = string.IsNullOrEmpty(q) ? _commands : _commands.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(); }
    void OnCmdPaletteKeyDown(object s, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Escape) CmdPaletteOverlay.Visibility = Visibility.Collapsed; else if (e.Key == Windows.System.VirtualKey.Enter) { var items = CmdPaletteResults.ItemsSource as IList<CommandEntry>; if (items?.Count > 0) { CmdPaletteOverlay.Visibility = Visibility.Collapsed; items[0].Execute?.Invoke(); } } }
    void OnCmdPaletteItemClick(object s, ItemClickEventArgs e) { CmdPaletteOverlay.Visibility = Visibility.Collapsed; (e.ClickedItem as CommandEntry)?.Execute?.Invoke(); }

    // ═══════════════════ ACTIVITY LOG ═══════════════════

    void OnToggleActivityLog(object s, RoutedEventArgs e) { _activityLogVisible = !_activityLogVisible; _settings.ActivityLogVisible = _activityLogVisible; _settings.Save(); ApplyActivityLogState(); }
    void ApplyActivityLogState() { if (_activityLogVisible) { ActivityLogRow.Height = new GridLength(180); ActivityLogPanel.Visibility = Visibility.Visible; ActivityLogText.Text = _logger.Format(); } else { ActivityLogRow.Height = new GridLength(0); ActivityLogPanel.Visibility = Visibility.Collapsed; } }
    void OnClearActivityLog(object s, RoutedEventArgs e) { _logger.Clear(); ActivityLogText.Text = ""; }

    // ═══════════════════ INODE INSPECTOR ═══════════════════

    async void OnInodeInspector(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null || FileList.SelectedItem is not FileItem fi || fi.Name == "..") return;
        string fullPath = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name;
        var inode = vol.Stat(fullPath); if (inode == null) { SetStatus("Cannot read inode"); return; }
        var panel = new StackPanel { Spacing = 6, MinWidth = 380 };
        void Row(string l, string v) => AddPropRow(panel, l, v);
        Row("Inode #", fi.InodeNum.ToString()); Row("Type", inode.IsDir ? "Directory" : inode.IsFile ? "File" : $"Type {inode.IType}");
        Row("Mode", $"0x{inode.Mode:X4}"); Row("UID:GID", $"{inode.Uid}:{inode.Gid}"); Row("Size", $"{inode.Size:N0} bytes");
        Row("Links", inode.Links.ToString()); Row("Flags", $"0x{inode.Flags:X2}{(inode.IsInline ? " (INLINE)" : "")}");
        Row("Extents", inode.NExtents.ToString()); Row("Indirect", inode.Indirect.ToString());
        try { Row("Created", DateTimeOffset.FromUnixTimeSeconds(inode.Ctime).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")); } catch { Row("Created", inode.Ctime.ToString()); }
        try { Row("Modified", DateTimeOffset.FromUnixTimeSeconds(inode.Mtime).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")); } catch { Row("Modified", inode.Mtime.ToString()); }
        if (!inode.IsInline && inode.Extents.Length > 0) { panel.Children.Add(new TextBlock { Text = "Extents", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) }); foreach (var (start, count) in inode.Extents) panel.Children.Add(new TextBlock { Text = $"  [{start}..{start + count - 1}] ({count} blocks)", FontFamily = new FontFamily("Cascadia Code"), FontSize = 11, Opacity = 0.6 }); }
        _logger.Log("INSPECT", $"Inode {fi.InodeNum}: {fi.Name}");
        await new ContentDialog { Title = $"Inode: {fi.Name}", Content = new ScrollViewer { Content = panel, MaxHeight = 500 }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    // ═══════════════════ SECTOR VIEWER ═══════════════════

    async void OnShowSectorViewer(object s, RoutedEventArgs e)
    {
        var disk = _activeTab?.Disk; if (disk == null) { SetStatus("No disk loaded"); return; }
        int currentSector = 0; bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var panel = new StackPanel { Spacing = 8, MinWidth = 600 };
        var navRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var sectorBox = new NumberBox { Value = 0, Minimum = 0, Maximum = disk.SectorCount - 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 150, Header = "Sector" };
        var goBtn = new Button { Content = "Go", Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6) };
        var infoText = new TextBlock { FontSize = 11, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
        navRow.Children.Add(sectorBox); navRow.Children.Add(goBtn); navRow.Children.Add(infoText);
        panel.Children.Add(navRow);
        var hexBorder = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(isDark ? ColorHelper.FromArgb(255, 30, 30, 30) : ColorHelper.FromArgb(255, 250, 250, 250)), Padding = new Thickness(8), MinHeight = 300 };
        panel.Children.Add(hexBorder);
        void ShowSector(int n) { currentSector = n; sectorBox.Value = n; var data = disk.ReadSector(n); infoText.Text = $"Sector {n}/{disk.SectorCount} · {disk.SectorSize} bytes · Magic: {(data.Length >= 4 ? Encoding.ASCII.GetString(data, 0, 4).Replace('\0', '·') : "?")}"; var sb = new StringBuilder(); for (int i = 0; i < data.Length; i += 16) { sb.Append($"{i:X4}  "); for (int j = 0; j < 16 && i + j < data.Length; j++) { sb.Append($"{data[i + j]:X2} "); if (j == 7) sb.Append(' '); } sb.Append("│ "); for (int j = 0; j < 16 && i + j < data.Length; j++) { byte b = data[i + j]; sb.Append(b >= 32 && b < 127 ? (char)b : '·'); } sb.AppendLine(); } hexBorder.Child = new TextBlock { Text = sb.ToString(), FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 11, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap }; }
        goBtn.Click += (_, _) => ShowSector((int)sectorBox.Value);
        ShowSector(0); _logger.Log("SECTOR", "Opened sector viewer");
        await new ContentDialog { Title = "Sector Viewer", Content = new ScrollViewer { Content = panel, MaxHeight = 550 }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    // ═══════════════════ HEALTH DASHBOARD ═══════════════════

    void RefreshHealthDashboard()
    {
        HealthStatusContent.Children.Clear(); HealthVolumeDetails.Children.Clear();
        var vol = _activeTab?.Vol;
        if (vol == null) { HealthStatusContent.Children.Add(new TextBlock { Text = "No volume mounted. Open a disk image first.", FontSize = 14, Opacity = 0.5, TextWrapping = TextWrapping.Wrap }); return; }
        var issues = new List<string>();
        if (!vol.Super.CrcValid) issues.Add("Superblock CRC is invalid");
        var rootInode = vol.Stat("/"); if (rootInode == null) issues.Add("Root inode missing or corrupted"); else if (!rootInode.IsDir) issues.Add("Root inode is not a directory");
        if (vol.Super.FreeBlocks > vol.Super.MaxBlocks) issues.Add($"Free blocks ({vol.Super.FreeBlocks}) exceeds max ({vol.Super.MaxBlocks})");
        if (vol.Super.FreeInodes > vol.Super.MaxInodes) issues.Add($"Free inodes ({vol.Super.FreeInodes}) exceeds max ({vol.Super.MaxInodes})");
        bool healthy = issues.Count == 0;
        var statusGrid = new Grid { ColumnSpacing = 12 }; statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconBorder = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(healthy ? ColorHelper.FromArgb(255, 52, 211, 153) : ColorHelper.FromArgb(255, 239, 68, 68)) };
        iconBorder.Child = new FontIcon { Glyph = healthy ? "\uE73E" : "\uE783", FontSize = 24, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(iconBorder, 0); statusGrid.Children.Add(iconBorder);
        var statusInfo = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        statusInfo.Children.Add(new TextBlock { Text = healthy ? "HEALTHY" : $"{issues.Count} ISSUE(S) FOUND", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(healthy ? ColorHelper.FromArgb(255, 52, 211, 153) : ColorHelper.FromArgb(255, 239, 68, 68)) });
        statusInfo.Children.Add(new TextBlock { Text = healthy ? "No issues detected" : "Review issues below", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(statusInfo, 1); statusGrid.Children.Add(statusInfo);
        HealthStatusContent.Children.Add(statusGrid);
        foreach (var issue in issues) { var issueBorder = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(ColorHelper.FromArgb(15, 239, 68, 68)) }; issueBorder.Child = new TextBlock { Text = $"⚠ {issue}", FontSize = 12, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)) }; HealthStatusContent.Children.Add(issueBorder); }
        // Volume details
        void Detail(string l, string v) => AddPropRow(HealthVolumeDetails, l, v);
        Detail("Label", vol.Super.Label); Detail("Version", vol.Super.Version.ToString()); Detail("Generation", vol.Super.Generation.ToString());
        Detail("Sector Size", $"{vol.Super.SectorSize} bytes"); Detail("Total Sectors", vol.Super.TotalSectors.ToString("N0"));
        Detail("Max Inodes", vol.Super.MaxInodes.ToString()); Detail("Free Inodes", vol.Super.FreeInodes.ToString());
        Detail("Max Blocks", vol.Super.MaxBlocks.ToString()); Detail("Free Blocks", vol.Super.FreeBlocks.ToString());
        int usedKB = (vol.Super.MaxBlocks - vol.Super.FreeBlocks) * (_activeTab?.Disk?.SectorSize ?? 512) / 1024;
        int totalKB = vol.Super.MaxBlocks * (_activeTab?.Disk?.SectorSize ?? 512) / 1024;
        Detail("Used Space", $"{usedKB} / {totalKB} KB ({(vol.Super.MaxBlocks > 0 ? (double)(vol.Super.MaxBlocks - vol.Super.FreeBlocks) / vol.Super.MaxBlocks * 100 : 0):F1}%)");
        Detail("CRC Valid", vol.Super.CrcValid ? "✓ Yes" : "✗ No");
        try { Detail("Created", DateTimeOffset.FromUnixTimeSeconds(vol.Super.Ctime).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
        try { Detail("Modified", DateTimeOffset.FromUnixTimeSeconds(vol.Super.Mtime).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
    }

    void OnRefreshHealth(object s, RoutedEventArgs e) => RefreshHealthDashboard();
    async void OnExportHealthReport(object s, RoutedEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) return;
        var sb = new StringBuilder(); sb.AppendLine("AXFS Health Report"); sb.AppendLine($"Generated: {DateTime.Now}"); sb.AppendLine($"Volume: {vol.Super.Label}"); sb.AppendLine($"Generation: {vol.Super.Generation}");
        sb.AppendLine($"CRC Valid: {vol.Super.CrcValid}"); sb.AppendLine($"Inodes: {vol.Super.MaxInodes - vol.Super.FreeInodes}/{vol.Super.MaxInodes}"); sb.AppendLine($"Blocks: {vol.Super.MaxBlocks - vol.Super.FreeBlocks}/{vol.Super.MaxBlocks}");
        sb.AppendLine(); sb.AppendLine("=== Activity Log ==="); sb.Append(_logger.Format());
        var picker = new FileSavePicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd); picker.FileTypeChoices.Add("Text", new List<string> { ".txt" }); picker.SuggestedFileName = "health_report.txt";
        var file = await picker.PickSaveFileAsync(); if (file != null) { await File.WriteAllTextAsync(file.Path, sb.ToString()); ShowNotification("Report exported", InfoBarSeverity.Success); }
    }

    // ═══════════════════ DRAG & DROP ═══════════════════

    void OnDragOver(object s, DragEventArgs e) { if (_activeTab?.Vol == null) return; e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; if (e.AcceptedOperation == DataPackageOperation.Copy) e.DragUIOverride.Caption = "Import into AXFS"; }

    async void OnDrop(object s, DragEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if (IsActivePartitionLocked()) { ShowNotification("Locked", InfoBarSeverity.Warning); return; }
        var items = await e.DataView.GetStorageItemsAsync(); int count = 0;
        foreach (var item in items) { if (item is StorageFile file) { try { var buf = await FileIO.ReadBufferAsync(file); var d = new byte[buf.Length]; using (var r = Windows.Storage.Streams.DataReader.FromBuffer(buf)) r.ReadBytes(d); string target = _activeTab!.CurrentPath == "/" ? "/" + file.Name : _activeTab.CurrentPath + "/" + file.Name; if (vol.WriteFile(target, d)) count++; } catch { } } else if (item is StorageFolder folder) count += await ImportFolderRecursive(folder, _activeTab!.CurrentPath); }
        _sounds?.PlayHddAccess(); FlashActivityLeds(); if (_settings.AutoSave) SaveImageToDisk(); RefreshFileList(); UpdateVolumeInfo(); ShowNotification($"Imported {count} item(s)", InfoBarSeverity.Success); _logger.Log("DROP", $"Dropped {count} item(s)");
    }

    async void OnDragStarting(object s, DragItemsStartingEventArgs e)
    {
        var vol = _activeTab?.Vol; if (vol == null) return; var tempFiles = new List<StorageFile>();
        foreach (var item in e.Items) { if (item is FileItem fi && !fi.IsDir && fi.Name != "..") { string fp = _activeTab!.CurrentPath == "/" ? "/" + fi.Name : _activeTab.CurrentPath + "/" + fi.Name; byte[]? d = vol.ReadFile(fp); if (d == null) continue; var tf = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(fi.Name, CreationCollisionOption.ReplaceExisting); await FileIO.WriteBytesAsync(tf, d); tempFiles.Add(tf); } }
        if (tempFiles.Count > 0) { e.Data.SetStorageItems(tempFiles, readOnly: true); e.Data.RequestedOperation = DataPackageOperation.Copy; }
    }

    // ═══════════════════ DIAGNOSTICS ═══════════════════

    async void OnShowDiagnostics(object s, RoutedEventArgs e) => await ShowDiagnosticsDialog();
    async Task ShowDiagnosticsDialog(string? lo = null)
    {
        var log = lo ?? string.Join("\n", _activeTab?.ScanLog ?? new List<string>()); if (string.IsNullOrEmpty(log)) log = "No diagnostic data.";
        await new ContentDialog { Title = "Image Diagnostics", CloseButtonText = "Close", XamlRoot = Content.XamlRoot, Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock { Text = log, FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } } }.ShowAsync();
    }

    // ═══════════════════ SETTINGS ═══════════════════

    void InitializeSettingsUI()
    {
        _isLoadingSettings = true;
        ThemeCombo.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        WindowSizeCombo.SelectedIndex = _settings.WindowWidth switch { <= 1000 => 0, >= 1400 => 2, _ => 1 };
        ConfirmDeleteToggle.IsOn = _settings.ConfirmDelete; AutoSaveToggle.IsOn = _settings.AutoSave;
        DefaultLabelBox.Text = _settings.DefaultVolumeLabel; DefaultSizeBox.Value = _settings.DefaultImageSizeKB;
        SyntaxHighlightToggle.IsOn = _settings.SyntaxHighlighting; LineNumbersToggle.IsOn = _settings.ShowLineNumbers;
        EditorFontSizeBox.Value = _settings.EditorFontSize; LuaLspToggle.IsOn = _settings.LuaLspEnabled;
        MasterVolumeSlider.Value = _settings.MasterVolume; GeneralSoundsToggle.IsOn = _settings.GeneralSoundsEnabled; BeepSoundsToggle.IsOn = _settings.BeepSoundsEnabled;
        UpdateMuteIcons(); _isLoadingSettings = false;
    }

    void ApplySoundSettings() { if (_sounds == null) return; _sounds.MasterVolume = _settings.MasterVolume / 100.0; _sounds.GeneralEnabled = _settings.GeneralSoundsEnabled; _sounds.BeepEnabled = _settings.BeepSoundsEnabled; _sounds.GeneralMuted = _settings.GeneralMuted; _sounds.BeepMuted = _settings.BeepMuted; _sounds.UpdateVolumes(); }
    void UpdateMuteIcons() { MasterMuteIcon.Glyph = (_settings.GeneralMuted && _settings.BeepMuted) ? "\uE74F" : "\uE995"; GeneralMuteIcon.Glyph = _settings.GeneralMuted ? "\uE74F" : "\uE995"; BeepMuteIcon.Glyph = _settings.BeepMuted ? "\uE74F" : "\uE995"; }

    void OnMasterVolumeChanged(object s, RangeBaseValueChangedEventArgs e) { if (!_isLoadingSettings) { _settings.MasterVolume = (int)e.NewValue; _settings.Save(); ApplySoundSettings(); } }
    void OnMasterMuteToggle(object s, RoutedEventArgs e) { bool all = _settings.GeneralMuted && _settings.BeepMuted; _settings.GeneralMuted = !all; _settings.BeepMuted = !all; _settings.Save(); ApplySoundSettings(); UpdateMuteIcons(); }
    void OnGeneralMuteToggle(object s, RoutedEventArgs e) { _settings.GeneralMuted = !_settings.GeneralMuted; _settings.Save(); ApplySoundSettings(); UpdateMuteIcons(); }
    void OnBeepMuteToggle(object s, RoutedEventArgs e) { _settings.BeepMuted = !_settings.BeepMuted; _settings.Save(); ApplySoundSettings(); UpdateMuteIcons(); }
    void OnSoundSettingToggled(object s, RoutedEventArgs e) { if (!_isLoadingSettings) { _settings.GeneralSoundsEnabled = GeneralSoundsToggle.IsOn; _settings.BeepSoundsEnabled = BeepSoundsToggle.IsOn; _settings.Save(); ApplySoundSettings(); } }
    void OnEditorSettingToggled(object s, RoutedEventArgs e) { if (!_isLoadingSettings) { _settings.SyntaxHighlighting = SyntaxHighlightToggle.IsOn; _settings.ShowLineNumbers = LineNumbersToggle.IsOn; _settings.LuaLspEnabled = LuaLspToggle.IsOn; _settings.Save(); } }
    void OnEditorFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (!_isLoadingSettings && !double.IsNaN(args.NewValue)) { _settings.EditorFontSize = (int)args.NewValue; _settings.Save(); } }
    void OnThemeComboChanged(object s, SelectionChangedEventArgs e) { if (!_isLoadingSettings && ThemeCombo.SelectedItem is ComboBoxItem item) { var t = item.Tag?.ToString() ?? "System"; _settings.Theme = t; _settings.Save(); ApplyTheme(t); RebuildTabStrip(); UpdateThemeToggleIcon(); } }
    void OnWindowSizeChanged(object s, SelectionChangedEventArgs e) { if (!_isLoadingSettings && WindowSizeCombo.SelectedItem is ComboBoxItem item) { var (w, h) = item.Tag?.ToString() switch { "compact" => (900, 550), "large" => (1500, 900), _ => (1200, 750) }; _settings.WindowWidth = w; _settings.WindowHeight = h; _settings.Save(); ResizeWindow(w, h); } }
    void OnSettingToggled(object s, RoutedEventArgs e) { if (!_isLoadingSettings) { _settings.ConfirmDelete = ConfirmDeleteToggle.IsOn; _settings.AutoSave = AutoSaveToggle.IsOn; _settings.Save(); } }
    void OnSettingTextChanged(object s, TextChangedEventArgs e) { if (!_isLoadingSettings) { _settings.DefaultVolumeLabel = DefaultLabelBox.Text; _settings.Save(); } }
    void OnSettingNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (!_isLoadingSettings && !double.IsNaN(args.NewValue)) { _settings.DefaultImageSizeKB = (int)args.NewValue; _settings.Save(); } }

    async void OnResetSettings(object s, RoutedEventArgs e)
    {
        var dlg = new ContentDialog { Title = "Reset Settings", Content = "Reset all settings to defaults?", PrimaryButtonText = "Reset", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _settings.Reset(); InitializeSettingsUI(); ApplySoundSettings(); ApplyTheme(_settings.Theme); ResizeWindow(_settings.WindowWidth, _settings.WindowHeight);
        ShowNotification("Settings reset to defaults", InfoBarSeverity.Informational);
    }

    void ApplyTheme(string t) { if (Content is FrameworkElement root) root.RequestedTheme = t switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default }; }
    void ResizeWindow(int w, int h) { try { var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd); var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wid); aw.Resize(new Windows.Graphics.SizeInt32(w, h)); } catch { } }

    // ═══════════════════ HELPERS ═══════════════════

    void SetStatus(string t) => StatusText.Text = t;
    void ShowNotification(string msg, InfoBarSeverity sev = InfoBarSeverity.Informational) { NotificationBar.Message = msg; NotificationBar.Severity = sev; NotificationBar.IsOpen = true; _notifTimer?.Stop(); _notifTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; _notifTimer.Tick += (s, e) => { NotificationBar.IsOpen = false; _notifTimer.Stop(); }; _notifTimer.Start(); }
    void UpdateConnectionIndicator() { var vol = _activeTab?.Vol; var disk = _activeTab?.Disk; Windows.UI.Color c; string tt; if (vol != null) { c = Windows.UI.Color.FromArgb(255, 52, 211, 153); tt = "Volume mounted"; } else if (disk != null) { c = Windows.UI.Color.FromArgb(255, 251, 191, 36); tt = "Disk open — no volume"; } else { c = Windows.UI.Color.FromArgb(60, 128, 128, 128); tt = "No image loaded"; } ConnectionDot.Background = new SolidColorBrush(c); ConnectionPulse.Background = new SolidColorBrush(c); ToolTipService.SetToolTip(ConnectionDot, tt); }

    void OnWindowClosed(object sender, WindowEventArgs e) { _animationTimer?.Stop(); _ledTimer?.Stop(); _sounds?.Dispose(); foreach (var tab in _tabs) tab.Dispose(); _tabs.Clear(); }
}