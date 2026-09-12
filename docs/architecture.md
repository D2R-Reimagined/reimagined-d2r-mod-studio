# Architecture and extension points

`ModStudio.Core` owns formats, documents, import, build snapshots and deployment/process control. It has no Avalonia or Node dependency. `ModStudio.App` owns desktop controls, dialogs and view state. `ModStudio.Cli` uses the same compiler/importer. `ModStudio.Tests` contains disposable synthetic fixtures and process/deployment regression checks.

The shell borrows charcoal/gold styling ideas from the Reimagined launcher. It does not link launcher services, updater code, authentication or a specific mod's runtime paths. The compiler supports the existing consolidated Reimagined schemas so users can open that project without rewriting its data format.

## Document contract

A `Document` owns disk/schema fingerprints, an editable table projection, lazy raw text, dirty revision and undo/redo. Table and Source share this session. Source changes are deltas; table commands retain affected rows only, and bulk edits commit together. Dirty state follows history back to the saved state. Validating source is explicit; malformed input remains recoverable. A save serializes only that document and checks the original disk fingerprint before replacement.

Parsing starts off the UI thread. The grid creates one lightweight projection per row and materializes visible controls, with 24-column windows plus a frozen first column to bound very wide grids. Sorting/filtering changes view indexes. Current row validation is synchronous; full streaming storage, incremental semantic validation, cancellation-aware indexes and LRU eviction should follow measurements rather than a language rewrite. Source view and recovery materialize JSON text, so large opened tables still have a real memory cost.

## Schema example

`source/tables/example/schema.json`:

```json
{
    "schemaVersion": 1,
    "name": "example",
    "targets": ["global/excel/example.txt"],
    "identityColumns": [],
    "columns": [
        { "key": "name", "header": "name" },
        { "key": "value", "header": "value" }
    ],
    "protectedRows": 1,
    "bom": false,
    "newline": "\r\n",
    "finalNewline": true
}
```

`records.json`:

```json
[
    {
        "sourceId": "row-00000",
        "order": 0,
        "columnCount": 2,
        "fields": { "name": "Example", "value": "160" }
    }
]
```

Profile override file referenced by `tableOverrides: ["example-change.json"]`:

```json
{
    "table": "example",
    "record": "row-00000",
    "reason": "Alternate runtime balance",
    "changes": { "value": { "expect": "160", "value": "210" } }
}
```

An optional `targets` array limits an override to declared table banks. Runtime identity hashes and legacy duplicate allowances in existing schemas are checked. Source JSON must retain those contracts.

`sourceId` is a stable identity, not a slot: imported rows are `row-NNNNN` (their original slot, below `protectedRows`), rows added in Studio get a random `row-xxxxxxxx`. `order` is the physical slot and is renumbered on insertion. Validation requires unique IDs, every original row still present, and the identity hash over the original rows in their original numbering; inserting before original rows produces an advisory warning rather than an error, because only some tables treat the row number as the game ID. Original rows cannot be deleted. See `table-editing.md`. Imported generic schemas protect original row slots but do not guess game-specific keys. Semantic definitions should be versioned separately from this lossless storage schema.

## Build and deployment contracts

The build copies source inputs into a new snapshot and checks that their count/hashes stayed consistent during capture. Later edits cannot mix into its output. It validates all source tables/catalogs, applies profile rules to separate table projections, copies native assets and hashes all emitted files. The C# compiler was compared against both current Node builds; keep this differential check when evolving either compiler.

Deployment validates source/output/target separation, the selected project's ownership and all hashes before staging on the target volume. A journal describes old/new file hashes and durable backups; ownership is the last replaced file. Recovery restores the old set after interruption. Never extend this into an unrestricted directory mirror: user configuration and saves must remain outside generated ownership. Journal safety is process/interruption tested, not a guarantee of transaction durability on every filesystem.

The process controller owns a single child and gates concurrent Build/Deploy/Play. Launch uses argument arrays with shell execution disabled. Runtime-specific adapters should eventually describe executable discovery, loader configuration, child/descendant lifetime and tested platform capabilities. General runner fields currently support explicit invocation only.

## Next design work

- Introduce a handler registry with explicit read/edit/preview/build/platform capabilities. Unknown binary formats remain read-only until a verified adapter exists.
- Separate semantic game/version definitions from storage schemas; add reference indexes and incremental diagnostics with revision cancellation.
- Use the selected build profile for resolved-value provenance, item tooltips and monster calculations. Missing dependencies should produce incomplete results.
- Add a Git adapter and record/field diffs before automatic three-way merging. IDs, deletions and conflicting cells require explicit resolution.
- Add row duplication and string-catalog row creation (catalog identities are still protected).
- Evolve docking and workspace layout persistence after native keyboard/accessibility checks on all three desktop platforms.
