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
}
