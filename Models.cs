// Contratos compartidos entre los modulos. No tocar desde otros archivos sin avisar.
public record PackageSummary(
    string Owner,
    string Name,
    string FullName,
    string LatestVersion,
    string Description,
    int DownloadCount,
    int RatingScore,
    string IconUrl,
    bool IsDeprecated,
    bool IsPinned,
    List<string> Categories
);

public record PackageDetail(
    PackageSummary Summary,
    string Readme,
    string Changelog,
    List<string> Dependencies, // formato "Owner-Name-Version"
    string DownloadUrl,
    string VersionNumber
);

public record InstalledModInfo(
    string Key,        // "Owner-Name"
    string Version,
    bool Enabled,
    string PluginPath  // carpeta BepInEx/plugins/<key>
);

public record ProfileInfo(
    string Name,
    string FolderPath  // carpeta donde vive mods.json + state.json de este perfil
);
