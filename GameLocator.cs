using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

public static class GameLocator
{
    const string AppId = "1966720";
    const string ExeName = "Lethal Company.exe";
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LethalModInstaller", "settings.json");

    // Recorre todas las librerias de Steam (libraryfolders.vdf). Prefiere una carpeta
    // con el .exe sobre una que solo existe (restos de mods sin el juego).
    public static string? Detect()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam)
            return null;

        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        string? fallback = null;
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.GetFullPath(Path.Combine(lib, "steamapps", "common", "Lethal Company"));
            if (HasExe(dir)) return dir;
            if (fallback == null && Directory.Exists(dir)) fallback = dir;
        }
        return fallback;
    }

    public static bool HasExe(string gameRoot) => File.Exists(Path.Combine(gameRoot, ExeName));

    // Via Steam para que el juego arranque con su overlay/auth. Vanilla = desactivar
    // doorstop (el loader de BepInEx) solo para esta ejecucion, sin tocar archivos.
    public static void Launch(bool vanilla) => Process.Start(new ProcessStartInfo(vanilla
        ? $"steam://run/{AppId}//--doorstop-enabled%20false/"
        : $"steam://rungameid/{AppId}") { UseShellExecute = true });

    public static string? LoadSavedRoot()
    {
        try { return JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("gameRoot").GetString(); }
        catch { return null; } // sin settings o corrupto: se re-detecta
    }

    public static void SaveRoot(string gameRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { gameRoot }));
    }
}
