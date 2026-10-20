using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Ventana host: toda la interfaz es ui/index.html dentro de un WebView2.
// La pagina habla con C# por postMessage {id, cmd, args} y recibe {id, ok, data|error};
// los eventos push (progreso, log, catalogo) van como {event, data}.
public class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(12, 12, 14);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
    readonly Installer installer;
    string gameRoot;
    List<PackageSummary>? catalog;
    Dictionary<string, PackageSummary> catalogByKey = new(StringComparer.OrdinalIgnoreCase);
    string catalogStatus = "loading";
    bool busy;

    public MainForm()
    {
        Text = "Lethal Launcher";
        Size = new Size(1220, 780);
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(web);

        installer = new Installer(msg => BeginInvoke(() => Emit("log", new { text = msg })));
        gameRoot = GameLocator.LoadSavedRoot() is string saved && Directory.Exists(saved)
            ? saved
            : GameLocator.Detect() ?? "";

        Load += async (_, _) => await InitWeb();
        FormClosing += (_, e) =>
        {
            if (busy && MessageBox.Show("Hay una instalación en curso. ¿Salir igual?", "Lethal Launcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE: barra de titulo oscura
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    async Task InitWeb()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LethalModInstaller", "WebView2");
        try
        {
            await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, dataDir));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Falta Microsoft Edge WebView2 Runtime.\nDescárgalo desde la web de Microsoft e instálalo.",
                "Lethal Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // sin F5/Ctrl+F: recargar perderia el estado
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        // La pagina nunca navega: cualquier link (README, etc.) se abre en el navegador del sistema.
        bool loaded = false;
        core.NavigationStarting += (_, e) => { if (loaded) { e.Cancel = true; OpenExternal(e.Uri); } };
        core.FrameNavigationStarting += (_, e) => { if (e.Uri != "about:srcdoc") { e.Cancel = true; OpenExternal(e.Uri); } };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationCompleted += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            _ = LoadCatalog(false);
        };
        core.WebMessageReceived += async (_, e) => await OnMessage(e.WebMessageAsJson);

        core.NavigateToString(LoadUi());
    }

    static string LoadUi()
    {
        static string Res(string name)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
            return new StreamReader(s).ReadToEnd();
        }
        return Res("index.html")
            .Replace("/*MARKED*/", Res("marked.min.js"))
            .Replace("/*ASCII*/\"\"", JsonSerializer.Serialize(Res("operator.txt")));
    }

    static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    async Task OnMessage(string json)
    {
        var msg = JsonNode.Parse(json)!;
        var id = msg["id"]!.GetValue<int>();
        object? result = null;
        string? error = null;
        try
        {
            result = await Dispatch(msg["cmd"]!.GetValue<string>(), msg["args"] as JsonObject ?? new JsonObject());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Emit("log", new { text = "ERROR: " + ex.Message });
        }
        Post(new { id, ok = error == null, data = result, error });
    }

    void Post(object o) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(o, Json));
    void Emit(string ev, object data) => Post(new { @event = ev, data });

    async Task<object?> Dispatch(string cmd, JsonObject a)
    {
        string Str(string k) => a[k]?.GetValue<string>() ?? "";
        List<string> Keys() => a["keys"]?.AsArray().Select(k => k!.GetValue<string>()).ToList() ?? new();

        switch (cmd)
        {
            case "state":
                return State();

            case "browseGame":
                using (var dlg = new FolderBrowserDialog { InitialDirectory = gameRoot, Description = "Carpeta de Lethal Company" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) SetRoot(dlg.SelectedPath);
                return State();

            case "detectGame":
                SetRoot(GameLocator.Detect() ?? throw new InvalidOperationException("No encontré Lethal Company en tus librerías de Steam."));
                return State();

            case "openFolder":
                RequireRoot();
                Process.Start("explorer.exe", gameRoot);
                return null;

            case "play":
                RequireRoot();
                if (!GameLocator.HasExe(gameRoot))
                    throw new InvalidOperationException("No está \"Lethal Company.exe\" en la carpeta del juego. Revisa la ruta en Ajustes.");
                GameLocator.Launch(vanilla: a["vanilla"]?.GetValue<bool>() == true);
                return null;

            case "search":
                return Search(Str("q"), a["offset"]?.GetValue<int>() ?? 0);

            case "detail":
                var d = await CatalogService.GetPackageDetailAsync(Str("owner"), Str("name"));
                return new { d.Readme, d.Changelog, d.Dependencies, d.VersionNumber };

            case "install": // instalar y actualizar son lo mismo: baja la ultima version
                await RunInstall(Keys());
                return State();

            case "toggle":
                var path = PluginPath(Str("key"));
                if (a["enabled"]!.GetValue<bool>()) ModToggle.EnableMod(path);
                else ModToggle.DisableMod(path);
                return State();

            case "depsPreview":
                RequireRoot();
                return DepsPreview(Keys());

            case "uninstall":
                RequireIdle();
                var state = Installer.LoadState(gameRoot);
                var toRemove = Keys();
                var paths = toRemove.Select(PluginPath).ToList(); // valida todas las keys antes de borrar nada
                busy = true;
                try
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        // Task.Run: un mod con GB de paquetes tarda y no debe congelar la ventana.
                        await Task.Run(() => ModToggle.UninstallMod(paths[i]));
                        state.Remove(toRemove[i]); // sin esto, reinstalarlo lo saltaba como "ya al dia"
                        Emit("log", new { text = $"  {toRemove[i]} desinstalado" });
                    }
                }
                finally
                {
                    busy = false;
                    Installer.SaveState(gameRoot, state); // aunque falle a mitad, lo ya borrado queda registrado
                }
                return State();

            case "configList":
                RequireRoot();
                return ConfigList();

            case "configRead":
                RequireRoot();
                return ConfigRead(Str("file"));

            case "configWrite":
                RequireRoot();
                var cfgPath = ConfigEditor.Resolve(gameRoot, Str("file"));
                var current = await File.ReadAllTextAsync(cfgPath);
                var next = a["text"] != null
                    ? Str("text")
                    : ConfigEditor.Apply(current, a["changes"]!.AsArray()
                        .Select(c => (c!["section"]!.GetValue<string>(), c["key"]!.GetValue<string>(), c["value"]!.GetValue<string>())));
                ConfigEditor.WriteText(cfgPath, next);
                Emit("log", new { text = $"Config guardada: {Str("file")}" });
                return ConfigRead(Str("file"));

            case "packExport":
            {
                RequireRoot();
                var packName = Str("name").Trim();
                if (packName.Length == 0) packName = ProfileManager.GetActiveProfileName();
                using var dlg = new SaveFileDialog
                {
                    Title = "Guardar modpack", Filter = "Modpack (*.json)|*.json", DefaultExt = "json",
                    FileName = CleanName(packName) + ".json", OverwritePrompt = true,
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                var pack = ModpackService.Build(gameRoot, packName, out var skipped);
                await File.WriteAllTextAsync(dlg.FileName, ModpackService.ToJson(pack), new UTF8Encoding(false));
                Emit("log", new { text = $"Modpack guardado: {dlg.FileName} ({pack.Mods.Count} mods, {pack.Configs.Count} configs)" });
                return new { path = dlg.FileName, mods = pack.Mods.Count, configs = pack.Configs.Count, skipped };
            }

            case "packOpen":
            {
                using var dlg = new OpenFileDialog { Title = "Cargar modpack", Filter = "Modpack (*.json)|*.json|Todos los archivos|*.*" };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                if (new FileInfo(dlg.FileName).Length > 40_000_000) throw new InvalidOperationException("El archivo es demasiado grande para ser un modpack.");
                var pack = ModpackService.Parse(await File.ReadAllTextAsync(dlg.FileName));
                pendingPack = pack; // se valida una vez aqui; la pagina solo confirma, nunca manda el contenido
                return new
                {
                    name = pack.Name, created = pack.Created, bepinex = pack.BepInEx, configs = pack.Configs.Count, files = pack.Files.Keys,
                    mods = pack.Mods.Select(m => new { m.Key, owner = m.Key[..m.Key.IndexOf('-')], name = m.Key[(m.Key.IndexOf('-') + 1)..], m.Version, m.Enabled }),
                };
            }

            case "packApply":
            {
                RequireRoot();
                RequireIdle();
                var pack = pendingPack ?? throw new InvalidOperationException("Primero elige un modpack.");
                if (Process.GetProcessesByName("Lethal Company").Length > 0)
                    throw new InvalidOperationException("Cierra Lethal Company antes de cargar un modpack.");
                var problems = new List<string>();
                busy = true;
                string profile;
                try
                {
                    // Perfil nuevo: tu perfil actual (mods + configs + patchers) se guarda antes de tocar nada.
                    var taken = ProfileManager.ListProfiles().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var baseName = CleanName("Pack " + pack.Name);
                    profile = baseName;
                    for (int n = 2; taken.Contains(profile); n++) profile = $"{baseName} {n}";
                    Emit("progress", new InstallProgress("Guardando tu perfil actual…", 0));
                    await Task.Run(() => { ProfileManager.CreateProfile(profile); ProfileManager.SwitchProfile(profile, gameRoot); });

                    var items = new List<(string, string)>();
                    if (pack.BepInEx != null && Installer.LoadState(gameRoot).GetValueOrDefault("BepInEx-BepInExPack") != pack.BepInEx)
                        items.Add(("BepInEx-BepInExPack", pack.BepInEx));
                    items.AddRange(pack.Mods.Select(m => (m.Key, m.Version)));
                    await installer.InstallPinned(gameRoot, items, new DirectProgress<InstallProgress>(p => Emit("progress", p)), problems);

                    foreach (var m in pack.Mods.Where(m => !m.Enabled))
                    {
                        var dir = Path.Combine(gameRoot, "BepInEx", "plugins", m.Key);
                        if (Directory.Exists(dir)) ModToggle.DisableMod(dir);
                    }
                    Emit("progress", new InstallProgress("Escribiendo configs…", 99));
                    await Task.Run(() => ModpackService.WriteConfigs(gameRoot, pack));
                    pendingPack = null;
                }
                finally { busy = false; }
                return new { state = State(), profile, problems };
            }

            case "createProfile":
                ProfileManager.CreateProfile(Str("name"));
                return State();

            case "deleteProfile":
                if (Str("name").Equals(ProfileManager.GetActiveProfileName(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("No puedes borrar el perfil activo. Cambia a otro primero.");
                ProfileManager.DeleteProfile(Str("name"));
                return State();

            case "switchProfile":
                RequireRoot();
                RequireIdle();
                await Task.Run(() => ProfileManager.SwitchProfile(Str("name"), gameRoot));
                Emit("log", new { text = $"Perfil activo: {Str("name")}" });
                return State();

            case "refreshCatalog":
                _ = LoadCatalog(true);
                return null;

            default:
                throw new InvalidOperationException("Comando desconocido: " + cmd);
        }
    }

    // Dependencias que quedarian sin uso si se desinstalan `keys`. Una dependencia compartida con otro
    // mod instalado NUNCA aparece. Sin catalogo cargado no se puede saber: devuelve vacio.
    object DepsPreview(List<string> keys)
    {
        if (CatalogService.LatestDeps.Count == 0) return Array.Empty<object>();
        var mods = ModToggle.ListInstalledMods(gameRoot);
        var byKey = mods.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        return DepGraph.Unused(keys, mods.Select(m => m.Key), CatalogService.LatestDeps)
            .Where(byKey.ContainsKey)
            .Select(k =>
            {
                var m = byKey[k];
                var dash = m.Key.IndexOf('-');
                catalogByKey.TryGetValue(m.Key, out var c);
                return new
                {
                    key = m.Key, owner = m.Key[..dash], name = m.Key[(dash + 1)..], version = m.Version,
                    icon = c?.IconUrl ?? $"https://gcdn.thunderstore.io/live/repository/icons/{m.Key}-{m.Version}.png",
                };
            }).ToList();
    }

    Pack? pendingPack;

    static string CleanName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var c = new string(s.Where(ch => !bad.Contains(ch)).ToArray()).Trim().Trim('.');
        return c.Length > 0 ? c : "Modpack";
    }

    static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    object ConfigList()
    {
        var mods = ModToggle.ListInstalledMods(gameRoot);
        return new
        {
            gameRunning = Process.GetProcessesByName("Lethal Company").Length > 0,
            files = ConfigEditor.List(gameRoot).Select(f =>
            {
                // El nombre del archivo es el GUID del plugin ("me.swipez.melonloader.morecompany"), no la
                // key del mod: se asocia por nombre normalizado, el match mas largo gana.
                // Tambien al reves: el mod "PushCompanyFork" usa el archivo "SoftDiamond.PushCompany.cfg".
                var n = Norm(f.File);
                var tail = Norm(f.Name.Split('.')[^1]);
                var mod = mods
                    .Select(m => (m.Key, Name: Norm(m.Key[(m.Key.IndexOf('-') + 1)..])))
                    .Select(m => (m.Key, Score: m.Name.Length >= 4 && n.Contains(m.Name) ? m.Name.Length
                        : tail.Length >= 6 && m.Name.Contains(tail) ? tail.Length : 0))
                    .Where(m => m.Score > 0)
                    .OrderByDescending(m => m.Score).Select(m => m.Key).FirstOrDefault();
                return new { f.File, f.Name, f.Size, f.IsCfg, mod };
            }),
        };
    }

    object ConfigRead(string file)
    {
        var path = ConfigEditor.Resolve(gameRoot, file);
        var text = File.ReadAllText(path);
        var isCfg = Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase);
        return new
        {
            file, text, isCfg,
            entries = isCfg ? ConfigEditor.Parse(ConfigEditor.SplitLines(text)) : new List<ConfigEntry>(),
        };
    }

    void SetRoot(string path)
    {
        gameRoot = Path.GetFullPath(path);
        GameLocator.SaveRoot(gameRoot);
    }

    void RequireRoot()
    {
        if (!Directory.Exists(gameRoot))
            throw new InvalidOperationException("Primero elige la carpeta del juego en Ajustes.");
    }

    void RequireIdle()
    {
        if (busy) throw new InvalidOperationException("Espera a que termine la instalación en curso.");
    }

    // La key viene de la pagina: validar que sea un nombre de carpeta simple para que
    // no pueda apuntar fuera de BepInEx/plugins.
    string PluginPath(string key)
    {
        RequireRoot();
        if (key.Length == 0 || key != Path.GetFileName(key) || key.Contains(".."))
            throw new InvalidOperationException("Mod inválido: " + key);
        return Path.Combine(gameRoot, "BepInEx", "plugins", key);
    }

    async Task RunInstall(IEnumerable<string> idents)
    {
        RequireRoot();
        RequireIdle();
        busy = true;
        try
        {
            // Reporte directo (no Progress<T>): Progress<T> encola los avisos y alguno
            // llegaria a la pagina despues de la respuesta final, re-mostrando la barra.
            await installer.InstallIdents(gameRoot, idents, new DirectProgress<InstallProgress>(p => Emit("progress", p)));
        }
        finally
        {
            busy = false;
        }
    }

    sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    async Task LoadCatalog(bool force)
    {
        catalogStatus = "loading";
        Emit("catalog", new { status = catalogStatus });
        try
        {
            catalog = await CatalogService.GetCommunityIndexAsync(forceRefresh: force);
            catalogByKey = catalog.GroupBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            catalogStatus = "ready";
            Emit("catalog", new { status = catalogStatus, count = catalog.Count });
            Emit("state", State()); // ahora se pueden calcular actualizaciones
        }
        catch (Exception ex)
        {
            catalogStatus = "error";
            Emit("catalog", new { status = catalogStatus, error = ex.Message });
        }
    }

    object Search(string q, int offset)
    {
        if (catalog == null)
            throw new InvalidOperationException("El catálogo todavía se está cargando.");
        var installed = Directory.Exists(gameRoot) ? Installer.LoadState(gameRoot) : new();
        var all = CatalogService.Search(catalog, q).Where(p => !p.IsDeprecated).ToList();
        return new
        {
            total = all.Count,
            items = all.Skip(offset).Take(48).Select(p => new
            {
                p.Owner, p.Name, p.FullName, p.LatestVersion, p.Description,
                p.DownloadCount, p.RatingScore, p.IconUrl, p.Categories,
                installed = installed.ContainsKey(p.FullName)
            })
        };
    }

    object State()
    {
        var hasRoot = Directory.Exists(gameRoot);
        var st = hasRoot ? Installer.LoadState(gameRoot) : new();
        var mods = new List<object>();
        if (hasRoot)
            foreach (var m in ModToggle.ListInstalledMods(gameRoot).OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
            {
                catalogByKey.TryGetValue(m.Key, out var c);
                var dash = m.Key.IndexOf('-');
                mods.Add(new
                {
                    key = m.Key,
                    owner = m.Key[..dash],
                    name = m.Key[(dash + 1)..],
                    version = m.Version,
                    enabled = m.Enabled,
                    icon = c?.IconUrl ?? $"https://gcdn.thunderstore.io/live/repository/icons/{m.Key}-{m.Version}.png",
                    description = c?.Description ?? "",
                    latest = c != null && IsNewer(c.LatestVersion, m.Version) ? c.LatestVersion : null,
                });
            }

        return new
        {
            gameRoot,
            gameFound = hasRoot,
            hasExe = hasRoot && GameLocator.HasExe(gameRoot),
            bepInEx = st.GetValueOrDefault("BepInEx-BepInExPack"),
            profiles = ProfileManager.ListProfiles().Select(p => p.Name),
            activeProfile = ProfileManager.GetActiveProfileName(),
            mods,
            catalog = catalogStatus,
            catalogCount = catalog?.Count ?? 0,
            busy,
        };
    }

    static bool IsNewer(string latest, string installed) =>
        Version.TryParse(latest, out var l) && Version.TryParse(installed, out var i) && l > i;
}
