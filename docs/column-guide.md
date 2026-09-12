# Column guide and game detection

## Column guide

Hovering a column header in the table view, or a field (label or input) in the **Row Editor**, shows what that column means. The text comes from the community [d2rdoc data guide](https://eezstreet.github.io/d2rdoc/) and is bundled into `ModStudio.Core` as `Assets/column-guide.json`, so it works offline. Each card shows the column, its documented family (`prop1` → `prop#`), the value type, the description, a parse format for expression columns, up to 12 rows of any reference table or bit-flag list, and the guide URL for the full page.

Lookups are case-insensitive by table name (`uniqueitems`, `MagicSuffix`, or a `global/excel/*.txt` target), then by column name, then by the guide's alternate names, then by collapsing digits to `#`. Studio's `#N` suffix on duplicated headers is ignored. String catalogs and tables the guide does not cover keep the plain full-name tooltip. Fields appended from shared pages (`shareditems`, `SharedItemMods`) are merged into their parent tables.

To refresh the bundled data:

```bash
git clone --depth 1 https://github.com/eezstreet/d2rdoc <checkout>
node scripts/import-column-guide.mjs <checkout>
```

The importer strips guide link markup and HTML, keeps the source commit, and truncates very long reference tables to 40 rows. `ColumnGuide.Generated`/`ColumnGuide.Commit` expose the bundled version.

### Reference navigation

When the guide documents a column as a reference to another table (`skills.srvmissile` → `missiles.Missile`), the **Details** inspector uses that as a navigation rule whenever the project has no explicit rule in `source/semantics.json`. The status line says where the value points, the matching rows are listed, and **Go to referenced row** (or double-click / Enter) opens the target table on that row. Guide targets are used for navigation only, never for validation warnings, because some point at guide-only enum pages or index-based tables. Reference columns are matched case-insensitively against the project's headers.

## Game installation detection

Run settings no longer start empty. When a project opens and the current profile has no game folder, Studio checks the usual places and pre-fills the game folder, the launch target and `mods/<name>` as the deployment folder. Nothing already set is replaced, and the result is logged so the choice is visible. The **Run settings** dialog also has **Detect installation**, which lists every installation found so you can pick between Battle.net and Steam copies.

Locations checked, in order:

1. Windows uninstall registry entries for `Diablo II Resurrected` (Battle.net) and `Steam App 2181070`.
2. `Diablo II Resurrected` and `Steam/steamapps/common/Diablo II Resurrected` under Program Files, Program Files (x86), `Games` and the root of every fixed drive.
3. Every Steam library from the Steam registry path plus `steamapps/libraryfolders.vdf`, and the Linux/macOS Steam and Wine defaults.

Only folders containing `D2R.exe` or `D2RLoader.exe` are reported. There is no full-disk search; use **Browse game folder** for unusual layouts.