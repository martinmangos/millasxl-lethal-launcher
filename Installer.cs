using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

// Logica de instalacion sin UI (antes vivia en MainForm). Reporta progreso via
// IProgress para que la interfaz web muestre la barra.
public record InstallProgress(string Text, double Percent);

public class Installer
{
    readonly HttpClient http = new();
    readonly Action<string> log;

    public Installer(Action<string> log)
    {
        this.log = log;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ModInstaller/1.0");
    }

    public static string StatePath(string gameRoot) => Path.Combine(gameRoot, "modinstaller_state.json");

    public static Dictionary<string, string> LoadState(string gameRoot)
    {
        var path = StatePath(gameRoot);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static void SaveState(string gameRoot, Dictionary<string, string> state) =>
        File.WriteAllText(StatePath(gameRoot), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    // Instala (o actualiza a la ultima version) los idents dados + sus dependencias + BepInEx.
    public async Task InstallIdents(string gameRoot, IEnumerable<string> idents, IProgress<InstallProgress> progress)
    {
        var packages = new Dictionary<string, (string Owner, string Name, string DownloadUrl, string Version)>();
        progress.Report(new("Resolviendo dependencias...", 0));
        await ResolveAll("BepInEx-BepInExPack", packages);
        foreach (var ident in idents)
            await ResolveAll(ident, packages);
        log($"{packages.Count} paquete(s): {string.Join(", ", packages.Keys)}");

        var state = LoadState(gameRoot);
        var pending = packages.Where(p => !(state.TryGetValue(p.Key, out var v) && v == p.Value.Version)).ToList();
        foreach (var key in packages.Keys.Except(pending.Select(p => p.Key)))
            log($"  {key} ya esta al dia, skip");

        var tmpRoot = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        try
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var (key, info) = (pending[i].Key, pending[i].Value);
                double basePct = i * 100.0 / pending.Count, span = 100.0 / pending.Count;
                var label = $"Descargando {info.Name} {info.Version} ({i + 1}/{pending.Count})";

                var zipPath = Path.Combine(tmpRoot, key + ".zip");
                await Download(info.DownloadUrl, zipPath, f => progress.Report(new(label, basePct + f * span * 0.9)));

                progress.Report(new($"Instalando {info.Name}...", basePct + span * 0.9));
                await Task.Run(() =>
                {
                    if (info.Name.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase))
                        InstallBepInEx(zipPath, gameRoot);
                    else
                        InstallMod(key, zipPath, gameRoot);
                });
                state[key] = info.Version;
                SaveState(gameRoot, state); // por paquete: si falla a mitad, lo ya instalado queda registrado
            }
            progress.Report(new("Listo", 100));
        }
        finally
        {
            Directory.Delete(tmpRoot, true);
        }
    }

    // Instala cada (key, version) EXACTA, sin resolver dependencias: un modpack ya las lista todas.
    // Si esa version ya no existe en Thunderstore, instala la ultima y lo avisa en `problems`.
    // key/version vienen de un archivo ajeno: el llamador las valida (solo [A-Za-z0-9_] y N.N.N).
    public async Task InstallPinned(string gameRoot, IReadOnlyList<(string Key, string Version)> items,
        IProgress<InstallProgress> progress, List<string> problems)
    {
        var state = LoadState(gameRoot);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var (key, version) = items[i];
                var dash = key.IndexOf('-');
                var (owner, name) = (key[..dash], key[(dash + 1)..]);
                double basePct = i * 100.0 / items.Count, span = 100.0 / items.Count;
                var label = $"Instalando {name} {version} ({i + 1}/{items.Count})";
                progress.Report(new(label, basePct));
                try
                {
                    var (url, actual) = await ResolveVersion(owner, name, version);
                    if (actual != version) problems.Add($"{key}: la versión {version} ya no existe, se instaló la {actual}");

                    var zipPath = Path.Combine(tmpRoot, key + ".zip");
                    await Download(url, zipPath, f => progress.Report(new(label, basePct + f * span * 0.9)));
                    await Task.Run(() =>
                    {
                        if (name.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase)) InstallBepInEx(zipPath, gameRoot);
                        else InstallMod(key, zipPath, gameRoot);
                    });
                    state[key] = actual;
                    SaveState(gameRoot, state);
                }
                catch (Exception ex)
                {
                    problems.Add($"{key} {version}: {ex.Message}");
                    log($"  ERROR {key}: {ex.Message}");
                }
            }
            progress.Report(new("Listo", 100));
        }
        finally
        {
            Directory.Delete(tmpRoot, true);
        }
    }

    async Task<(string Url, string Version)> ResolveVersion(string owner, string name, string version)
    {
        var baseUrl = $"https://thunderstore.io/api/experimental/package/{owner}/{name}/";
        var resp = await http.GetAsync($"{baseUrl}{version}/");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            resp = await http.GetAsync(baseUrl); // version retirada: cae a la ultima
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var v = doc.RootElement.TryGetProperty("latest", out var latest) ? latest : doc.RootElement;
        return (v.GetProperty("download_url").GetString()!, v.GetProperty("version_number").GetString()!);
    }

    async Task Download(string url, string dest, Action<double> onFraction)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var net = await resp.Content.ReadAsStreamAsync();
        await using var file = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await net.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0) onFraction((double)read / total.Value);
        }
    }

    async Task ResolveAll(string ident, Dictionary<string, (string, string, string, string)> seen)
    {
        var parts = ident.Split('-');
        var owner = parts[0];
        var name = parts[1];
        var key = $"{owner}-{name}";
        if (seen.ContainsKey(key)) return;

        var json = await http.GetStringAsync($"https://thunderstore.io/api/experimental/package/{owner}/{name}/");
        using var doc = JsonDocument.Parse(json);
        var latest = doc.RootElement.GetProperty("latest");
        seen[key] = (owner, name, latest.GetProperty("download_url").GetString()!, latest.GetProperty("version_number").GetString()!);

        if (latest.TryGetProperty("dependencies", out var deps))
            foreach (var dep in deps.EnumerateArray())
            {
                var depIdent = dep.GetString()!;
                if (depIdent.StartsWith("bbepis-BepInExPack")) continue;
                await ResolveAll(depIdent, seen);
            }
    }

    void InstallBepInEx(string zipPath, string gameRoot)
    {
        var tmp = Extract(zipPath, "BepInExPack");
        var dirs = Directory.GetDirectories(tmp);
        CopyDirRecursive(dirs.Length == 1 ? dirs[0] : tmp, gameRoot);
        log($"  BepInEx instalado en {gameRoot}");
    }

    static readonly string[] MetaFiles = { "manifest.json", "icon.png", "readme.md", "changelog.md" };
    // Carpetas de BepInEx que se mergean tal cual (no son "el mod" que se togglea).
    static readonly string[] SharedDirs = { "config", "patchers", "core", "monomod" };

    // Mismo criterio que r2modman: cada entrada de la raiz del zip se ubica segun su
    // nombre. Antes, si el zip traia BepInEx/plugins/, se descartaba todo lo demas de
    // la raiz (ej. "Lethal Teto.dll" de KoolKids-KasaneTeto: quedaban los trajes sin modelo).
    void InstallMod(string key, string zipPath, string gameRoot)
    {
        var tmp = Extract(zipPath, key);
        var bepinex = Path.Combine(gameRoot, "BepInEx");
        var pluginDir = Path.Combine(bepinex, "plugins", key);
        // Instalacion limpia: sin esto, al actualizar quedaban .dll de la version vieja
        // (o archivos *.disabled mezclados con los nuevos si el mod estaba apagado).
        if (Directory.Exists(pluginDir))
            Directory.Delete(pluginDir, true);
        Directory.CreateDirectory(pluginDir);

        foreach (var entry in Directory.GetFileSystemEntries(tmp))
        {
            var name = Path.GetFileName(entry);
            if (File.Exists(entry))
            {
                if (!MetaFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                    File.Copy(entry, Path.Combine(pluginDir, name), true);
            }
            else if (name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                // BepInEx/plugins/* es el mod; BepInEx/<otra> (config, patchers) se mergea.
                foreach (var sub in Directory.GetFileSystemEntries(entry))
                    PlaceTopDir(sub, pluginDir, bepinex);
            }
            else
                PlaceTopDir(entry, pluginDir, bepinex);
        }
        if (FlatLibs.TryGetValue(key, out var libs))
            foreach (var lib in libs)
            {
                var nested = Path.Combine(pluginDir, lib, lib + ".dll");
                if (File.Exists(nested)) File.Copy(nested, Path.Combine(pluginDir, lib + ".dll"), true);
            }
        log($"  {key} instalado");
    }

    // Mirage 1.29.x carga sus librerias como <carpeta de MirageCore>/<Lib>.dll (planas), pero el zip de
    // MirageCore 1.0.4 las trae en subcarpetas (FSharp.Core/FSharp.Core.dll): sin esta copia falla con
    // FileNotFoundException y Mirage no arranca ni crea su config.
    static readonly Dictionary<string, string[]> FlatLibs = new()
    {
        ["qwbarch-MirageCore"] = new[] { "FSharp.Core", "Collections.Pooled", "IcedTasks", "SileroVAD", "FSharpPlus" },
    };

    static void PlaceTopDir(string entry, string pluginDir, string bepinex)
    {
        var name = Path.GetFileName(entry);
        if (File.Exists(entry))
            File.Copy(entry, Path.Combine(pluginDir, name), true);
        else if (name.Equals("plugins", StringComparison.OrdinalIgnoreCase))
            CopyDirRecursive(entry, pluginDir);
        else if (SharedDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
            CopyDirRecursive(entry, Path.Combine(bepinex, name));
        else
            CopyDirRecursive(entry, Path.Combine(pluginDir, name));
    }

    string Extract(string zipPath, string label)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(zipPath)!, "extract_" + Path.GetFileNameWithoutExtension(zipPath));
        ZipFile.ExtractToDirectory(zipPath, tmp);
        var blocked = SecurityFilter.FilterZipEntries(tmp);
        if (blocked.Count > 0)
            log($"  bloqueados {blocked.Count} archivo(s) peligroso(s) en {label}");
        return tmp;
    }

    public static void CopyDirRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(src, file)), true);
    }
}
