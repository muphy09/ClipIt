using System.Diagnostics;
using System.Text.Json;

namespace ClipIt;

internal static class Smoke
{
    // Explicit diagnostic mode: records a short clip to an isolated test folder and exits.
    public static int Run(string[] args)
    {
        int duration = args.Length > 1 ? int.Parse(args[1]) : 12;
        string folder = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(AppContext.BaseDirectory, "smoke-clips");
        Directory.CreateDirectory(folder);
        string result = Path.Combine(folder, "result.json");
        var events = new List<WorkerMessage>();
        bool saved = false;
        var saveClock = new Stopwatch();
        double? saveMilliseconds = null;
        try
        {
            using var recorder = new Recorder(Settings.Load() with { Folder = folder }, m =>
            {
                events.Add(m);
                if (m.Type == "saved") { saved = true; saveMilliseconds = saveClock.Elapsed.TotalMilliseconds; }
            });
            recorder.Start();
            var clock = Stopwatch.StartNew();
            var tick = Stopwatch.StartNew();
            bool requested = false;
            while (clock.Elapsed.TotalSeconds < duration + 30 && !saved)
            {
                if (!requested && clock.Elapsed.TotalSeconds >= duration) { saveClock.Start(); recorder.Save(); requested = true; }
                recorder.PollSaves();
                if (tick.ElapsedMilliseconds >= 1000) { recorder.Tick(); tick.Restart(); }
                Thread.Sleep(50);
            }
            File.WriteAllText(result, JsonSerializer.Serialize(new { Success = saved, SaveMilliseconds = saveMilliseconds, Events = events }, new JsonSerializerOptions { WriteIndented = true }));
            return saved ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write("Smoke failed: " + ex);
            File.WriteAllText(result, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString(), Events = events }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
