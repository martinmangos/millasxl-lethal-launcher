using System.Text.Json;
using System.Text.RegularExpressions;

// Modpack = un .json con TODO lo necesario para dejar a otro igual: cada mod con su version exacta y si esta
// activo, el contenido de BepInEx/config, y algunos archivos de ajustes de mods. No lleva binarios: los
// mods se vuelven a bajar de Thunderstore.
public record PackMod(string Key, string Version, bool Enabled);

public class Pack
{
    public string Format { get; set; } = ModpackService.FormatId;
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Created { get; set; } = "";
    public string? BepInEx { get; set; }
    public List<PackMod> Mods { get; set; } = new();
    public Dictionary<string, string> Configs { get; set; } = new();
    public Dictionary<string, string> Files { get; set; } = new();
}

public static class ModpackService
{
    public const string FormatId = "lethal-launcher-modpack";
    const string BepInExKey = "BepInEx-BepInExPack";
    // Archivos fuera de BepInEx/config que tambien se llevan. Lista cerrada: el pack es de terceros y
    // no puede escribir en cualquier ruta.
    static readonly string[] ExtraFiles = { "Mirage/settings.json" };
    static readonly Regex KeyRx = new(@"^[A-Za-z0-9_]+-[A-Za-z0-9_]+$");
    static readonly Regex VerRx = new(@"^\d+\.\d+\.\d+$");
    const int MaxMods = 600, MaxConfigs = 400;
    const long MaxTotalBytes = 30_000_000;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Pack Build(string gameRoot, string name, out List<string> skipped)
    {
        skipped = new();
        var state = Installer.LoadState(gameRoot);
        var pack = new Pack { Name = name.Trim(), Created = DateTime.UtcNow.ToString("o"), BepInEx = state.GetValueOrDefault(BepInExKey) };

        foreach (var m in ModToggle.ListInstalledMods(gameRoot).OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!KeyRx.IsMatch(m.Key) || !VerRx.IsMatch(m.Version)) { skipped.Add(m.Key); continue; } // sin version no se puede fijar
            pack.Mods.Add(new PackMod(m.Key, m.Version, m.Enabled));
        }

        foreach (var f in ConfigEditor.List(gameRoot))
            pack.Configs[f.File] = File.ReadAllText(ConfigEditor.Resolve(gameRoot, f.File));

        foreach (var rel in ExtraFiles)
        {
            var path = Path.Combine(gameRoot, rel);
            if (File.Exists(path) && new FileInfo(path).Length <= ConfigEditor.MaxFileBytes)
                pack.Files[rel] = File.ReadAllText(path);
        }
        return pack;
    }

    public static string ToJson(Pack p) => JsonSerializer.Serialize(p, Json);

    // El archivo viene de otra persona: se valida todo antes de usar cualquier dato.
    public static Pack Parse(string text)
    {
        Pack? p;
        try { p = JsonSerializer.Deserialize<Pack>(text, Json); }
        catch (JsonException) { throw new InvalidOperationException("Ese archivo no es un modpack válido (JSON ilegible)."); }
        if (p == null || p.Format != FormatId || p.Version != 1)
            throw new InvalidOperationException("Ese archivo no es un modpack de Lethal Launcher.");

        p.Name = (p.Name ?? "").Trim();
        if (p.Name.Length == 0 || p.Name.Length > 60) throw new InvalidOperationException("El modpack no tiene un nombre válido.");
        if (p.BepInEx != null && !VerRx.IsMatch(p.BepInEx)) throw new InvalidOperationException("Versión de BepInEx inválida en el modpack.");

        p.Mods ??= new(); p.Configs ??= new(); p.Files ??= new();
        if (p.Mods.Count > MaxMods) throw new InvalidOperationException("El modpack tiene demasiados mods.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in p.Mods)
        {
            if (m == null || !KeyRx.IsMatch(m.Key ?? "") || !VerRx.IsMatch(m.Version ?? ""))
                throw new InvalidOperationException($"Mod inválido en el modpack: {m?.Key}");
            if (!seen.Add(m.Key!)) throw new InvalidOperationException($"Mod repetido en el modpack: {m.Key}");
        }

        if (p.Configs.Count > MaxConfigs) throw new InvalidOperationException("El modpack tiene demasiados archivos de config.");
        long total = 0;
        foreach (var (rel, content) in p.Configs)
        {
            if (!SafeRel(rel) || !ConfigEditor.IsAllowedExt(rel)) throw new InvalidOperationException($"Ruta de config no permitida: {rel}");
            total += (content ?? "").Length;
        }
        foreach (var (rel, content) in p.Files)
        {
            if (!ExtraFiles.Contains(rel)) throw new InvalidOperationException($"Archivo no permitido en el modpack: {rel}");
            total += (content ?? "").Length;
        }
        if (total > MaxTotalBytes) throw new InvalidOperationException("Los configs del modpack son demasiado grandes.");
        return p;
    }

    static bool SafeRel(string rel) =>
        !string.IsNullOrWhiteSpace(rel) && rel.Length <= 200 && !Path.IsPathRooted(rel) && !rel.Contains(':')
        && !rel.Split('/', '\\').Any(seg => seg is ".." or "." or "");

    // Deja BepInEx/config exactamente como el pack (lo que no esta en el pack se borra: los configs de otro
    // perfil no deben mezclarse). El llamador ya guardo el perfil anterior, que incluye sus configs.
    public static void WriteConfigs(string gameRoot, Pack p)
    {
        var dir = Path.GetFullPath(ConfigEditor.Dir(gameRoot));
        Directory.CreateDirectory(dir);
        foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

        var prefix = dir + Path.DirectorySeparatorChar;
        foreach (var (rel, content) in p.Configs)
        {
            var full = Path.GetFullPath(Path.Combine(dir, rel));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            ConfigEditor.WriteText(full, content);
        }
        foreach (var (rel, content) in p.Files)
        {
            var full = Path.GetFullPath(Path.Combine(gameRoot, rel));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            ConfigEditor.WriteText(full, content);
        }
    }
}
