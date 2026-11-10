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
    private string folder = "";
    private int generation;
    private Wpf.Point dragStart;
    private ClipItem? mouseClip;
    private bool dragged, disposed;
    private string? selectedGame;
    private ClipItem? menuClip;
    private readonly HashSet<string> deleting = new(StringComparer.OrdinalIgnoreCase);
    public bool Busy => Editor.Busy;
    private readonly Dictionary<string, ClipItem> items = new(StringComparer.OrdinalIgnoreCase);
    public ClipLibrary()
    {
        InitializeComponent(); ClipsList.ItemsSource = clips;
        refreshTimer.Tick += async (_, _) => { refreshTimer.Stop(); await Reload(); };
        IsVisibleChanged += async (_, _) => { if (IsVisible) await Reload(); else Editor.Pause(); };
    }
    public void SetFolder(string value)
    {
        if (string.Equals(folder, value, StringComparison.OrdinalIgnoreCase)) return;
        folder = value; watcher?.Dispose(); watcher = null;
        try
        {
            Directory.CreateDirectory(folder);
            watcher = new FileSystemWatcher(folder, "*.mp4") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            watcher.Created += Watch; watcher.Changed += Watch; watcher.Deleted += Watch; watcher.Renamed += Watch;
            LibraryMessage.Text = "";
        }
        catch (Exception ex) { LibraryMessage.Text = "This folder is unavailable: " + ex.Message; }
        _ = Reload();
    }
    private void Watch(object sender, FileSystemEventArgs e)
    {
        if (disposed) return;
        if (e.FullPath.StartsWith(Path.Combine(folder, ".clipit-buffer") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try { _ = Dispatcher.BeginInvoke(() => { if (!disposed) { refreshTimer.Stop(); refreshTimer.Start(); } }); }
        catch (InvalidOperationException) { }
    }
    public void Refresh() { if (!disposed) { refreshTimer.Stop(); refreshTimer.Start(); } }
    private async Task Reload()
    {
        if (disposed || folder.Length == 0) return;
        int current = ++generation; string directory = folder;
        try
        {
            var files = await Task.Run(() => new DirectoryInfo(directory).EnumerateFiles("*.mp4", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(f => !f.Name.StartsWith(".clipit-", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTimeUtc).ToArray(), lifetime.Token);
            if (current != generation || disposed) return;
            var next = new List<ClipItem>();
            foreach (var f in files)
            {
                var stamp = new ClipStamp(f.Length, f.LastWriteTimeUtc);
                if (!items.TryGetValue(f.FullName, out var item) || item.Stamp != stamp || item.Path != f.FullName)
                    item = new ClipItem(f.FullName, stamp, ClipFiles.DisplayApplication(ClipFiles.Application(f.FullName, directory)));
                next.Add(item);
            }
            items.Clear();
            foreach (var item in next) items[item.Path] = item;
            UpdateView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LibraryMessage.Text = "Could not load clips: " + ex.Message; }
    }
    private void UpdateView()
    {
        if (GamesList == null) return; // Checked can fire while XAML is still loading.
        SummaryLabel.Text = $"{items.Count} {(items.Count == 1 ? "clip" : "clips")}  ·  {items.Values.Sum(c => c.Stamp.Size) / 1048576.0:0.#} MB  ·  Newest first";
        bool byGame = ByGameTab.IsChecked == true;
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
        foreach (var item in ordered.Where(c => !byGame || selectedGame == null || string.Equals(c.Application, selectedGame, StringComparison.OrdinalIgnoreCase))) clips.Add(item);
        EmptyState.Visibility = (folders ? games.Length == 0 : clips.Count == 0) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        EmptyTitle.Text = selectedGame != null && byGame ? "No clips in this game yet." : "Your next highlight lives here.";
        LibraryHint.Text = folders ? "Games A–Z. Open a folder to see its clips." : "Newest first. Click to edit, right-click to copy or delete, or drag to another app.";
    }
    private void Tab_Changed(object sender, Wpf.RoutedEventArgs e) => UpdateView();
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
        if (MenuClip is not ClipItem item || Editor.Busy || !deleting.Add(item.Path)) return;
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
                ClipFiles.RemoveTitle(item.Path);
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
        if (sender is not C.Image { DataContext: ClipItem item } || item.PreviewTask is { IsCompleted: false } || item.Read || deleting.Contains(item.Path)) return;
        item.PreviewTask = LoadThumbnail(item);
    }
    private async Task LoadThumbnail(ClipItem item)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        item.PreviewCancellation = cancellation;
        try
        {
            var info = await MediaTools.Probe(item.Path, cancellation.Token);
            string thumbnail = await MediaTools.Thumbnail(item.Path, item.Stamp, cancellation.Token);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 320; image.UriSource = new Uri(thumbnail); image.EndInit(); image.Freeze();
            item.SetInfo(info, image);
        }
        catch (OperationCanceledException) { }
        catch { item.SetDetail("Preview unavailable · click to retry playback"); }
        finally { item.PreviewCancellation = null; }
    }
    public async Task OpenClip(string path)
    {
        if (Editor.Busy || deleting.Contains(path)) return;
        Dashboard.Visibility = Wpf.Visibility.Collapsed; Editor.Visibility = Wpf.Visibility.Visible;
        await Editor.Open(path, ClipFiles.DisplayApplication(ClipFiles.Application(path, folder)));
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
    private void Editor_Changed(object? sender, string e) => Refresh();
    private void Refresh_Click(object sender, Wpf.RoutedEventArgs e) => Refresh();
    private void Folder_Click(object sender, Wpf.RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(folder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); }
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
    public void Dispose() { disposed = true; ++generation; lifetime.Cancel(); watcher?.Dispose(); refreshTimer.Stop(); Editor.Dispose(); }

    private sealed class ClipItem : INotifyPropertyChanged
    {
        public string Path { get; }
        public ClipStamp Stamp { get; }
        public string Application { get; }
        public string Title { get; }
        public string Date => Stamp.Modified.ToLocalTime().ToString("MMM d, yyyy  ·  h:mm tt");
        public string Detail { get; private set; }
        public BitmapImage? Thumbnail { get; private set; }
        public Task? PreviewTask;
        public CancellationTokenSource? PreviewCancellation;
        public bool Read;
        public event PropertyChangedEventHandler? PropertyChanged;
        public ClipItem(string path, ClipStamp stamp, string application)
        {
            Path = path; Stamp = stamp; Application = application;
            Title = ClipFiles.Title(path, application);
            Detail = $"{stamp.Size / 1048576.0:0.#} MB  ·  Loading preview…";
        }
        public void SetInfo(ClipInfo info, BitmapImage image) { Read = true; Thumbnail = image; Detail = $"{MediaTools.Time(info.Duration)}  ·  {info.Width} × {info.Height}  ·  {info.Size / 1048576.0:0.#} MB"; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); PropertyChanged?.Invoke(this, new(nameof(Detail))); }
        public void SetDetail(string text) { Detail = text; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
    }
}
