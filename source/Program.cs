namespace ClipIt;

internal static class Program
{
    internal static EventWaitHandle? ShowRequested;
    [STAThread]
    private static int Main(string[] args)
    {
        // Explicit startup application happens only after the frontend's single-instance gate.
        // Recording workers and second launches must never apply a downloaded update.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        ApplicationConfiguration.Initialize();
        if (args.Length == 3 && args[0] == "--worker") return Recorder.RunWorker(args[1], args[2]);
        if (args.Length > 0 && args[0] == "--smoke") return Smoke.Run(args);
        if (args.Length == 2 && args[0] == "--media-checks") return MediaChecks.Run(args[1]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--list-apps") { File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(AppTargets.List())); return 0; }
        string instance = Environment.UserName;
        if (Environment.GetEnvironmentVariable("CLIPIT_DATA") is { Length: > 0 } isolated)
            instance += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(isolated))))[..12];
        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClipIt.Show-" + instance);
        ShowRequested = showRequested;
        using var mutex = new Mutex(true, "Local\\ClipIt-" + instance, out bool first);
        if (!first)
        {
            if (Environment.GetEnvironmentVariable("CLIPIT_DATA") == null)
            {
                string current = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "0.4.0";
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("ClipIt"))
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId) continue;
                        string existing = System.Diagnostics.FileVersionInfo.GetVersionInfo(process.MainModule!.FileName).ProductVersion ?? "unknown";
                        if (existing == current) continue;
                        MessageBox.Show($"ClipIt {existing} is already running. Quit that version from its system tray menu, then launch ClipIt {current}. This prevents the older app from intercepting the new launch.", "Start the new ClipIt version", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            showRequested.Set(); return 0;
        }
        if (AppUpdates.ApplyPendingOnStartup()) return 0;
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);
        try
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) => { Log.Write("UI error: " + e.Exception); e.Handled = true; };
            app.Run(new ClipWindow());
            return 0;
        }
        catch (Exception ex) { Log.Write(ex.ToString()); MessageBox.Show(ex.Message, "ClipIt could not start"); return 1; }
    }
}
