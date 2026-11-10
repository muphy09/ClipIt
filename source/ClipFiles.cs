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
        File.Exists(TitleMarker(path)) ? Path.GetFileNameWithoutExtension(path) : game;

    public static void Rename(string path, string destination)
    {
        string marker = TitleMarker(destination);
        bool markerExists = File.Exists(marker);
        if (!markerExists)
        {
            using var created = File.Open(marker, FileMode.CreateNew);
        }
        try { File.Move(path, destination, overwrite: false); }
        catch
        {
            if (!markerExists) RemoveTitle(destination);
            throw;
        }
        if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)) RemoveTitle(path);
    }

    public static void RemoveTitle(string path)
    {
        try { File.Delete(TitleMarker(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not remove custom clip title: " + ex.Message);
        }
    }

    // A companion marker keeps an explicit rename across restarts and MP4 replacement during trimming.
    private static string TitleMarker(string path)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant())));
        return Path.Combine(Path.GetDirectoryName(path)!, ".clipit-title-" + key);
    }
}
