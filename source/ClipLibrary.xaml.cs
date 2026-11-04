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
                if (!items.TryGetValue(f.FullName, out var item) || item.Stamp != stamp) item = new ClipItem(f.FullName, stamp, string.Equals(f.DirectoryName, directory, StringComparison.OrdinalIgnoreCase) ? "Unsorted" : f.Directory!.Name);
                next.Add(item);
            }
            clips.Clear(); items.Clear();
            foreach (var item in next) { items[item.Path] = item; clips.Add(item); }
            SummaryLabel.Text = $"{files.Length} {(files.Length == 1 ? "clip" : "clips")}  ·  {files.Sum(f => f.Length) / 1048576.0:0.#} MB  ·  Newest first";
            EmptyState.Visibility = clips.Count == 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LibraryMessage.Text = "Could not load clips: " + ex.Message; }
    }
    private async void Thumbnail_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        if (sender is not C.Image { DataContext: ClipItem item } || item.Loading || item.Read) return;
        item.Loading = true;
        try
        {
            var info = await MediaTools.Probe(item.Path, lifetime.Token);
            string thumbnail = await MediaTools.Thumbnail(item.Path, item.Stamp, lifetime.Token);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 320; image.UriSource = new Uri(thumbnail); image.EndInit(); image.Freeze();
            item.SetInfo(info, image);
        }
        catch (OperationCanceledException) { }
        catch { item.SetDetail("Preview unavailable · click to retry playback"); }
        finally { item.Loading = false; }
    }
    public async Task OpenClip(string path)
    {
        if (Editor.Busy) return;
        Dashboard.Visibility = Wpf.Visibility.Collapsed; Editor.Visibility = Wpf.Visibility.Visible;
        await Editor.Open(path);
    }
    public void ShowDashboard()
    {
        if (Editor.Busy) return;
        Editor.CloseMedia(); Editor.Visibility = Wpf.Visibility.Collapsed;
        Dashboard.Visibility = Wpf.Visibility.Visible; Refresh();
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
        e.Effects = e.Data.GetDataPresent(Wpf.DataFormats.FileDrop) && !Editor.Busy ? Wpf.DragDropEffects.Copy : Wpf.DragDropEffects.None;
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
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
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
    public void PausePlayback() => Editor.Suspend();
    public void Dispose() { disposed = true; ++generation; lifetime.Cancel(); watcher?.Dispose(); refreshTimer.Stop(); Editor.Dispose(); }

    private sealed class ClipItem : INotifyPropertyChanged
    {
        public string Path { get; }
        public ClipStamp Stamp { get; }
        public string Application { get; }
        public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Date => Application + "  ·  " + Stamp.Modified.ToLocalTime().ToString("MMM d, yyyy  ·  h:mm tt");
        public string Detail { get; private set; }
        public BitmapImage? Thumbnail { get; private set; }
        public bool Loading, Read;
        public event PropertyChangedEventHandler? PropertyChanged;
        public ClipItem(string path, ClipStamp stamp, string application) { Path = path; Stamp = stamp; Application = application; Detail = $"{stamp.Size / 1048576.0:0.#} MB  ·  Loading preview…"; }
        public void SetInfo(ClipInfo info, BitmapImage image) { Read = true; Thumbnail = image; Detail = $"{MediaTools.Time(info.Duration)}  ·  {info.Width} × {info.Height}  ·  {info.Size / 1048576.0:0.#} MB"; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); PropertyChanged?.Invoke(this, new(nameof(Detail))); }
        public void SetDetail(string text) { Detail = text; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
    }
}
