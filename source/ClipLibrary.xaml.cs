using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf = System.Windows;
using C = System.Windows.Controls;

namespace ClipIt;

public partial class ClipLibrary : C.UserControl, IDisposable
{
    private readonly ObservableCollection<ClipItem> clips = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly CancellationTokenSource lifetime = new();
    private FileSystemWatcher? watcher;
    private FileSystemWatcher? portalWatcher;
    private string folder = "";
    private string portalFolder = "";
    private int generation;
    private Wpf.Point dragStart;
    private ClipItem? mouseClip;
    private bool dragged, disposed;
    private string? selectedGame;
    private readonly C.ItemsPanelTemplate listPanel;
    private readonly Wpf.DataTemplate listTemplate;
    private C.ScrollViewer? clipScroll;
    private bool showingTiles;
    private ClipItem? menuClip;
    private readonly HashSet<string> deleting = new(StringComparer.OrdinalIgnoreCase);
    public bool Busy => Editor.Busy;
    internal event Action<bool>? GameViewPreferenceChanged;
    private readonly Dictionary<string, ClipItem> items = new(StringComparer.OrdinalIgnoreCase);
    public ClipLibrary()
    {
        InitializeComponent(); ClipsList.ItemsSource = clips;
        listPanel = ClipsList.ItemsPanel;
        listTemplate = ClipsList.ItemTemplate;
        refreshTimer.Tick += async (_, _) => { refreshTimer.Stop(); await Reload(); };
        IsVisibleChanged += async (_, _) => { if (IsVisible) await Reload(); else Editor.Pause(); };
    }
    public void SetFolder(string value, string portal = "")
    {
        if (!string.Equals(folder, value, StringComparison.OrdinalIgnoreCase))
        {
            folder = value; watcher?.Dispose(); watcher = null;
            try { Directory.CreateDirectory(folder); watcher = WatchFolder(folder, "*.mp4", NotifyFilters.FileName); }
            catch (Exception ex) { LibraryMessage.Text = "This folder is unavailable: " + ex.Message; }
        }
        if (!string.Equals(portalFolder, portal, StringComparison.OrdinalIgnoreCase))
        {
            portalFolder = portal;
            portalWatcher?.Dispose(); portalWatcher = null;
        }
        _ = Reload();
    }
    private FileSystemWatcher WatchFolder(string path, string filter, NotifyFilters names)
    {
        var source = new FileSystemWatcher(path, filter)
        {
            IncludeSubdirectories = true, NotifyFilter = names | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        source.Created += Watch; source.Changed += Watch; source.Deleted += Watch; source.Renamed += Watch;
        try { source.EnableRaisingEvents = true; }
        catch { source.Dispose(); throw; }
        return source;
    }
    private void Watch(object sender, FileSystemEventArgs e)
    {
        if (disposed) return;
        if (e.FullPath.StartsWith(Path.Combine(folder, ".clipit-buffer") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try { _ = Dispatcher.BeginInvoke(() => { if (!disposed) { refreshTimer.Stop(); refreshTimer.Start(); } }); }
        catch (InvalidOperationException) { }
    }
    public void Refresh() { if (!disposed) { refreshTimer.Stop(); refreshTimer.Start(); } }
    private static (FileInfo[] Files, string? Error) Scan(string directory)
    {
        try
        {
            return (new DirectoryInfo(directory).EnumerateFiles("*.mp4", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(f => !f.Name.StartsWith(".clipit-", StringComparison.OrdinalIgnoreCase)).ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ([], ex.Message);
        }
    }
    private async Task Reload()
    {
        if (disposed || folder.Length == 0) return;
        int current = ++generation;
        string directory = folder, portal = portalFolder;
        try
        {
            var (local, linked) = await Task.Run(() =>
                (Scan(directory), portal.Length == 0 ? (Files: Array.Empty<FileInfo>(), Error: (string?)null) : Scan(portal)), lifetime.Token);
            if (current != generation || disposed) return;
            if (local.Error != null) LibraryMessage.Text = "Could not load clips: " + local.Error;
            PortalMessage.Text = linked.Error == null ? "" : "Portal Clips folder is unavailable: " + linked.Error;
            PortalMessage.Visibility = linked.Error == null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
            if (linked.Error != null) { portalWatcher?.Dispose(); portalWatcher = null; }
            else if (portal.Length > 0 && portalWatcher == null)
            {
                try { portalWatcher = WatchFolder(portal, "*", NotifyFilters.FileName | NotifyFilters.DirectoryName); }
                catch (Exception ex) { Log.Write("Could not watch Portal Clips folder: " + ex.Message); }
            }
            var next = new Dictionary<string, ClipItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in local.Files.Concat(linked.Files))
            {
                if (next.ContainsKey(f.FullName)) continue;
                bool isLinked = !ClipFiles.IsInLibrary(f.FullName, directory);
                string game = isLinked ? ClipFiles.PortalGameName(f.FullName, portal) : ClipFiles.GameName(f.FullName, directory);
                bool favorite = ClipFiles.IsFavorite(f.FullName, directory);
                var stamp = new ClipStamp(f.Length, f.LastWriteTimeUtc);
                if (!items.TryGetValue(f.FullName, out var item) || item.Stamp != stamp || item.Path != f.FullName
                    || item.Application != game || item.IsLinked != isLinked)
                    item = new ClipItem(f.FullName, stamp, game, favorite, isLinked);
                else item.IsFavorite = favorite;
                next.Add(item.Path, item);
            }
            items.Clear();
            foreach (var (path, item) in next) items[path] = item;
            UpdateView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (current == generation && !disposed) LibraryMessage.Text = "Could not load clips: " + ex.Message; }
    }
    private void UpdateView()
    {
        if (GamesList == null) return; // Checked can fire while XAML is still loading.
        long bytes = items.Values.Sum(c => c.Stamp.Size);
        double megabytes = bytes / 1048576.0;
        string size = megabytes > 1000 ? MediaTools.Gigabytes(bytes) : $"{megabytes:0.#} MB";
        SummaryLabel.Text = $"{items.Count} {(items.Count == 1 ? "clip" : "clips")}  ·  {size}  ·  Newest first";
        bool byGame = ByGameTab.IsChecked == true;
        bool favorites = FavoritesTab.IsChecked == true;
        var ordered = items.Values.OrderByDescending(c => c.Stamp.Modified).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var games = ordered.GroupBy(c => c.Application, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameFolder(g.Key, g.Count())).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        bool folders = byGame && selectedGame == null;
        GamesList.ItemsSource = games;
        GamesList.Visibility = folders ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        ClipsList.Visibility = folders ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        GameNavigation.Visibility = byGame && selectedGame != null ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        GameTitle.Text = selectedGame ?? "";
        clips.Clear();
        var visibleClips = favorites ? ordered.Where(c => c.IsFavorite)
            : byGame ? ordered.Where(c => string.Equals(c.Application, selectedGame, StringComparison.OrdinalIgnoreCase))
            : ordered;
        foreach (var item in visibleClips) clips.Add(item);
        UpdateGameView();
        if (showingTiles) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LoadVisibleTiles);
        EmptyState.Visibility = (folders ? games.Length == 0 : clips.Count == 0) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        EmptyTitle.Text = favorites ? "No favorites yet." : selectedGame != null && byGame ? "No clips in this game yet." : "Your next highlight lives here.";
        EmptyHint.Text = favorites ? "Open a clip and choose Mark as Favorite to add it here." : "Save a replay, or drop MP4 clips here to add them.";
        LibraryHint.Text = folders ? "Games A–Z. Open a folder to see its clips." : "Newest first. Click to open, right-click for options, or drag to another app.";
    }
    private void Tab_Changed(object sender, Wpf.RoutedEventArgs e) => UpdateView();
    internal void RestoreGameView(bool tiles) => (tiles ? TileViewButton : ListViewButton).IsChecked = true;

    private void GameView_Changed(object sender, Wpf.RoutedEventArgs e)
    {
        UpdateGameView();
        try { GameViewPreferenceChanged?.Invoke(TileViewButton.IsChecked == true); }
        catch (Exception ex) { LibraryMessage.Text = "Could not save the game view: " + ex.Message; }
    }
    private void UpdateGameView()
    {
        if (ClipsList == null) return;
        bool tiles = ByGameTab.IsChecked == true && selectedGame != null && TileViewButton.IsChecked == true;
        if (tiles == showingTiles) return;
        showingTiles = tiles;
        ClipsList.ItemsPanel = tiles ? (C.ItemsPanelTemplate)Resources["TilePanel"] : listPanel;
        ClipsList.ItemTemplate = tiles ? (Wpf.DataTemplate)Resources["TileTemplate"] : listTemplate;
        ClipsList.ItemContainerStyle = (Wpf.Style)Resources[tiles ? "TileCard" : "ClipCard"];
        if (tiles) ClipsList.SetValue(C.ScrollViewer.CanContentScrollProperty, false);
        else ClipsList.ClearValue(C.ScrollViewer.CanContentScrollProperty);
        clipScroll?.ScrollToTop();
        if (tiles) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LoadVisibleTiles);
    }

    private void Clips_ScrollChanged(object sender, C.ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not C.ScrollViewer scroll) return;
        clipScroll = scroll;
        LoadVisibleTiles();
    }
    private void LoadVisibleTiles()
    {
        if (disposed || !showingTiles || !ClipsList.IsVisible
            || clipScroll is not { ViewportWidth: > 0, ViewportHeight: > 0 } scroll) return;
        var (start, end) = ClipTilePanel.VisibleItems(scroll.ViewportWidth, scroll.VerticalOffset, scroll.ViewportHeight, clips.Count);
        for (int i = start; i < end; i++) RequestThumbnail(clips[i]);
    }
    private void Games_Back(object sender, Wpf.RoutedEventArgs e) { selectedGame = null; UpdateView(); }
    private void Game_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Wpf.DependencyObject source && (C.ItemsControl.ContainerFromElement(GamesList, source) as C.ListBoxItem)?.DataContext is GameFolder game)
        { OpenGame(game.Name); e.Handled = true; }
    }
    private void Game_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GamesList.SelectedItem is GameFolder game) { OpenGame(game.Name); e.Handled = true; }
    }
    private void OpenGame(string name) { selectedGame = name; UpdateView(); }
    private sealed record GameFolder(string Name, int Count) { public string Detail => $"{Count} {(Count == 1 ? "clip" : "clips")}"; }

    private void Clip_RightClick(object sender, MouseButtonEventArgs e)
    {
        menuClip = ItemAt(e.OriginalSource);
        ClipsList.SelectedItem = menuClip;
        mouseClip = null;
    }
    private void ClipMenu_Opened(object sender, Wpf.RoutedEventArgs e)
    {
        if (sender is not C.ContextMenu menu) return;
        menu.Tag = menuClip ?? ClipsList.SelectedItem;
        menuClip = null;
        if (menu.Tag is not ClipItem item || deleting.Contains(item.Path) || Editor.Busy) menu.IsOpen = false;
        else DeleteMenuItem.IsEnabled = !item.IsLinked;
    }
    private ClipItem? MenuClip => ClipsList.ContextMenu.Tag as ClipItem;
    private void Copy_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (MenuClip is not ClipItem item) return;
        try
        {
            if (!File.Exists(item.Path)) throw new IOException("This clip is no longer available.");
            Wpf.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { item.Path });
            LibraryMessage.Text = "Clip copied. Paste it into a folder or another app.";
        }
        catch (Exception ex) { LibraryMessage.Text = "Could not copy clip: " + ex.Message; }
    }
    private async void Delete_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (MenuClip is not ClipItem { IsLinked: false } item || Editor.Busy || !deleting.Add(item.Path)) return;
        try
        {
            if (item.PreviewTask is { IsCompleted: false } preview)
            {
                item.PreviewCancellation?.Cancel();
                await preview;
            }
            // Recycle rather than permanently deleting so accidental clicks are recoverable.
            await Task.Run(() =>
            {
                if (File.Exists(item.Path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (File.Exists(item.Path)) throw new IOException("The file is still in the clip folder.");
                ClipFiles.RemoveLabels(item.Path);
            });
            if (disposed) return;
            ++generation; // Ignore any directory snapshot taken before the file was removed.
            items.Remove(item.Path);
            UpdateView();
            LibraryMessage.Text = "Clip moved to the Recycle Bin.";
        }
        catch (Exception ex) { LibraryMessage.Text = "Could not delete clip: " + ex.Message; }
        finally { deleting.Remove(item.Path); Refresh(); }
    }
    private void Thumbnail_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        if (sender is C.Image { DataContext: ClipItem item }) RequestThumbnail(item);
    }
    private void RequestThumbnail(ClipItem item)
    {
        if (item.PreviewTask is { IsCompleted: false } || deleting.Contains(item.Path)
            || item.PreviewFailed || (item.Thumbnail != null && (showingTiles || item.DetailsLoaded))) return;
        item.PreviewTask = LoadThumbnail(item);
    }
    private async Task LoadThumbnail(ClipItem item)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        item.PreviewCancellation = cancellation;
        try
        {
            string thumbnail = await MediaTools.Thumbnail(item.Path, item.Stamp, cancellation.Token);
            if (item.Thumbnail == null)
            {
                var image = await Task.Run(() =>
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 320;
                    bitmap.UriSource = new Uri(thumbnail);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                item.SetThumbnail(image);
            }
            if (!showingTiles)
            {
                var info = await MediaTools.PreviewInfo(item.Path, thumbnail, cancellation.Token);
                item.SetDetails(info);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            item.PreviewFailed = item.Thumbnail == null;
            item.DetailsLoaded = true;
            if (item.PreviewFailed) item.SetDetail($"{MediaTools.Gigabytes(item.Stamp.Size)}  ·  Preview unavailable · click to retry playback");
        }
        finally { item.PreviewCancellation = null; }
    }
    public async Task OpenClip(string path)
    {
        if (Editor.Busy || deleting.Contains(path)) return;
        if (items.TryGetValue(path, out var item)) item.PreviewCancellation?.Cancel();
        Dashboard.Visibility = Wpf.Visibility.Collapsed; Editor.Visibility = Wpf.Visibility.Visible;
        await Editor.Open(path, folder, item is { IsLinked: true } ? item.Application : null);
    }
    public void ShowDashboard()
    {
        if (Editor.Busy) return;
        Editor.CloseMedia(); Editor.Visibility = Wpf.Visibility.Collapsed;
        Dashboard.Visibility = Wpf.Visibility.Visible; Refresh();
    }
    public void ShowRecent()
    {
        ShowDashboard(); RecentTab.IsChecked = true; UpdateView();
    }
    private ClipItem? ItemAt(object source)
    {
        if (source is not Wpf.DependencyObject dependency) return null;
        return (C.ItemsControl.ContainerFromElement(ClipsList, dependency) as C.ListBoxItem)?.DataContext as ClipItem;
    }
    private void Clip_MouseDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); mouseClip = ItemAt(e.OriginalSource); dragged = false; }
    private void Clip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || mouseClip == null || dragged) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - dragStart.X) < Wpf.SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < Wpf.SystemParameters.MinimumVerticalDragDistance) return;
        dragged = true;
        if (File.Exists(mouseClip.Path)) Wpf.DragDrop.DoDragDrop(ClipsList, new Wpf.DataObject(Wpf.DataFormats.FileDrop, new[] { mouseClip.Path }), Wpf.DragDropEffects.Copy);
    }
    private async void Clip_MouseUp(object sender, MouseButtonEventArgs e) { var item = mouseClip; mouseClip = null; if (!dragged && item != null && File.Exists(item.Path)) await OpenClip(item.Path); }
    private async void Clip_Key(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && ClipsList.SelectedItem is ClipItem item) { e.Handled = true; await OpenClip(item.Path); } }
    private void Editor_Back(object? sender, EventArgs e) => ShowDashboard();
    private void Editor_Changed(object? sender, string e) => _ = Reload();
    private void Refresh_Click(object sender, Wpf.RoutedEventArgs e) => Refresh();
    internal string ViewedFolder
    {
        get
        {
            // A game can include clips from several locations. Use its newest clip's source.
            if (ByGameTab.IsChecked != true || selectedGame == null || clips.FirstOrDefault() is not { } clip)
                return folder;

            string root = clip.IsLinked ? portalFolder : folder;
            if (root.Length == 0 || !ClipFiles.IsInLibrary(clip.Path, root)) return folder;

            string relative = Path.GetRelativePath(root, clip.Path);
            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            return separator < 0 ? root : Path.Combine(root, relative[..separator]);
        }
    }
    private void Folder_Click(object sender, Wpf.RoutedEventArgs e)
    {
        try
        {
            string target = ViewedFolder;
            if (target == folder) Directory.CreateDirectory(folder);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("This folder is unavailable: " + target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { LibraryMessage.Text = ex.Message; }
    }
    private async void Import_Click(object sender, Wpf.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import clips", Filter = "MP4 clips|*.mp4", Multiselect = true };
        if (dialog.ShowDialog(Wpf.Window.GetWindow(this)) == true) await Import(dialog.FileNames);
    }
    private void Library_DragOver(object sender, Wpf.DragEventArgs e)
    {
        e.Effects = e.Data.GetData(Wpf.DataFormats.FileDrop) is string[] files && !Editor.Busy && files.Any(CanImport) ? Wpf.DragDropEffects.Copy : Wpf.DragDropEffects.None;
        e.Handled = true;
    }
    private async void Library_Drop(object sender, Wpf.DragEventArgs e)
    {
        if (e.Data.GetData(Wpf.DataFormats.FileDrop) is string[] files && !Editor.Busy) await Import(files);
        e.Handled = true;
    }
    private async Task Import(IEnumerable<string> files)
    {
        int imported = 0;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (string source in files.Where(p => string.Equals(Path.GetExtension(p), ".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
            {
                if (ClipFiles.IsInLibrary(source, folder)) continue;
                string destination = Path.Combine(folder, Path.GetFileName(source)); int n = 1;
                while (File.Exists(destination)) destination = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(source)} ({n++}).mp4");
                // Import is a copy; never move or overwrite a file dropped from another app.
                string temporary = Path.Combine(folder, ".clipit-import-" + Guid.NewGuid().ToString("N") + ".mp4");
                try
                {
                    await Task.Run(() => File.Copy(source, temporary, false), lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, false); imported++;
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            }
            LibraryMessage.Text = imported > 0 ? $"Added {imported} {(imported == 1 ? "clip" : "clips")}. Originals stay where they were." : "Drop MP4 files from another folder to import them.";
            await Reload();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LibraryMessage.Text = "Import couldn't finish: " + ex.Message; }
    }
    private bool CanImport(string path)
    {
        try { return folder.Length > 0 && string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !ClipFiles.IsInLibrary(path, folder); }
        catch { return false; }
    }
    public void PausePlayback() => Editor.Suspend();
    public void Dispose() { disposed = true; ++generation; lifetime.Cancel(); watcher?.Dispose(); portalWatcher?.Dispose(); refreshTimer.Stop(); Editor.Dispose(); }

    private sealed class ClipItem : INotifyPropertyChanged
    {
        public string Path { get; }
        public ClipStamp Stamp { get; }
        public string Application { get; }
        public bool IsFavorite { get; set; }
        public bool IsLinked { get; }
        public string Title { get; }
        public string Date => Stamp.Modified.ToLocalTime().ToString("MMM d, yyyy  ·  h:mm tt");
        public string Detail { get; private set; }
        public BitmapImage? Thumbnail { get; private set; }
        public Task? PreviewTask;
        public CancellationTokenSource? PreviewCancellation;
        public bool PreviewFailed;
        public bool DetailsLoaded;
        public event PropertyChangedEventHandler? PropertyChanged;
        public ClipItem(string path, ClipStamp stamp, string application, bool isFavorite, bool isLinked)
        {
            Path = path; Stamp = stamp; Application = application; IsFavorite = isFavorite; IsLinked = isLinked;
            Title = isLinked ? System.IO.Path.GetFileNameWithoutExtension(path) : ClipFiles.Title(path, application);
            Detail = MediaTools.Gigabytes(stamp.Size);
        }
        public void SetThumbnail(BitmapImage image) { Thumbnail = image; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); }
        public void SetDetails(ClipPreviewInfo info)
        {
            DetailsLoaded = true;
            SetDetail($"{MediaTools.Time(info.Duration)}  ·  {info.Width} × {info.Height}  ·  {MediaTools.Gigabytes(Stamp.Size)}");
        }
        public void SetDetail(string text) { Detail = text; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
    }
}
