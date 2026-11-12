using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipIt;

internal static class ClipFiles
{
    internal sealed record PlaybackSettings(ClipStamp Stamp, MixSettings Mix, double Master, bool MasterEnabled = true);

    private static string PlaybackPath(string path) => Path.Combine(Settings.DataDirectory, "playback", Path.GetFileName(FavoritePath(path)));

    internal static PlaybackSettings? ReadPlayback(string path)
    {
        try
        {
            string marker = PlaybackPath(path);
            if (!File.Exists(marker)) return null;
            var saved = JsonSerializer.Deserialize<PlaybackSettings>(File.ReadAllText(marker));
            return saved != null && saved.Stamp == ClipStamp.Read(path) ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Log.Write("Could not read playback settings: " + ex.Message); return null; }
    }

    internal static void SavePlayback(string path, ClipStamp stamp, MixSettings mix, double master, bool masterEnabled = true)
    {
        string marker = PlaybackPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        string temporary = marker + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new PlaybackSettings(stamp, mix, master, masterEnabled)));
            File.Move(temporary, marker, overwrite: true);
        }
        finally { RemoveMarker(temporary); }
    }

    public static string SaveName(string application) => $"{AppTargets.FolderName(application)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid().ToString("N")[..6]}";

    public static bool IsInLibrary(string path, string library)
    {
        string root = Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static string Application(string path, string library)
    {
        string relative = Path.GetRelativePath(library, path);
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        // Imported clips at the root have no captured-game metadata.
        return separator < 0 ? "Unsorted" : relative[..separator];
    }

    private static bool IsLegacyFavorite(string path, string library) => IsInLibrary(path, Path.Combine(library, "Favorites"));

    public static string PortalGameName(string path, string portal)
    {
        string relative = Path.GetRelativePath(portal, path);
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? new DirectoryInfo(portal).Name : relative[..separator];
    }

    public static bool IsFavorite(string path, string library)
    {
        string marker = FavoritePath(path);
        try
        {
            if (File.Exists(marker) && bool.TryParse(File.ReadAllText(marker), out bool marked)) return marked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not read clip's favorite status: " + ex.Message);
        }
        return IsLegacyFavorite(path, library);
    }

    public static void SetFavorite(string path, bool favorite)
    {
        string marker = FavoritePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        string temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, favorite.ToString());
            File.Move(temporary, marker, overwrite: true);
        }
        finally { RemoveMarker(temporary); }
    }

    public static string GameName(string path, string library)
    {
        if (!IsLegacyFavorite(path, library)) return DisplayApplication(Application(path, library));
        try
        {
            string marker = LabelPath(path, "game");
            return File.Exists(marker) ? File.ReadAllText(marker) : "Unsorted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not read favorite clip's game: " + ex.Message);
            return "Unsorted";
        }
    }

    public static string DisplayApplication(string application)
    {
        string name = application.Replace('_', ' ').Trim();
        string key = name.Replace(" ", "").Replace("-", "").ToLowerInvariant();
        return key switch
        {
            "rocketleague" => "Rocket League",
            "leagueoflegends" => "League of Legends",
            "cs2" => "Counter-Strike 2",
            "gta5" or "gtav" => "Grand Theft Auto V",
            "gta5enhanced" => "Grand Theft Auto V Enhanced",
            "fortniteclientwin64shipping" => "Fortnite",
            "valorantwin64shipping" => "VALORANT",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " "))
        };
    }

    public static string Title(string path, string game) =>
        File.Exists(LabelPath(path, "title")) ? Path.GetFileNameWithoutExtension(path) : game;

    public static void Rename(string path, string destination)
    {
        string oldGameMarker = LabelPath(path, "game");
        string oldFavoriteMarker = FavoritePath(path);
        string titleMarker = LabelPath(destination, "title");
        string gameMarker = LabelPath(destination, "game");
        string favoriteMarker = FavoritePath(destination);
        bool samePath = string.Equals(path, destination, StringComparison.OrdinalIgnoreCase);
        bool createdTitle = false, createdGame = false, createdFavorite = false, createdPlayback = false;
        try
        {
            if (!File.Exists(titleMarker))
            {
                using var file = File.Open(titleMarker, FileMode.CreateNew);
                createdTitle = true;
            }
            if (!samePath && File.Exists(oldGameMarker))
            {
                File.Copy(oldGameMarker, gameMarker);
                createdGame = true;
            }
            if (!samePath && File.Exists(oldFavoriteMarker))
            {
                File.Copy(oldFavoriteMarker, favoriteMarker);
                createdFavorite = true;
            }
            if (!samePath && File.Exists(PlaybackPath(path)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlaybackPath(destination))!);
                File.Copy(PlaybackPath(path), PlaybackPath(destination));
                createdPlayback = true;
            }
            File.Move(path, destination, overwrite: false);
        }
        catch
        {
            if (createdTitle) RemoveMarker(titleMarker);
            if (createdGame) RemoveMarker(gameMarker);
            if (createdFavorite) RemoveMarker(favoriteMarker);
            if (createdPlayback) RemoveMarker(PlaybackPath(destination));
            throw;
        }
        if (!samePath) RemoveLabels(path);
    }

    public static void RemoveLabels(string path)
    {
        RemoveMarker(LabelPath(path, "title"));
        RemoveMarker(LabelPath(path, "game"));
        RemoveMarker(FavoritePath(path));
        RemoveMarker(PlaybackPath(path));
    }

    private static void RemoveMarker(string marker)
    {
        try { File.Delete(marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not remove clip metadata: " + ex.Message);
        }
    }

    // Companion markers preserve labels across restarts and MP4 replacement during trimming.
    private static string LabelPath(string path, string kind)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant())));
        return Path.Combine(Path.GetDirectoryName(path)!, ".clipit-" + kind + "-" + key);
    }

    // Favorites live in app data so linked clips can be marked on read-only drives.
    private static string FavoritePath(string path)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        return Path.Combine(Settings.DataDirectory, "favorites", key);
    }
}
