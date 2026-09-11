# Reimagined D2R Mod Studio

A standalone D2R mod editor built with C#/.NET 10 and Avalonia. Open a consolidated JSON-source project, or import another mod creator's native `data` folder into a new project. Project identity, tables, locales and launch paths are configurable; Reimagined is a compatibility fixture, not a required dependency.

This is the first implementation of the shell/grid, document foundation, and build/deploy/play milestones. Windows execution and headless desktop rendering have been checked locally. The CI matrix is prepared for Windows, Linux and macOS; native Linux/macOS interaction and real game launches still require validation on those systems. See [validation evidence](docs/validation.md).

See [version 0.2 controls, migration and milestone 4 details](docs/version-0.2.md).

## Start editing

Extract the complete package and run `app/ModStudio.App.exe` on Windows, `app/ModStudio.App` on Linux, or `Reimagined D2R Mod Studio.app` on macOS. Self-contained packages include the .NET runtime. Packages are currently unsigned development builds; signing/notarization belongs to release setup. No game assets, game executable, D2RLoader or Wine/Proton are bundled.

1. **Open project** selects the root containing `source/`, `data/`, `compatibility/` and `modinfo.json`. Existing Reimagined consolidated projects work without another conversion.
2. For an existing native mod, choose **Import data folder**, select its actual `data` directory, choose a parent directory for the new project, and enter a mod name. Review the paths and start the import. The original is untouched. The destination must be new or empty.
3. Double-click the table name in the tree. Each schema/records pair appears as one table; use its context menu or the Schema button to edit the schema. Use Table for cells and Source for the same JSON document. Ordinary JSON/text uses the source editor; native TSV `.txt` with tab-separated columns also opens as a table. Unknown binary files get a bounded read-only hex preview.
4. Use the column-window controls for wide tables, the inspector to select any field, and Enter in the row filter. Clicking a column sorts the view only. Original runtime slots never move because of sorting.
5. Save all with Ctrl/Cmd+S. Table Ctrl/Cmd+C copies selected rows from the visible column window; Ctrl/Cmd+V pastes a TSV rectangle beginning at the current cell. Paste is a single undo transaction. Ctrl/Cmd+Z and Ctrl/Cmd+Y operate on table history; the toolbar also provides Undo/Redo for the shared document.

Source edits stay in the document even when invalid. **Apply source** validates them; Table editing pauses until syntax is valid. Save refuses invalid documents. Cell values remain strings: blank and `0` are different. Catalog IDs/keys and schema-declared runtime identities are protected in the table view. Adding/removing records currently requires Source editing with valid `order`, `sourceId` and identity metadata; there is no safe automatic ID allocator yet.

Local recovery is written every three seconds after changes into `.studio/recovery/`. At reopen, restore, inspect, discard or defer a recovery. A stale recovery cannot replace a newer disk version. Right-click a document tab to reload or close. External edits to an open file or its schema are detected; saving checks fingerprints again. Reconcile competing edits manually before saving. Atomic replacement prevents ordinary partial saves, but is not a guarantee against every filesystem or power failure.

## Build, deploy and play

Select a profile and open **Run settings**. Settings are personal files under `.studio/settings/`, excluded from imported projects' Git history.

- **Deployment directory:** the mod's own folder, for example `D:/Games/Diablo II Resurrected/mods/MyMod`, separate from source.
- **Executable:** the game executable for Standard, or the loader executable for D2RL. It must live beside `mods/` for Play.
- **Runner:** optional Wine/Proton or another explicit executable. Runner and game arguments are JSON arrays so paths and spaces remain separate arguments. Runner configuration is manual; compatibility is not auto-detected.
- **Save before build:** enabled by default. If disabled, dirty documents require a save decision before building.

**Build** validates a consistent source snapshot and produces `<cache>/builds/<id>/output/<name>.mpq/`. **Deploy** builds and replaces only owned outputs in the selected mod folder. **Play** saves, builds, deploys and starts the selected executable with `-mod <name> -txt`, followed by configured arguments. **Stop / Cancel** cancels work or terminates the child process launched by this editor. Another game started outside the editor is not tracked; a loader that detaches a separate game process also needs a future runtime-specific lifecycle adapter.

Each build has a manifest of output hashes and source snapshots. Deployment has a separate ownership manifest and rollback journal. It rejects source/deployment overlap, changed build outputs, competing owners, conflicting unowned files and externally edited owned files. It preserves unrelated files and removes obsolete owned outputs. An existing install containing identical output bytes can be adopted; otherwise use a separate test mod folder. Do not delete the ownership manifest to bypass conflicts.

Cancellation during replacement rolls back before returning. A pending journal is recovered on the next deployment. If the journal/backup has been damaged or the destination was edited during a failed transaction, the editor retains the evidence and asks for manual reconciliation. Symbolic links and directory junctions in source/deployment paths are deliberately rejected in this release; select the real canonical directory, including on macOS.

