using System.IO;
using System.Text.Json;

public static class ModToggle
{
    public static List<InstalledModInfo> ListInstalledMods(string gameRoot)
    {
        var result = new List<InstalledModInfo>();

        if (string.IsNullOrWhiteSpace(gameRoot))
            return result;

        var pluginsDir = Path.Combine(gameRoot, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir))
            return result;

        var statePath = Path.Combine(gameRoot, "modinstaller_state.json");
        Dictionary<string, string>? state = null;
        if (File.Exists(statePath))
        {
            try
            {
                state = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(statePath));
            }
            catch
            {
                state = null;
            }
        }

        foreach (var folder in Directory.GetDirectories(pluginsDir))
        {
            var key = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(key) || !key.Contains('-'))
                continue;

            string version = "?";
            if (state != null)
            {
                if (state.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    version = v;
                }
                else
                {
                    var match = state.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (match.Key != null && !string.IsNullOrWhiteSpace(match.Value))
                    {
                        version = match.Value;
                    }
                }
            }

            // Desactivado = algun archivo renombrado a *.disabled. No se mira solo .dll:
            // los packs de trajes (solo .png) y assetbundles tambien se desactivan asi.
            bool enabled = !GetPluginFiles(folder).Any(f => f.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase));
            string pluginPath = Path.GetFullPath(folder);

            result.Add(new InstalledModInfo(key, version, enabled, pluginPath));
        }

        return result;
    }

    const string Suffix = ".disabled";

    // Renombra TODOS los archivos del mod (recursivo), igual que r2modman: BepInEx no
    // carga un .dll.disabled y MoreSuits no lee un .png.disabled.
    public static void DisableMod(string pluginPath)
    {
        foreach (var file in GetPluginFiles(pluginPath))
            if (!file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                File.Move(file, file + Suffix);
    }

    public static void EnableMod(string pluginPath)
    {
        foreach (var file in GetPluginFiles(pluginPath))
            if (file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                File.Move(file, file[..^Suffix.Length], overwrite: true);
    }

    public static void UninstallMod(string pluginPath)
    {
        Directory.Delete(pluginPath, true);
    }

    // Recursivo: hay mods con la .dll varios niveles abajo (plugins/<Mod>/<Mod>.dll).
    private static string[] GetPluginFiles(string pluginPath) =>
        Directory.Exists(pluginPath) ? Directory.GetFiles(pluginPath, "*", SearchOption.AllDirectories) : Array.Empty<string>();
}
