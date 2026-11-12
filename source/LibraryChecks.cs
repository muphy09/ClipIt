using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using C = System.Windows.Controls;
using Wpf = System.Windows;

namespace ClipIt;

internal static class LibraryChecks
{
    internal static int Run(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        string directory = Path.Combine(root, "library-" + Guid.NewGuid().ToString("N"));
        string libraryFolder = Path.Combine(directory, "clips");
        Directory.CreateDirectory(libraryFolder);
        Environment.SetEnvironmentVariable("CLIPIT_DATA", Path.Combine(directory, "data"));
        var results = new List<string>(); int exit = 1;
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        void Require(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); }
        app.Startup += async (_, _) =>
        {
            ClipLibrary? library = null; Wpf.Window? window = null;
            var clipboard = Wpf.Clipboard.GetDataObject(); bool copied = false;
            try
            {
                string fixture = Path.Combine(directory, "fixture.mp4");
                var generated = await MediaTools.Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1", "-c:v", "libx264", "-threads", "2", "-y", fixture], CancellationToken.None);
                Require(generated.Exit == 0, "Created disposable playable test clip");
                string Add(string group, string name, int age)
                {
                    string folder = Path.Combine(libraryFolder, group); Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, name + ".mp4"); File.Copy(fixture, file);
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-age)); return file;
                }
                string old = Add("zebra", "ClipIt old", 10), desktop = Add("Desktop", "ClipIt desktop", 8);
                string newer = Add("zebra", "zebra new", 1), alpha = Add("Alpha", "Alpha", 4);
                Add("", "Imported", 3); Add(Path.Combine("Alpha", "Exports"), "Edited", 2);
                library = new ClipLibrary();
                object? Invoke(string name, params object[] args) => typeof(ClipLibrary).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(library, args);
                async Task Reload() => await (Task)Invoke("Reload")!;
                static string PathOf(object item) => (string)item.GetType().GetProperty("Path")!.GetValue(item)!;
                library.SetFolder(libraryFolder); await Reload();
                var clips = (C.ListBox)library.FindName("ClipsList");
                var games = (C.ListBox)library.FindName("GamesList");
                Require(clips.Items.Count == 6 && PathOf(clips.Items[0]) == newer, "Recent includes all folders with newest clip first");
                ((C.RadioButton)library.FindName("ByGameTab")).IsChecked = true;
                string[] names = games.Items.Cast<object>().Select(g => (string)g.GetType().GetProperty("Name")!.GetValue(g)!).ToArray();
                Require(names.SequenceEqual(new[] { "Alpha", "Desktop", "Unsorted", "Zebra" }), "Game folders sort alphabetically, including Desktop and unknown imports");
                object zebra = games.Items[3];
                long zebraSize = new FileInfo(newer).Length + new FileInfo(old).Length;
                string zebraDetail = (string)zebra.GetType().GetProperty("Detail")!.GetValue(zebra)!;
                Require(zebraDetail == $"2 clips ({zebraSize / 1024.0:0.##} KB)", "By Game shows the combined size of its clips");
                Require(ClipFiles.Application(Path.Combine(libraryFolder, "Alpha", "Exports", "Edited.mp4"), libraryFolder) == "Alpha", "Nested exports retain their top-level game");
                games.SelectedIndex = 3;
                Invoke("OpenGame", "zebra");
                Require(clips.Items.Count == 2 && PathOf(clips.Items[0]) == newer && PathOf(clips.Items[1]) == old, "Opening a game shows only that game's clips, newest first");
                Invoke("Games_Back", library, new Wpf.RoutedEventArgs());
                Require(games.Visibility == Wpf.Visibility.Visible, "All games returns to the folder list");
                Require(ClipFiles.IsInLibrary(newer, libraryFolder) && !ClipFiles.IsInLibrary(Path.Combine(directory, "clips-other", "file.mp4"), libraryFolder), "Library membership includes descendants without matching sibling prefixes");
                await (Task)Invoke("Import", (object)new string[] { old, desktop, newer, alpha })!;
                Require(Directory.GetFiles(libraryFolder, "*.mp4", SearchOption.AllDirectories).Length == 6, "Dropping existing clips from game folders makes no duplicates");
                await (Task)Invoke("Import", (object)new string[] { fixture })!;
                Require(File.Exists(fixture) && File.Exists(Path.Combine(libraryFolder, "fixture.mp4")), "External import copies the clip and preserves its original");
                Require(!(bool)Invoke("CanImport", newer)! && (bool)Invoke("CanImport", fixture)!, "Drop eligibility rejects library clips and accepts external clips");
                ((C.RadioButton)library.FindName("RecentTab")).IsChecked = true;
                clips.SelectedItem = clips.Items.Cast<object>().First(i => PathOf(i) == newer);
                var menu = clips.ContextMenu;
                menu.RaiseEvent(new Wpf.RoutedEventArgs(C.ContextMenu.OpenedEvent));
                Invoke("Copy_Click", library, new Wpf.RoutedEventArgs()); copied = true;
                Require(Wpf.Clipboard.GetFileDropList().Cast<string>().SequenceEqual(new[] { newer }), "Copy places the selected file on the clipboard for pasting");
                Require(menu.Items.Cast<C.MenuItem>().Select(i => (string)i.Header).SequenceEqual(new[] { "Copy", "Delete" }) && menu.Style != null, "Clip menu offers themed Copy and Delete actions");
                window = new Wpf.Window { Title = "ClipIt library checks", Content = library, Width = 1000, Height = 720, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 16, 25)), ShowInTaskbar = false };
                window.Show();
                await Reload();
                object deletingClip = clips.Items.Cast<object>().First(i => PathOf(i) == newer);
                object otherClip = clips.Items.Cast<object>().First(i => PathOf(i) == old);
                var previewTask = deletingClip.GetType().GetField("PreviewTask")!;
                if (previewTask.GetValue(deletingClip) is Task runningPreview) await runningPreview;
                var finishPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                previewTask.SetValue(deletingClip, finishPreview.Task);
                bool HasDeleteAppearance(object clip, bool pending)
                {
                    clips.ScrollIntoView(clip);
                    window.UpdateLayout();
                    var card = (C.ListBoxItem)clips.ItemContainerGenerator.ContainerFromItem(clip);
                    var status = (Wpf.FrameworkElement)card.Template.FindName("DeleteStatus", card);
                    var content = (Wpf.FrameworkElement)card.Template.FindName("CardContent", card);
                    var spinner = (Wpf.FrameworkElement)card.Template.FindName("DeleteSpinner", card);
                    return status.IsVisible == pending && (content.Opacity < 1) == pending
                        && spinner.RenderTransform.HasAnimatedProperties == pending
                        && (Wpf.Automation.AutomationProperties.GetItemStatus(card) == "Deleting") == pending;
                }
                clips.SelectedItem = deletingClip;
                menu.RaiseEvent(new Wpf.RoutedEventArgs(C.ContextMenu.OpenedEvent));
                try
                {
                    Invoke("Delete_Click", library, new Wpf.RoutedEventArgs());
                    Require(File.Exists(newer) && HasDeleteAppearance(deletingClip, true) && HasDeleteAppearance(otherClip, false),
                        "Delete immediately dims only the pending list card and shows its status");
                    ((C.RadioButton)library.FindName("TileViewButton")).IsChecked = true;
                    await Reload();
                    Require(HasDeleteAppearance(deletingClip, true) && HasDeleteAppearance(otherClip, false),
                        "The pending delete indicator survives a refresh and follows the clip into tiles");
                }
                finally { finishPreview.SetResult(); }
                for (int i = 0; i < 100 && (File.Exists(newer) || clips.Items.Cast<object>().Any(c => PathOf(c) == newer)); i++) await Task.Delay(50);
                Require(!File.Exists(newer) && File.Exists(old) && !clips.Items.Cast<object>().Any(c => PathOf(c) == newer), "Delete removes only the selected disposable clip and refreshes the list");
                Require(ClipFiles.SaveName("Desktop").StartsWith("Desktop " ) && ClipFiles.SaveName("Alpha").StartsWith("Alpha ") && ClipFiles.SaveName("Alpha") != ClipFiles.SaveName("Alpha"), "Save names begin with Desktop or the game and remain unique");
                ((C.RadioButton)library.FindName("ListViewButton")).IsChecked = true;
                await Task.Delay(1000);
                void Snapshot(string name)
                {
                    window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(root, name + ".png")); encoder.Save(stream);
                }
                Snapshot("recent");
                ((C.RadioButton)library.FindName("ByGameTab")).IsChecked = true;
                await Task.Delay(100); Snapshot("by-game");
                exit = 0;
            }
            catch (Exception ex) { results.Add("FAILED: " + ex); }
            finally
            {
                if (copied) { if (clipboard != null) Wpf.Clipboard.SetDataObject(clipboard); else Wpf.Clipboard.Clear(); }
                library?.Dispose(); window?.Close(); app.Shutdown();
            }
        };
        app.Run();
        File.WriteAllText(Path.Combine(root, "library-check-results.json"), JsonSerializer.Serialize(new { Success = exit == 0, Checks = results }, new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }
}
