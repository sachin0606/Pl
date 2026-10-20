using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using Forms = System.Windows.Forms;

namespace BroadcastPlayout.Views;

public partial class MediaAssetManagementWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<MamAsset> Assets { get; } = [];
    public ICollectionView AssetsView { get; }
    private string? _selectedRoot;
    private MamAsset? _selectedAsset;
    private int _scanGeneration;
    private bool _uiReady;
    private readonly EnterpriseMamCatalog _catalog = new();

    public string? SelectedRoot { get => _selectedRoot; set { if (_selectedRoot == value) return; _selectedRoot = value; Raise(); } }
    public MamAsset? SelectedAsset { get => _selectedAsset; set { if (ReferenceEquals(_selectedAsset, value)) return; _selectedAsset = value; Raise(); } }

    public MediaAssetManagementWindow()
    {
        // Build the collection view before XAML initialization. ComboBox SelectionChanged
        // can fire while InitializeComponent is still constructing the visual tree.
        // Keeping the view ready and suppressing UI handlers until initialization completes
        // prevents the MAM standalone app from failing its startup smoke test.
        AssetsView = CollectionViewSource.GetDefaultView(Assets);
        AssetsView.Filter = FilterAsset;
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        DataContext = this;
        LoadRoots();
        _uiReady = true;
        Loaded += async (_, _) => { if (SelectedRoot is not null) await ScanAsync(); };
    }

    private void LoadRoots()
    {
        var settings = SettingsStore.Load();
        foreach (var folder in settings.MediaFolders.Where(Directory.Exists)) AddRoot(folder);
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady).Select(x => x.RootDirectory.FullName)) AddRoot(drive);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); if (Directory.Exists(videos)) AddRoot(videos);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); if (Directory.Exists(desktop)) AddRoot(desktop);
        SelectedRoot = Roots.FirstOrDefault(x => x.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)) ?? Roots.FirstOrDefault();
    }

    private void AddRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        if (!Roots.Any(x => x.Equals(path, StringComparison.OrdinalIgnoreCase))) Roots.Add(path);
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedRoot) || !Directory.Exists(SelectedRoot)) return;
        var generation = Interlocked.Increment(ref _scanGeneration);
        StatusText.Text = $"Indexing {SelectedRoot} …";
        var indexed = await Task.Run(() =>
        {
            var result = new List<(MediaLibraryItem Media, MediaAssetMetadata Metadata, EnterpriseMamCatalog.Asset Catalog)>();
            foreach (var media in MediaLibraryService.ScanFolder(SelectedRoot, true, 15000))
            {
                var metadata = MediaAssetMetadataStore.Get(media.FilePath);
                if (string.IsNullOrWhiteSpace(metadata.Category)) metadata.Category = media.Category;
                try
                {
                    var catalog = _catalog.RegisterFile(media.FilePath, metadata, "MAM scan");
                    result.Add((media, metadata, catalog));
                }
                catch { }
            }
            return result;
        });
        if (generation != Volatile.Read(ref _scanGeneration)) return;
        Assets.Clear();
        foreach (var entry in indexed)
            Assets.Add(new MamAsset(entry.Media, entry.Metadata, entry.Catalog));
        AssetsView.Refresh();
        UpdateCount();
        StatusText.Text = $"Indexed {Assets.Count:n0} asset(s) from {SelectedRoot}";
    }

    private bool FilterAsset(object obj)
    {
        if (obj is not MamAsset asset) return false;
        var q = SearchBox?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var haystack = string.Join(" ", asset.DisplayTitle, asset.Media.FileName, asset.Metadata.Tags, asset.Metadata.Description, asset.Metadata.Category, asset.Media.Format, asset.Media.FilePath);
            if (!haystack.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
        }
        var category = (CategoryFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("ALL CATEGORIES", StringComparison.OrdinalIgnoreCase) && !asset.Metadata.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) return false;
        var type = (TypeFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (type == "VIDEO" && IsAudio(asset.Media.FilePath)) return false;
        if (type == "AUDIO" && !IsAudio(asset.Media.FilePath)) return false;
        return true;
    }

    private static bool IsAudio(string path) => new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void UpdateCount()
    {
        if (!_uiReady || ResultCountText is null) return;
        ResultCountText.Text = $"{AssetsView.Cast<object>().Count():n0} ASSETS";
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private async void Root_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await ScanAsync(); }
    private void Search_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        AssetsView.Refresh();
        UpdateCount();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        using var d = new Forms.FolderBrowserDialog { Description = "Add MAM media root", UseDescriptionForTitle = true };
        if (d.ShowDialog() != Forms.DialogResult.OK) return;
        AddRoot(d.SelectedPath);
        var settings = SettingsStore.Load();
        if (!settings.MediaFolders.Any(x => x.Equals(d.SelectedPath, StringComparison.OrdinalIgnoreCase))) settings.MediaFolders.Add(d.SelectedPath);
        SettingsStore.Save(settings);
        SelectedRoot = d.SelectedPath; RootList.SelectedItem = d.SelectedPath;
    }

    private async void AssetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAsset is null) return;
        StatusText.Text = "Probing " + SelectedAsset.Media.FileName;
        await MediaLibraryService.ProbeAsync(SelectedAsset.Media);
        SelectedAsset.RaiseAll();
        RefreshHistory_Click(this, new RoutedEventArgs());
        StatusText.Text = SelectedAsset.Media.Format;
    }

    private void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) return;
        try
        {
            SelectedAsset.Metadata.LastUsedUtc = DateTime.UtcNow;
            MediaAssetMetadataStore.Set(SelectedAsset.Media.FilePath, SelectedAsset.Metadata);
            SelectedAsset.Media.Category = SelectedAsset.Metadata.Category;
            SelectedAsset.Catalog.Title = string.IsNullOrWhiteSpace(SelectedAsset.Metadata.Title) ? SelectedAsset.Media.ParsedName : SelectedAsset.Metadata.Title.Trim();
            _catalog.UpdateWorkflow(SelectedAsset.Catalog);
            SelectedAsset.RefreshCatalog(_catalog.Get(SelectedAsset.Catalog.AssetId));
            SelectedAsset.RaiseAll();
            AssetsView.Refresh();
            StatusText.Text = "Metadata + workflow saved · " + SelectedAsset.DisplayTitle;
        }
        catch (Exception ex) { StatusText.Text = "Save failed · " + ex.GetBaseException().Message; }
    }

    private async void VerifyChecksum_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) return;
        try
        {
            StatusText.Text = "Computing SHA-256 · " + SelectedAsset.DisplayTitle;
            var updated = await _catalog.VerifyIntegrityAsync(SelectedAsset.Catalog.AssetId);
            SelectedAsset.RefreshCatalog(updated); SelectedAsset.RaiseAll();
            StatusText.Text = updated.IntegritySummary;
        }
        catch (Exception ex) { StatusText.Text = "Integrity check failed · " + ex.GetBaseException().Message; }
    }

    private async void RunQc_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) return;
        try
        {
            StatusText.Text = "Running QC · " + SelectedAsset.DisplayTitle;
            var settings = SettingsStore.Load();
            var result = await QualityControlService.CheckAsync(SelectedAsset.Media.FilePath, settings.QcPreset?.Clone() ?? new MediaQcPreset(), deepDecode: false);
            _catalog.SetQc(SelectedAsset.Catalog.AssetId, result.Passed ? "PASS" : "FAIL", result.Summary);
            SelectedAsset.RefreshCatalog(_catalog.Get(SelectedAsset.Catalog.AssetId)); SelectedAsset.RaiseAll();
            StatusText.Text = result.Summary;
        }
        catch (Exception ex) { StatusText.Text = "QC failed · " + ex.GetBaseException().Message; }
    }

    private async void GenerateProxy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) return;
        try
        {
            StatusText.Text = "Generating H.264 proxy · " + SelectedAsset.DisplayTitle;
            var output = await ProxyGenerationService.GenerateAsync(SelectedAsset.Media.FilePath);
            _catalog.LinkProxy(SelectedAsset.Catalog.AssetId, output);
            SelectedAsset.RefreshCatalog(_catalog.Get(SelectedAsset.Catalog.AssetId)); SelectedAsset.RaiseAll();
            StatusText.Text = "Proxy ready · " + output;
        }
        catch (Exception ex) { StatusText.Text = "Proxy failed · " + ex.GetBaseException().Message; }
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) return;
        try
        {
            StatusText.Text = "Archiving + verifying · " + SelectedAsset.DisplayTitle;
            if (string.IsNullOrWhiteSpace(SelectedAsset.Catalog.Sha256))
                SelectedAsset.RefreshCatalog(await _catalog.VerifyIntegrityAsync(SelectedAsset.Catalog.AssetId));
            var path = await _catalog.ArchiveAsync(SelectedAsset.Catalog.AssetId);
            SelectedAsset.RefreshCatalog(_catalog.Get(SelectedAsset.Catalog.AssetId)); SelectedAsset.RaiseAll();
            StatusText.Text = "Archive verified · " + path;
        }
        catch (Exception ex) { StatusText.Text = "Archive failed · " + ex.GetBaseException().Message; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) return;
        try
        {
            StatusText.Text = "Restoring master · " + SelectedAsset.DisplayTitle;
            var path = await _catalog.RestoreAsync(SelectedAsset.Catalog.AssetId);
            SelectedAsset.RefreshCatalog(_catalog.Get(SelectedAsset.Catalog.AssetId)); SelectedAsset.RaiseAll();
            StatusText.Text = "Restored · " + path;
        }
        catch (Exception ex) { StatusText.Text = "Restore failed · " + ex.GetBaseException().Message; }
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAsset is null) { HistoryBox.Text = string.Empty; return; }
        HistoryBox.Text = string.Join(Environment.NewLine, _catalog.History(SelectedAsset.Catalog.AssetId, 100).Select(x => $"{x.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {x.Action} · {x.Detail} · {x.Actor}"));
    }

    private void AssetGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var paths = AssetGrid.SelectedItems.Cast<MamAsset>().Select(x => x.Media.FilePath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 && SelectedAsset is not null && File.Exists(SelectedAsset.Media.FilePath)) paths = [SelectedAsset.Media.FilePath];
        if (paths.Length == 0) return;
        var data = new DataObject(); data.SetData(DataFormats.FileDrop, paths); data.SetData("Kashtrix.MAM.Assets", string.Join("\n", paths));
        DragDrop.DoDragDrop(AssetGrid, data, DragDropEffects.Copy);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e) { if (SelectedAsset is null) return; try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedAsset.Media.FilePath}\"") { UseShellExecute = true }); } catch { } }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (SelectedAsset is not null) Clipboard.SetText(SelectedAsset.Media.FilePath); }
    private void FileManager_Click(object sender, RoutedEventArgs e) => Launch("FileManager");
    private void Ingest_Click(object sender, RoutedEventArgs e) => Launch("IngestServer");
    private void CgEditor_Click(object sender, RoutedEventArgs e) => Launch("CGEditor");
    private void Launch(string key) { if (!StandaloneAppLauncher.Launch(key, out var error)) StatusText.Text = error; }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; return; } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MamAsset : INotifyPropertyChanged
{
    public MamAsset(MediaLibraryItem media, MediaAssetMetadata metadata, EnterpriseMamCatalog.Asset catalog) { Media = media; Metadata = metadata; Catalog = catalog; }
    public MediaLibraryItem Media { get; }
    public MediaAssetMetadata Metadata { get; }
    public EnterpriseMamCatalog.Asset Catalog { get; private set; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Metadata.Title) ? Media.ParsedName : Metadata.Title;
    public string ModifiedText => Media.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string AssetVersionText => $"v{Catalog.Version}";
    public string DuplicateText => Catalog.DuplicateCount == 0 ? "UNIQUE" : $"{Catalog.DuplicateCount} DUP";
    public string HistorySummary => $"{Catalog.ApprovalState} · {Catalog.QcStatus} · {Catalog.StorageState}";
    public void RefreshCatalog(EnterpriseMamCatalog.Asset? updated) { if (updated is not null) Catalog = updated; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaiseAll() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
}
