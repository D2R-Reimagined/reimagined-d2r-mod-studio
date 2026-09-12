# Editor performance

## Build, deploy and Play (September 2026)

Measured on the Reimagined project (2,531 source files, 735 MB of native assets), an unchanged build took about 21 s and a deployment several seconds more, all spent re-reading files that had not changed:

- `Storage.NoLinks` walked every ancestor of every path with three syscalls per level, and `Files()` did that for every entry it enumerated (1.3–1.9 s per directory listing). It now stats each level once, and build/deployment run inside `Storage.PathChecks()`, a scope that remembers verified directories. Enumeration reads reparse attributes straight from the listing. Files are never memoized; a replaced file is still checked.
- Every source, snapshot, output and deployed file was SHA-256'd on every build and again on deploy. `BuildCache.FileHash` now keeps a fingerprint (size, write time, hash) per path, persisted in `.studio/hashes.json`, and only reads a file whose size or write time changed. A file written within the last two seconds is always hashed again, because a same-tick rewrite could keep the same stamp. `FileEntries()` hands the enumeration's own size/time to the cache, so an unchanged file costs no syscall at all.
- The semantic check ran over the whole snapshot on every build. It is cached in `builds/current/semantics.json`, keyed on the rules file and the hashes of only the tables the rules read.
- Deployment enumerates the build output and the mod folder once instead of stat'ing each of the 2,500 paths three times.

Result on the same project: an unchanged build is ~0.4 s and a deploy ~0.15 s in-process; editing one cell and pressing Play is ~0.8 s for build plus deploy. The first build after updating still hashes everything once to fill the cache.

Known limit: a file rewritten with identical size and an identical write-time stamp more than two seconds after the fact is not re-hashed. Tools that preserve timestamps while changing content (some sync clients, `touch -r`) can do this; deleting `.studio/hashes.json` forces a full re-hash.

## Editing and undo (September 2026)

- `TableData.Validate` did `Columns.Take(width).Contains(key)` for every field of every row (O(rows × columns²)); columns are now indexed, and the identity hash is written straight through `Utf8JsonWriter` instead of building a `JsonArray`. A full pass over `sounds` (12,000 rows) is still ~170 ms cold / ~40 ms warm, dominated by `JsonNode` string materialization; it now runs only on open and structural changes.
- A cell edit or its undo no longer re-validates the whole table. `SetCell` protects identities, slots and the row set, so when the table was valid before, only the edited rows are rechecked (`TableData.ValidateRows`). Any table that already has an error keeps running the full pass so its diagnostics never go stale. Twenty edits on `sounds` now take 7 ms in total; twenty undos 1 ms.
- `Document.LastChangedRows/LastChangedColumns` describe a change that only replaced cell values (null after inserts, deletes or source edits). `EditorPane.Undo/Redo` use them to refresh the affected rows in place instead of rebuilding columns, auto-fit widths and every RowView; the full rebuild still happens when the view is filtered, or sorted by a column the step touched.
- The Row Editor updates its existing inputs when the same row changes revision (undo, paste, grid edits) instead of recreating hundreds of fields, which also keeps keyboard focus.
- Recovery serializes the whole document on the UI thread. The 3-second timer now skips documents edited in the last 2 seconds, so it runs after a typing pause instead of during one; closing the project still writes everything.

## Preview and table pass

The September 2026 performance pass reduces repeated work before increasing concurrency:

- Preview file reads, parsing, decoding and channel conversion run away from the UI dispatcher. A shared queue permits one preview job at a time. Switching frames/channels, reloading or closing cancels queued work; an already-running native/library decode finishes, then its stale result is discarded. Channel loops check cancellation between chunks.
- Each attached preview has a 32 MiB LRU cache of decoded frames. RGB and Alpha views derive from immutable cached RGBA pixels. Oversized frames still display within the existing preview limits, but are not cached. Reload, palette changes and detaching discard the cache, so reopening reads the current file. This is a cache budget, not a total process memory limit: encoded data, bitmap uploads and decoder scratch memory also consume memory.
- Uncompressed sprite frames copy only their selected rectangle, instead of copying the full atlas first. Compressed frames still need atlas decoding on a cache miss.
- DS1 occupancy previews select and order layers once per render, instead of allocating a sorted collection for every tile.
- Table auto-sizing samples up to 200 rows and measures at most 12 distinct longest cell candidates per column, plus the header. Width remains approximate and capped at 220 pixels; manual resizing remains available.
- JSON folding uses an immutable text snapshot on a worker, debounces edits, cancels superseded scans and applies only results for the current revision. Hidden source views do not scan, and unchanged fold ranges are reused for collapse/expand and tab reattachment. Fold application still occurs on the dispatcher.

## Verification

`dotnet run --project tests/ModStudio.Tests -c Release` includes a repeatable 256×256 DS1 fixture, cache budget/eviction and channel-integrity checks, plus queue cancellation/failure coverage. The UI smoke suite verifies cached frame/channel switches, explicit reload, close-during-load, asynchronous folding replacement, and existing table/row-editor behavior.

On the development machine, five combined DS1 previews measured **46.5 ms / 111,411,520 allocated bytes before**, and **1.6 ms / 1,313,000 bytes after**. These are synthetic decoder-only measurements, not whole-editor startup timings or measurements on older hardware. Timing is reported rather than used as a pass/fail threshold; an allocation guard catches a return to per-tile collections.

Further measurements on affected machines should identify the operation (project open, specific table, typing, row selection or asset preview), file size, CPU, RAM and storage type. Full-table validation after edits, recovery serialization, inspector snapshot creation, and UI bitmap/fold application still contain synchronous work; they have not been removed or represented as fixed by this pass.
