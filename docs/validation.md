# Validation evidence

Local host: Windows, .NET SDK 10.0.302. Checks were run on 2026-09-11. This file separates implemented behavior from platform/game verification still outstanding.

## Completed locally

- Solution builds with warnings treated as errors.
- 84 regression checks pass, including the version 0.2 migration/locking/semantic/profile/diff checks: import isolation and TXT round trips, duplicate headers, blank/zero distinctions, bulk undo/redo and saved state, invalid raw source/recovery, external changes, compact translation review/placeholders, profile overrides/stale/conflicting rules, safe deployment ownership and removal, interrupted-journal recovery, cancellation rollback, path/encoding guards, failed process startup/retry, duplicate Play prevention and Stop.
- The process test launches a harmless managed child fixture and stops that child. It does not execute D2R or D2RLoader.
- Avalonia headless mode renders actual controls with Skia and checks row realization, cell setter, clipboard paste/copy, undo, invalid source, view sorting, row navigation and far-right column navigation. Both synthetic data and the current Reimagined project pass. Version 0.2 also checks the folder-chevron hit area, folded table nodes, conditional Stop controls, freezing and scrollbar alignment, sort indicators, real mouse cell selection, reference navigation and invalidation from unsaved target edits/undo. Headless clipboard behavior is not a substitute for native OS clipboard/IME checks.
- Real tables exercised: sounds (11,992 rows), cubemain (15,866 rows), skills (490 rows, 322 columns). Roughly 14–16 row controls were materialized at a 1440×920 window. Column windows bound the number of realized columns.
- Native compiler comparison against the existing Node compiler: Standard matches 2,509 files byte-for-byte plus 8 semantically identical JSON files; D2RL matches 2,509 files byte-for-byte plus 9 semantically identical JSON files. These are 2,517 and 2,518 complete output files respectively. All generated TXT files match byte-for-byte. JSON escaping/formatting is the only accepted difference in this comparison.
- A self-contained Windows x64 application/CLI package is produced by `scripts/publish.ps1`. The packaged application is also exercised through `--smoke`, and its CLI builds the real mod. Package notices are included.

The UI smoke duration includes edits, validation, copy/paste, sorting and deliberate settling delays; it is not a cold-open or input-latency measurement. Previous load/validation samples were approximately 448 ms for sounds, 299 ms for cubemain and 39 ms for skills on this host. They are observational samples, not latency or memory guarantees. Large-document validation/recovery still materializes data and warrants further profiling.

## Prepared, not yet independently verified

The GitHub Actions workflow builds, runs regression checks, creates synthetic data, renders/exercises controls and packages on Windows, Ubuntu and macOS. It has been authored locally; no remote CI run has been claimed. Cross-platform publishing support does not itself establish native platform quality.

Before a public release, run that matrix and perform native keyboard, mouse, clipboard, Unicode/IME, screen-reader, display scaling and file-dialog checks on all supported platforms. Check real game loading and Standard/D2RL launch behavior on the exact Windows/Wine/Proton configurations to be supported. Loader processes that detach their children need dedicated lifecycle integration. macOS signing/notarization and Windows signing are not configured.

No real game installation was deployed to or launched for these checks. No game/mod assets are included in the editor package. Structural/profile validation does not prove gameplay correctness or complete game-specific lint coverage. Item/monster previews, broader semantic coverage, profile-bank-aware semantic resolution, three-way Git merging and specialist editors remain later milestones.
