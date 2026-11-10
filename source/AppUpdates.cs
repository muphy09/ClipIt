using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace ClipIt;

internal sealed class AppUpdates : IDisposable
{
    internal const string Repository = "https://github.com/muphy09/ClipIt";
    internal static string Version => typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    private readonly UpdateManager manager = CreateManager();
    private readonly CancellationTokenSource lifetime = new();
    private bool checking;
    internal string Text { get; private set; } = "Checking for Updates…";
    internal bool CanApply { get; private set; }
    internal bool CanRetry { get; private set; }
    internal bool CanCheck { get; private set; }
    internal event Action? Changed;

    private static UpdateManager CreateManager()
    {
        // A local feed is available only to explicitly isolated developer/test instances.
        if (Environment.GetEnvironmentVariable("CLIPIT_DATA") is { Length: > 0 } && Environment.GetEnvironmentVariable("CLIPIT_UPDATE_FEED") is { Length: > 0 } feed)
            return new UpdateManager(feed, new UpdateOptions { ExplicitChannel = "win" });
        return new UpdateManager(new GithubSource(Repository, null, false), new UpdateOptions { ExplicitChannel = "win" });
    }

    internal static bool ApplyPendingOnStartup(bool windowsStartup = false)
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending) return false;
            Log.Write("Applying downloaded update on startup: " + pending.Version);
            manager.WaitExitThenApplyUpdates(pending, silent: windowsStartup, restart: true, restartArgs: windowsStartup ? ["--windows-startup"] : []);
            return true;
        }
        catch (Exception ex) { Log.Write("Startup update could not apply; continuing with current app: " + ex); return false; }
    }

    internal async Task Check()
    {
        if (checking || lifetime.IsCancellationRequested) return;
        checking = true;
        try
        {
            if (!manager.IsInstalled) { Set("Development build", retry: false); return; }
            if (manager.UpdatePendingRestart != null) { Set("Apply Update", apply: true); return; }
            Set("Checking for Updates…");
            var update = await manager.CheckForUpdatesAsync();
            if (lifetime.IsCancellationRequested) return;
            if (update == null) { Set("Up to Date!", check: true); return; }
            Set("Update found: Downloading");
            await manager.DownloadUpdatesAsync(update, percent => Set($"Update found: Downloading {Math.Clamp(percent, 0, 100)}%"), lifetime.Token);
            if (!lifetime.IsCancellationRequested) Set("Apply Update", apply: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Write("Update check/download failed: " + ex); if (!lifetime.IsCancellationRequested) Set("Update unavailable · Retry", retry: true); }
        finally { checking = false; }
    }

    // Launch the updater first, then let the window shut down its recorder/editor gracefully.
    internal void ScheduleApply()
    {
        if (!CanApply || manager.UpdatePendingRestart is not { } pending) throw new InvalidOperationException("No downloaded update is ready.");
        manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
        Set("Applying update…");
    }

    private void Set(string text, bool apply = false, bool retry = false, bool check = false)
    {
        Text = text; CanApply = apply; CanRetry = retry; CanCheck = check;
        Log.Write("Update: " + text);
        if (Environment.GetEnvironmentVariable("CLIPIT_DATA") is { Length: > 0 })
            try { Directory.CreateDirectory(Settings.DataDirectory); File.WriteAllText(Path.Combine(Settings.DataDirectory, "update-status.json"), JsonSerializer.Serialize(new { Version, Text, CanApply, CanRetry })); } catch { }
        Changed?.Invoke();
    }
    public void Dispose() { lifetime.Cancel(); }
}
