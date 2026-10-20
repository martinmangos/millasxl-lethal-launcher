using System.Net.Http;
using System.Text.Json;

public static class CatalogService
{
    static readonly HttpClient Http = new();
    static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LethalModInstaller");

    static CatalogService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ModInstaller/1.0");
    }

    // Usa el endpoint bulk (community index completo en una llamada) en vez del
    // endpoint experimental por-paquete, que no escala para un catalogo entero.
    // OJO: ese bulk JSON pesa ~300MB+ para lethal-company. Parsearlo entero con
    // JsonDocument carga todo el DOM en memoria y congela la UI varios segundos/minutos.
    // Por eso se baja a disco en streaming y se parsea elemento por elemento
    // (Utf8JsonReader sobre el FileStream) sin nunca tener el JSON completo en memoria.
    public static async Task<List<PackageSummary>> GetCommunityIndexAsync(string community = "lethal-company", bool forceRefresh = false)
    {
        Directory.CreateDirectory(CacheDir);
        var cachePath = Path.Combine(CacheDir, $"catalog_{community}.json");

        if (forceRefresh || !File.Exists(cachePath) ||
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) >= TimeSpan.FromHours(1))
        {
            using var resp = await Http.GetAsync(
                $"https://thunderstore.io/c/{community}/api/v1/package/",
                HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            // Bajar a un .tmp propio y mover al final: nunca se lee un cache a medio
            // escribir (otra instancia descargando, conexion cortada).
            var tmpPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            await using (var netStream = await resp.Content.ReadAsStreamAsync())
            await using (var fileStream = File.Create(tmpPath))
                await netStream.CopyToAsync(fileStream);
            File.Move(tmpPath, cachePath, overwrite: true);
        }

        try
        {
            return await Task.Run(() => ParseCatalogFile(cachePath));
        }
        catch (JsonException) when (!forceRefresh)
        {
            // Cache corrupto/truncado: re-descargar una vez antes de rendirse.
            return await GetCommunityIndexAsync(community, forceRefresh: true);
        }
    }

    static List<PackageSummary> ParseCatalogFile(string path)
    {
        // Corre dentro de Task.Run (ver arriba), asi que aunque JsonDocument arme
        // el DOM completo en memoria, el trabajo pesado queda en un hilo de fondo
        // y no congela la ventana. El fix real del freeze era ese + no bufferear
        // los 300MB+ como string antes de parsear.
        var result = new List<PackageSummary>();
        var depsMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);

        foreach (var pkg in doc.RootElement.EnumerateArray())
        {
            if (!pkg.TryGetProperty("versions", out var versions) || versions.GetArrayLength() == 0)
                continue;

            var latest = versions[0]; // Thunderstore devuelve versions mas nuevas primero

            // "Owner-Name-1.2.3" -> "Owner-Name": para saber que dependencias quedan sin uso al desinstalar.
            if (latest.TryGetProperty("dependencies", out var depEls) && depEls.GetArrayLength() > 0)
                depsMap[pkg.GetProperty("full_name").GetString() ?? ""] = depEls.EnumerateArray()
                    .Select(d => d.GetString() ?? "").Where(d => d.Contains('-'))
                    .Select(d => d[..d.LastIndexOf('-')]).ToArray();
            var categories = pkg.TryGetProperty("categories", out var cats)
                ? cats.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
                : new List<string>();

            // Downloads totales = suma de "downloads" de cada version (no solo la latest)
            int totalDownloads = 0;
            foreach (var v in versions.EnumerateArray())
                if (v.TryGetProperty("downloads", out var d))
                    totalDownloads += d.GetInt32();

            result.Add(new PackageSummary(
                Owner: pkg.GetProperty("owner").GetString() ?? "",
                Name: pkg.GetProperty("name").GetString() ?? "",
                FullName: pkg.GetProperty("full_name").GetString() ?? "",
                LatestVersion: latest.GetProperty("version_number").GetString() ?? "",
                Description: latest.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                DownloadCount: totalDownloads,
                RatingScore: pkg.TryGetProperty("rating_score", out var r) ? r.GetInt32() : 0,
                IconUrl: latest.TryGetProperty("icon", out var icon) ? icon.GetString() ?? "" : "",
                IsDeprecated: pkg.TryGetProperty("is_deprecated", out var dep) && dep.GetBoolean(),
                IsPinned: pkg.TryGetProperty("is_pinned", out var pin) && pin.GetBoolean(),
                Categories: categories
            ));
        }
        LatestDeps = depsMap;
        return result;
    }

    // Dependencias de la ultima version de cada paquete del catalogo (se llena al cargarlo).
    public static IReadOnlyDictionary<string, string[]> LatestDeps { get; private set; } =
        new Dictionary<string, string[]>();

    public static async Task<PackageDetail> GetPackageDetailAsync(string owner, string name)
    {
        var baseUrl = $"https://thunderstore.io/api/experimental/package/{owner}/{name}/";
        var json = await Http.GetStringAsync(baseUrl);
        using var doc = JsonDocument.Parse(json);
        var latest = doc.RootElement.GetProperty("latest");
        var version = latest.GetProperty("version_number").GetString()!;
        var downloadUrl = latest.GetProperty("download_url").GetString()!;

        var deps = latest.TryGetProperty("dependencies", out var depsEl)
            ? depsEl.EnumerateArray().Select(d => d.GetString() ?? "").ToList()
            : new List<string>();

        var summary = new PackageSummary(
            Owner: owner, Name: name, FullName: $"{owner}-{name}",
            LatestVersion: version,
            Description: latest.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            DownloadCount: 0, RatingScore: 0,
            IconUrl: latest.TryGetProperty("icon", out var ic) ? ic.GetString() ?? "" : "",
            IsDeprecated: false, IsPinned: false, Categories: new List<string>());

        var readme = await FetchMarkdownOrEmpty($"{baseUrl}{version}/readme/");
        var changelog = await FetchMarkdownOrEmpty($"{baseUrl}{version}/changelog/");

        return new PackageDetail(summary, readme, changelog, deps, downloadUrl, version);
    }

    static async Task<string> FetchMarkdownOrEmpty(string url)
    {
        try
        {
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return "";
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("markdown", out var md) ? md.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    public static List<PackageSummary> Search(List<PackageSummary> index, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return index.OrderByDescending(p => p.DownloadCount).ToList();

        var q = query.Trim();

        bool exact(PackageSummary p) => p.Name.Equals(q, StringComparison.OrdinalIgnoreCase) || p.FullName.Equals(q, StringComparison.OrdinalIgnoreCase);
        bool startsWith(PackageSummary p) => p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) || p.FullName.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        bool contains(PackageSummary p) =>
            p.FullName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Description.Contains(q, StringComparison.OrdinalIgnoreCase);

        return index.Where(contains)
            .OrderByDescending(exact)
            .ThenByDescending(startsWith)
            .ThenByDescending(p => p.DownloadCount)
            .ToList();
    }
}
