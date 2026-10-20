using System.Text;
using System.Text.RegularExpressions;

// Lectura/edicion de BepInEx/config. Los .cfg de BepInEx traen metadatos en comentarios
// (descripcion, tipo, valor por defecto, valores aceptables) que se usan para armar el
// formulario. La escritura cambia SOLO la linea "Clave = valor" de cada ajuste editado,
// asi comentarios y orden del archivo quedan intactos.
public record ConfigEntry(
    string Section, string Key, string Value, string Description, string Type,
    string? Default, string[]? Values, bool Flags, string? Min, string? Max, int Line);

public record ConfigFileInfo(string File, string Name, long Size, bool IsCfg);

public static class ConfigEditor
{
    static readonly string[] Extensions = { ".cfg", ".json", ".yaml", ".yml", ".toml", ".txt", ".ini" };
    const long MaxBytes = 1_000_000;
    const int MaxDepth = 3;
    static readonly UTF8Encoding Utf8 = new(false);

    public static bool IsAllowedExt(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public const long MaxFileBytes = MaxBytes;

    public static string Dir(string gameRoot) => Path.Combine(gameRoot, "BepInEx", "config");

    public static List<ConfigFileInfo> List(string gameRoot)
    {
        var dir = Dir(gameRoot);
        var result = new List<ConfigFileInfo>();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
            var ext = Path.GetExtension(path);
            if (rel.Count(c => c == '/') >= MaxDepth || !Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            var size = new FileInfo(path).Length;
            if (size > MaxBytes) continue;
            result.Add(new ConfigFileInfo(rel, Path.GetFileNameWithoutExtension(path), size, ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase)));
        }
        return result.OrderBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // El nombre viene de la pagina: validar que quede dentro de la carpeta de config.
    public static string Resolve(string gameRoot, string rel)
    {
        var dir = Path.GetFullPath(Dir(gameRoot)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, rel));
        if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
            || !Extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)
            || !File.Exists(full))
            throw new InvalidOperationException("Archivo de config inválido: " + rel);
        return full;
    }

    public static string[] SplitLines(string text) => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

    public static List<ConfigEntry> Parse(string[] lines)
    {
        var result = new List<ConfigEntry>();
        string section = "", type = "";
        string? def = null, min = null, max = null;
        string[]? values = null;
        bool flags = false;
        var desc = new List<string>();

        void Reset() { desc.Clear(); type = ""; def = min = max = null; values = null; flags = false; }

        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0) { Reset(); continue; }
            if (l.StartsWith('[') && l.EndsWith(']')) { section = l[1..^1]; Reset(); continue; }
            if (l.StartsWith("##")) { desc.Add(l[2..].Trim()); continue; }
            if (l.StartsWith('#'))
            {
                var c = l[1..].Trim();
                if (c.StartsWith("Setting type:")) type = c["Setting type:".Length..].Trim();
                else if (c.StartsWith("Default value:")) def = c["Default value:".Length..].Trim();
                else if (c.StartsWith("Acceptable values:")) values = c["Acceptable values:".Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                else if (c.StartsWith("Acceptable value range:"))
                {
                    var m = Regex.Match(c, @"From\s+(.+?)\s+to\s+(.+)$");
                    if (m.Success) { min = m.Groups[1].Value; max = m.Groups[2].Value; }
                }
                else if (c.Contains("Multiple values can be set")) flags = true;
                continue;
            }
            var eq = l.IndexOf('=');
            if (eq <= 0) continue;
            result.Add(new ConfigEntry(section, l[..eq].Trim(), l[(eq + 1)..].Trim(), string.Join("\n", desc),
                type, def, values, flags, min, max, i));
            Reset();
        }
        return result;
    }

    public static string Apply(string text, IEnumerable<(string Section, string Key, string Value)> changes)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text);
        var entries = Parse(lines);
        foreach (var (section, key, value) in changes)
        {
            if (value.Contains('\n') || value.Contains('\r'))
                throw new InvalidOperationException($"El valor de {key} no puede tener saltos de línea.");
            var e = entries.FirstOrDefault(x => x.Section == section && x.Key == key)
                ?? throw new InvalidOperationException($"No existe [{section}] {key} en el archivo.");
            var raw = lines[e.Line];
            lines[e.Line] = raw[..(raw.IndexOf('=') + 1)] + " " + value;
        }
        return string.Join(nl, lines);
    }

    // Escritura atomica: si algo falla a mitad, el archivo original queda intacto.
    public static void WriteText(string path, string text)
    {
        var tmp = path + ".launcher.tmp";
        File.WriteAllText(tmp, text, Utf8);
        File.Move(tmp, path, overwrite: true);
    }
}
