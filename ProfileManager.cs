using System.Text.Json;

public static class ProfileManager
{
    static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LethalModInstaller", "Profiles");

    // Ademas de plugins/, el perfil guarda los configs y los patchers que traen los mods
    // (BepInEx/config, BepInEx/patchers). Un perfil sin esa carpeta (creado antes) las deja como estan.
    static readonly string[] SnapshotDirs = { "patchers", "config" };

    static readonly string ActiveFile = Path.Combine(RootDir, "active_profile.txt");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<ProfileInfo> ListProfiles()
    {
        Directory.CreateDirectory(RootDir);
        var dirs = Directory.GetDirectories(RootDir);
        if (dirs.Length == 0)
        {
            CreateProfile("Default");
            dirs = Directory.GetDirectories(RootDir);
        }
        return dirs.Select(d => new ProfileInfo(Path.GetFileName(d), d)).ToList();
    }

    public static ProfileInfo CreateProfile(string name)
    {
        var safeName = SanitizeName(name);
        var dir = Path.Combine(RootDir, safeName);
        if (Directory.Exists(dir))
            throw new InvalidOperationException($"Ya existe un perfil llamado '{safeName}'.");

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        foreach (var d in SnapshotDirs) Directory.CreateDirectory(Path.Combine(dir, d)); // perfil nuevo = BepInEx limpio
        File.WriteAllText(Path.Combine(dir, "mods.json"), "[]");
        File.WriteAllText(Path.Combine(dir, "state.json"), "{}");
        return new ProfileInfo(safeName, dir);
    }

    public static void DeleteProfile(string name)
    {
        var profiles = ListProfiles();
        if (profiles.Count <= 1)
            throw new InvalidOperationException("No se puede borrar el último perfil.");

        var dir = Path.Combine(RootDir, SanitizeName(name));
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    public static List<string> GetProfileMods(string profileName)
    {
        var path = Path.Combine(RootDir, SanitizeName(profileName), "mods.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new();
    }

    public static void SetProfileMods(string profileName, List<string> idents)
    {
        var path = Path.Combine(RootDir, SanitizeName(profileName), "mods.json");
        File.WriteAllText(path, JsonSerializer.Serialize(idents, JsonOpts));
    }

    public static Dictionary<string, string> GetProfileState(string profileName)
    {
        var path = Path.Combine(RootDir, SanitizeName(profileName), "state.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
    }

    public static void SetProfileState(string profileName, Dictionary<string, string> state)
    {
        var path = Path.Combine(RootDir, SanitizeName(profileName), "state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
    }

    public static string GetProfilePluginsPath(string profileName)
    {
        var path = Path.Combine(RootDir, SanitizeName(profileName), "plugins");
        Directory.CreateDirectory(path);
        return path;
    }

    // Cambiar de perfil: guarda los mods actuales del juego en el perfil activo y
    // recien despues pone los del perfil destino. Sin el guardado previo, aplicar
    // un perfil vacio borraba los mods sin copia.
    public static void SwitchProfile(string targetProfile, string gameRoot)
    {
        var current = GetActiveProfileName();
        if (Directory.Exists(Path.Combine(RootDir, SanitizeName(current))))
            SaveGameToProfile(current, gameRoot);
        ApplyProfileToGame(targetProfile, gameRoot);
        SetActiveProfileName(targetProfile);
    }

    static void SaveGameToProfile(string profileName, string gameRoot)
    {
        var dest = GetProfilePluginsPath(profileName);
        ClearDir(dest);
        var pluginsSrc = Path.Combine(gameRoot, "BepInEx", "plugins");
        if (Directory.Exists(pluginsSrc))
            Installer.CopyDirRecursive(pluginsSrc, dest);
        SetProfileState(profileName, Installer.LoadState(gameRoot));

        var profileDir = Path.Combine(RootDir, SanitizeName(profileName));
        foreach (var d in SnapshotDirs)
        {
            var snap = Path.Combine(profileDir, d);
            Directory.CreateDirectory(snap);
            ClearDir(snap);
            var src = Path.Combine(gameRoot, "BepInEx", d);
            if (Directory.Exists(src)) Installer.CopyDirRecursive(src, snap);
        }
    }

    static void ApplyProfileToGame(string profileName, string gameRoot)
    {
        var pluginsDest = Path.Combine(gameRoot, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDest);
        ClearDir(pluginsDest);
        Installer.CopyDirRecursive(GetProfilePluginsPath(profileName), pluginsDest);

        var profileDir = Path.Combine(RootDir, SanitizeName(profileName));
        foreach (var d in SnapshotDirs)
        {
            var snap = Path.Combine(profileDir, d);
            if (!Directory.Exists(snap)) continue;
            var target = Path.Combine(gameRoot, "BepInEx", d);
            Directory.CreateDirectory(target);
            ClearDir(target);
            Installer.CopyDirRecursive(snap, target);
        }

        // BepInEx vive fuera de plugins/ y no cambia con el perfil: conservar su entrada.
        var state = GetProfileState(profileName);
        foreach (var kv in Installer.LoadState(gameRoot).Where(kv => kv.Key.Contains("BepInExPack")))
            state[kv.Key] = kv.Value;
        Installer.SaveState(gameRoot, state);
    }

    static void ClearDir(string dir)
    {
        foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
    }

    public static string GetActiveProfileName()
    {
        return File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() : "Default";
    }

    public static void SetActiveProfileName(string name)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(ActiveFile, SanitizeName(name));
    }

    static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // Trim('.') evita que ".." apunte fuera de Profiles (borrar perfil ".." borraba todo).
        var safe = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().Trim('.');
        return safe.Length > 0 ? safe : throw new InvalidOperationException("Nombre de perfil inválido.");
    }
}
