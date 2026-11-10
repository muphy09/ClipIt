using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipIt;

internal static class ClipFiles
{
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

    public static string FavoritesFolder(string library) => Path.Combine(library, "Favorites");

    public static bool IsFavorite(string path, string library) => IsInLibrary(path, FavoritesFolder(library));

    public static string GameName(string path, string library)
    {
        if (!IsFavorite(path, library)) return DisplayApplication(Application(path, library));
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
        string gameMarker = LabelPath(path, "game");
        Move(path, destination, renamed: true, game: File.Exists(gameMarker) ? File.ReadAllText(gameMarker) : null);
    }

    public static string MoveToFavorites(string path, string library, string game)
    {
        string folder = FavoritesFolder(library);
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, Path.GetFileName(path));
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int n = 2; Path.Exists(destination) || File.Exists(LabelPath(destination, "game")); n++)
            destination = Path.Combine(folder, $"{name} ({n}){extension}");
        Move(path, destination, renamed: File.Exists(LabelPath(path, "title")), game);
        return destination;
    }

    private static void Move(string path, string destination, bool renamed, string? game)
    {
        string titleMarker = LabelPath(destination, "title");
        string gameMarker = LabelPath(destination, "game");
        bool samePath = string.Equals(path, destination, StringComparison.OrdinalIgnoreCase);
        bool createdTitle = false, createdGame = false;
        try
        {
            if (renamed && !File.Exists(titleMarker))
            {
                using var file = File.Open(titleMarker, FileMode.CreateNew);
                createdTitle = true;
            }
            if (game != null && !samePath)
            {
                using var file = new StreamWriter(File.Open(gameMarker, FileMode.CreateNew));
                createdGame = true;
                file.Write(game);
            }
            File.Move(path, destination, overwrite: false);
        }
        catch
        {
            if (createdTitle) RemoveMarker(titleMarker);
            if (createdGame) RemoveMarker(gameMarker);
            throw;
        }
        if (!samePath) RemoveLabels(path);
    }

    public static void RemoveLabels(string path)
    {
        RemoveMarker(LabelPath(path, "title"));
        RemoveMarker(LabelPath(path, "game"));
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
}