The log identifies the build and started process. This proves which output was deployed, not that the game accepted every mod change. Builds are retained for inspection; `.studio/builds/` can be removed when no build/deploy operation is active and its outputs are no longer needed.

## Community project format

```text
MyMod/
  mod-project.json          # schemaVersion, stable id, mod name
  modinfo.json              # game metadata
  source/
    tables/<table>/schema.json
    tables/<table>/records.json
    strings/<catalog>/schema.json
    strings/<catalog>/records.json
    text/*.json             # optional plain-text source assets
  data/                     # native assets kept in their runtime layout
  compatibility/<profile>/profile.json
  .studio/                  # local recovery, run settings, builds; never authored data
```

Import handles `global/excel/*.txt` and `global/excel/base/*.txt`, preserving UTF-8 BOM, newline style, column order, blank/duplicate headers, short rows and trailing newlines. Every converted TXT is regenerated and compared byte-for-byte before the new project is published. Main/base tables share source only when their original bytes are identical. Lossy encodings, mixed TXT line endings, malformed rows and overlapping paths fail explicitly rather than silently changing data.

Recognized `local/lng/strings/*.json` catalogs become locale matrices; all original locale values are checked for semantic equality. Catalog whitespace/escaping may change. Other assets remain native. Import records original file hashes in `import-report.json`. It creates fresh mod metadata for the chosen name; review your original `modinfo.json` and loader settings separately because they live outside `data/` and are not part of this import.

Each table uses a single formatted JSON array in physical runtime order. Stable formatting makes unrelated cell edits reviewable; it does not eliminate Git conflicts in the same record. Unknown supported record metadata is retained. Do not alphabetize source arrays or renumber existing slots. Use Git normally outside the editor for branches/commits; the **Changes** panel lists unsaved documents and can compare the active table with disk. Git status and merging remain outside the editor.

Profiles are data, not hardcoded mod variants. Any `compatibility/<id>/profile.json` with matching `id`, `schemaVersion: 1`, `stringMode: "standard" | "full"`, `tableOverrides` and `assetOverrides` participates in builds. An empty Standard/D2RL pair is created on import. Table overrides name a table, stable record, reason and field `expect`/`value`; stale or competing overrides block the build. Source/schema/override files are editable in Source view. [Architecture and schema example](docs/architecture.md).

## Validation coverage and planned extensions

Current checks cover JSON/TSV structure, source slots, row widths, schema-declared identities, duplicate string IDs, translation locales and compact-review hashes/placeholders, profile expectations, output collisions and deployment safety. Many imported table schemas intentionally have no game-specific identity rules. A passing build is **structural/profile validation**, not full D2R semantic lint.

Milestone 4 now includes initial field/reference rules, numeric checks, shared/profile value comparison, profile-cell editing and cell diffs against disk. Further work will expand semantic coverage, add formula analysis, profile-bank-aware semantic resolution, semantic Git diffs and conflict resolution, item tooltips, monster calculations, and confirmed external level-editor launches. The current binary preview does not edit DS1, textures, models or saves. Native IME/accessibility, OS clipboard integration, detached-loader handling, code signing and real Windows/Wine game tests remain release validation work.

## Develop and test

Install the .NET 10 SDK; no Node runtime is required by this editor or its compiler.

```sh
dotnet restore ModStudio.slnx --configfile NuGet.Config
dotnet build ModStudio.slnx --no-restore --warnaserror
dotnet run --project tests/ModStudio.Tests --no-build
dotnet run --project src/ModStudio.App --no-build -- /path/to/project
```

The regression runner is a console executable and exits nonzero on failure. `dotnet test` does not run it. Create a synthetic mod for headless rendering without distributing any game data:

```sh
dotnet run --project tests/ModStudio.Tests --no-build -- --create-ui-fixture artifacts/example-project
dotnet run --project src/ModStudio.App --no-build -- --smoke artifacts/example-project artifacts/ui-smoke
```

CLI commands (or the packaged `cli/ModStudio.Cli` executable):

```sh
dotnet run --project src/ModStudio.Cli -- import /original/data /new/project MyMod
dotnet run --project src/ModStudio.Cli -- build /new/project standard
dotnet run --project src/ModStudio.Cli -- benchmark /path/to/reimagined
dotnet run --project src/ModStudio.Cli -- compare /native/build/output /node/build/mod-root
```

PowerShell 7 packaging: `./scripts/publish.ps1 -Runtime win-x64`. Supported packaging RIDs also include `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`; packaging a RID is not evidence that it has been run. Use `-Dotnet /path/to/dotnet` or `-PackageCache /path/to/cache` when needed. CI runs the same regression and headless UI checks on three hosts and creates self-contained archives. Local environment-specific paths are not part of the project configuration.

Own code is MIT licensed. See [third-party notices](THIRD_PARTY_NOTICES.md); game and mod assets retain their own ownership and are not covered by this project's license.
