# Editor performance

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
