# Third-party components

Mod Studio's own MIT license does not replace upstream licenses. The distributable includes `licenses/`, package-provided license/notice files, and the notices included with the self-contained .NET runtime. Preserve them when redistributing binaries.

| Component | Source / license |
| --- | --- |
| Avalonia 12.1.2, desktop backends, Fluent theme, DataGrid, Headless and Inter integration | [Avalonia](https://github.com/AvaloniaUI/Avalonia), MIT; `licenses/Avalonia.txt` |
| AvaloniaEdit 12.0.0 | [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit), MIT; `licenses/AvaloniaEdit.txt` |
| Markdown.Avalonia.Tight 12.0.0-a1 and its ColorTextBlock/ColorDocument controls | [Markdown.Avalonia](https://github.com/whistyun/Markdown.Avalonia), MIT; `licenses/Markdown.Avalonia.txt` |
| Markdig 1.3.2 | [Markdig](https://github.com/xoofx/markdig), BSD-2-Clause; `licenses/Markdig.txt` |
| MicroCom.Runtime 0.11.6 | [MicroCom](https://github.com/kekekeks/MicroCom), MIT; `licenses/MicroCom.txt` |
| Tmds.DBus.Protocol 0.94.1 | [Tmds.DBus](https://github.com/tmds/Tmds.DBus), MIT; `licenses/Tmds.DBus.txt` |
| Inter font | [Inter](https://github.com/rsms/inter), SIL Open Font License 1.1; `licenses/Inter.txt` |
| SkiaSharp 3.119.4 and native assets | [SkiaSharp](https://github.com/mono/SkiaSharp), MIT wrapper plus native third-party notices copied from its packages |
| HarfBuzzSharp 8.3.1.3 and native assets | [SkiaSharp/HarfBuzzSharp](https://github.com/mono/SkiaSharp), MIT wrapper plus native third-party notices copied from its packages |
| Avalonia ANGLE Windows native assets 2.1.27548.20260419 | Package `LICENSE` copied during packaging |
| .NET runtime | [dotnet/runtime](https://github.com/dotnet/runtime), runtime/package license and third-party notices copied during packaging |

Build-time-only Avalonia.BuildServices is restored through NuGet and not called by the editor at runtime. Package manifests (`*.deps.json`) identify the actual shipped dependencies. The packaging script copies available package notices, including native font/shaping/rendering notices; additional transitive packages should receive a license review when versions change.

No Blizzard game assets, game executable, D2RLoader, proprietary codecs, external level editor or Reimagined mod data are included. The example project is generated entirely from synthetic values. The Reimagined launcher is a visual reference only; this project does not include its application code.
