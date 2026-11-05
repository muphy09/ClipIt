using Microsoft.Win32;
using Velopack.Locators;

namespace ClipIt;

internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "ClipIt";
    // Disposable developer builds must not register themselves at sign-in or replace the installed app's entry.
    internal static bool Available => Environment.GetEnvironmentVariable("CLIPIT_DATA") == null;

    internal static string Command(string executable) => $"\"{Path.GetFullPath(executable)}\" --windows-startup";

    internal static string Executable()
    {
        var locator = VelopackLocator.Current;
        if (locator.CurrentlyInstalledVersion != null && !locator.IsPortable && locator.RootAppDir is { } root)
        {
            // Use the stable launcher outside 'current', which survives replacement during updates.
            string launcher = Path.Combine(root, "ClipIt.exe");
            if (File.Exists(launcher)) return launcher;
        }
        return Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate ClipIt.");
    }

    internal static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command == Command(Executable());
        }
    }

    internal static void Apply(bool enabled)
    {
        if (!Available) throw new InvalidOperationException("Windows startup is available in the installed app. Development instances stay separate.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        WriteEntry(key, ValueName, enabled, Executable());
    }

    internal static void WriteEntry(RegistryKey key, string name, bool enabled, string executable)
    {
        if (enabled) key.SetValue(name, Command(executable), RegistryValueKind.String);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }
}
