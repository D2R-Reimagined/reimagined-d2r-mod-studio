# Version 0.2: editing controls and milestone 4

The application is now **Reimagined D2R Mod Studio**. It remains a general editor: mod names, paths, source schemas, validation rules and runtime profiles are project data. No Reimagined game assets are shipped.

## Navigation and grid

- Folder chevrons have a 32×32 click target while retaining a compact glyph. The headless UI check clicks near the edge of that target.
- Table/catalog folders appear as one logical entry. Open the entry to edit records; choose **Edit schema…** from its context menu or **Schema…** in the table toolbar. The underlying file format is unchanged.
- Active sorting shows ▲ or ▼ in the column header and names its direction in the status line. Sorting survives cell edits, column navigation and freezes. **Freeze / Lock → Clear sorting** restores source order.
- **Freeze selected rows** keeps up to five comparison rows above the scrolling body. **Freeze current column** adds/removes columns at the left; the first column starts frozen. Frozen columns are marked ▣. Width changes and horizontal scrolling stay synchronized between the fixed and scrolling rows. **Unfreeze all** removes all pins.
- **Lock selected rows / current column against edits** protects cell edits, inspector changes and bulk paste. Locked rows show `L` in the row-number header; columns show `[locked]`. Source is read-only until locks are cleared. Existing Undo/Redo remains available. Locks and freezes are local to the current document session and are not written to mod data.
- The horizontal scrollbar is always present, has a larger handle, and spans the grid width including the space beneath frozen columns. It does not overlay the last row.
- Stop/Cancel is visible only during a cancelable operation or while the editor's child process is running.

## Legacy migration

Use **Migrate project** for a native project root, unpacked `.mpq` directory, a `data` folder, or the earlier one-file-per-record JSON format. Opening a detected legacy project also offers migration. A directory containing multiple installed mods requires choosing one. Existing consolidated projects are not treated as native-only projects.

Migration writes a separate new/empty destination. It verifies converted TXT bytes, preserves original row order/metadata when consolidating JSON, detects changed inputs, and refuses conflicts between native TXT and legacy JSON. Every migrated profile is built for validation before publication. Cancellation/failure does not publish a partial project. The original project is untouched.

For native project roots, additional files are copied under `legacy/` for review and are not deployed automatically. Mod metadata inside the selected project is retained; renaming the mod sets a separate save path. Selecting only `data` limits the imported scope; select the project root to retain outside metadata and files. Git history, local editor settings and build/cache directories are excluded. Review `migration-report.json` after migration. Binary MPQ archives must be extracted first.

CLI: `ModStudio.Cli migrate <legacy-project> <new-destination> <mod-name>`.

## Milestone 4: first usable slice

The first slice includes shared-source reference navigation, explicit field rules, profile-cell editing and cell/row-width diffs against disk. Full Git integration, three-way merging, broad D2R formula validation and profile-bank-aware semantic analysis are still future work.

Built-in advisory reference rules cover `superuniques.Class → monstats.Id`, unique/set base-item codes, and weapon/armor/misc type codes. Selecting a supported field resolves references in project tables; double-click or Enter opens a matching row. Open target-table edits participate before saving. Results refresh after edits, undo, file changes and selection changes. Missing resources are reported as unresolved/incomplete.

**Problems → Check source references / rules** runs shared-source checks with valid unsaved table buffers. Rule-file edits must be saved first. Changing inputs during checks invalidates the result. Built-in checks are warnings because community projects may omit vanilla resources. They do not pretend to cover every game field.

Use **Edit validation rules…** to open/create `source/semantics.json`. Project rules replace a built-in rule for the same table/column; `enabled: false` disables that rule. Types are `text`, `integer`, `number` and `boolean` (`0`/`1`). Optional `required`, `min`, `max`, `values`, `referenceTables` and `referenceColumn` control validation. Numeric values use invariant formatting; references are exact strings.

```json
{
    "schemaVersion": 1,
    "rules": [
        {
            "table": "uniqueitems",
            "column": "lvl",
            "type": "integer",
            "min": 0,
            "max": 99,
            "severity": "Error"
        }
    ]
}
```

That is an example project rule, not a universal D2R limit. Explicit semantic errors block Build/Deploy/Play; warnings are retained in build diagnostics. Semantic checks currently analyze shared source. Build also validates runtime override expectations and output structure, but does not yet apply every semantic rule to every effective profile bank.

The inspector shows the shared and effective profile values. **Edit profile override…** writes a reasoned override with the shared value as its `expect` guard. Save the shared table/profile documents first. The dialog checks fingerprints before writing, preserves other changes in an existing rule, and refuses stale or bank-specific rules that need explicit source editing. New dialog-created overrides apply to all declared banks. Identity/edit locks still apply. To remove an override, edit its profile/source rule; there is no automatic merge or removal UI yet.

**Changes → Compare active table with disk…** displays changed cells and row widths, with navigation back to the row. It does not compare arbitrary record metadata, schemas, or Git revisions. Dirty documents and Source remain the place to review those changes.

CLI: `ModStudio.Cli check <project>` runs the same shared-source rules.
