using System.Text.Json;

namespace ClipIt;

public sealed record Settings
{
    public string Folder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipIt");
    public string Monitor { get; init; } = "";
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public int Bitrate { get; init; } = 24000;
    public int ReplaySeconds { get; init; } = 120;
    internal int ReplayBufferSeconds => ReplaySeconds + 2; // Allow for two-second keyframes.
    internal static long EstimateClipBytes(double videoMbps, int seconds) =>
        (long)Math.Ceiling((videoMbps + .192 * 4) * 1_000_000 / 8 * seconds * 1.01); // Four AAC tracks and container overhead.
    public bool Microphone { get; init; }
    public string MicrophoneDevice { get; init; } = "default";
    public string GameWindow { get; init; } = "auto";
    public string DiscordExecutable { get; init; } = "Discord.exe";
    public bool Cursor { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool Overlay { get; init; } = true;
    public bool BufferInMemory { get; init; }
    public uint Modifiers { get; init; } = 1; // Alt
    public uint Key { get; init; } = (uint)Keys.B;
    public uint LibraryModifiers { get; init; } = 5; // Alt + Shift
    public uint LibraryKey { get; init; } = (uint)Keys.B;
    public static string DataDirectory => Environment.GetEnvironmentVariable("CLIPIT_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipIt");
    public static string FileName => Path.Combine(DataDirectory, "settings.json");
    public static Settings Load()
    {
        if (!File.Exists(FileName) && Environment.GetEnvironmentVariable("CLIPIT_DATA") == null)
        {
            // Preserve preferences from the earlier preview without touching its recordings.
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayTray", "settings.json");
            if (File.Exists(legacy))
            {
                try
                {
                    var migrated = JsonSerializer.Deserialize<Settings>(File.ReadAllText(legacy))!.Validated();
                    if (migrated.Folder == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReplayTray"))
                        migrated = migrated with { Folder = new Settings().Folder };
                    migrated.Save();
                    return migrated;
                }
                catch (Exception ex) { Log.Write("Could not migrate preview preferences: " + ex.Message); }
            }
        }
        try { return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(FileName)) ?? new()).Validated(); }
        catch { return new(); }
    }
    public Settings Validated()
    {
        if (!Path.IsPathFullyQualified(Folder)) throw new ArgumentException("Choose an absolute save folder.");
        if (Height < 2 || Height > 32768 || Height % 2 != 0 || Fps is not (30 or 60) || Bitrate < 5000 || Bitrate > 100000)
            throw new ArgumentException("Invalid recording quality settings.");
        if (Key < 1 || Key > 254 || Modifiers > 15 || (Modifiers & 3) == 0)
            throw new ArgumentException("The shortcut must include Ctrl or Alt.");
        if (LibraryKey < 1 || LibraryKey > 254 || LibraryModifiers > 15 || (LibraryModifiers & 3) == 0)
            throw new ArgumentException("The Your Clips shortcut must include Ctrl or Alt.");
        if (ReplaySeconds < 30 || ReplaySeconds > 300 || ReplaySeconds % 30 != 0)
            throw new ArgumentException("Replay length must be 30 seconds to 5 minutes, in 30-second steps.");
        return this;
    }
    public void Save()
    {
        Validated();
        Directory.CreateDirectory(DataDirectory);
        var temp = FileName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FileName, true);
    }
    public string Shortcut => $"{((Modifiers & 2) != 0 ? "Ctrl+" : "")}{((Modifiers & 1) != 0 ? "Alt+" : "")}{((Modifiers & 4) != 0 ? "Shift+" : "")}{((Modifiers & 8) != 0 ? "Win+" : "")}{(Keys)Key}";
    public string LibraryShortcut => (this with { Modifiers = LibraryModifiers, Key = LibraryKey }).Shortcut;
}

internal static class Log
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDirectory);
                var file = Path.Combine(Settings.DataDirectory, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 4_000_000) File.Move(file, file + ".old", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

public sealed record WorkerMessage(string Type, string Text = "", int Seconds = 0, int Frames = 0, string Path = "");
