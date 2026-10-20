# millasxl-lethal-launcher

Launcher de mods para Lethal Company (Windows, .NET 10 + WebView2).
(an alternative to Thunderstore/Overwolf, you fucking Jews)

- Instala mods de Thunderstore con sus dependencias y versiones exactas.
- Perfiles (mods + configs + patchers) y modpacks compartibles en un `.json`.
- Editor de configs de BepInEx por mod.
- Desinstala mods junto con las dependencias que ningun otro mod usa.

## Compilar

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Requiere el runtime de Microsoft Edge WebView2 (viene con Windows 11).
