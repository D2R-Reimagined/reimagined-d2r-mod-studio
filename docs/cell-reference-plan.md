# Cell reference navigation

The Gamble interaction now applies across 416 audited field families in 61 tables. The bundled guide remains pinned to commit `0b3dc30377d077fd550b7388405fc6328c1c9a44` (generated 2026-09-12).

## Coverage

The [per-table inventory](cell-reference-inventory.md) and [complete audit](cell-reference-inventory.json) cover all 89 tables and 1,734 documented field families. Run `node scripts/audit-cell-references.mjs` to regenerate both inventories and the embedded `src/ModStudio.Core/Assets/cell-reference-rules.json` registry. The generator rejects stale curated field names. Registry entries retain source families, expanded aliases, lookup kinds and targets; the inventory retains evidence and guide URLs. Audit notes describe candidate mappings, including the exclusions below.

| Area | Enabled relationships |
| --- | --- |
| Direct guide keys | All 222 table/key declarations: skills, missiles, item and monster types, skill prerequisites, stats, states, overlays, sounds, classes, modes, body locations, colors, categories, monster definitions, set membership and more |
| Item codes | Gamble, unique/set bases, starting equipment, monster equipment, belt defaults, books, upgrades, transmogrification and better gems |
| Properties | Affix, unique/set, rune, gem, cube, monster and property-group codes; optional PropertyGroups does not block ordinary Properties links |
| Conditional parameters | Paired property function determines skill, state or monster-type navigation. Functions 9/11/19 reference skills; 22/24 use known parameter-bearing stats; 25 references a property ID. MonPet uses its paired consumption stat. Numbered, prefixed and difficulty aliases are preserved |
| Numeric and group references | Level IDs, warps, presets, substitution groups, auto-affix groups, row slots, object/shrine lookups, conditional States graphics class |
| Localization | Documented string keys search all authored catalogs and retain duplicate keys |
| Expressions | Named skill/stat/missile operands in calculation fields, cube input/output item tokens, treasure-class tokens, monster component alternatives and literal summon modifier indices |

Automagic `mod1code` links to Properties. `mod1param` depends on the paired property's function: for example, a skill property with parameter `36` opens the skill with `*Id=36`. A socket count or skill-tab enum does not become a skill ID. Numeric IDs accept the guide's older unstarred spelling when actual project headers are `*Id`/`*ID`.

## Interaction and performance

- The small gold top-right arrow and Alt+Enter work in ordinary and frozen cells. Text editing and editing locks retain their normal behavior.
- A direct match opens or reuses the target tab, reveals its row, highlights the whole row and selects only the key cell for editing. A filter is cleared only if it hides the destination. Existing visible grids are reused.
- Multiple matches open a chooser. Missing or malformed required targets produce an incomplete-search explanation alongside available matches. Unknown values never select an arbitrary row.
- Table parses, validation and per-column indexes are reused. Valid unsaved buffers are cloned once per document revision. Disk timestamps/lengths and workspace changes invalidate caches; replaced cell requests are canceled. The Details inspector uses the same resolver.
- Pending source never falls back to stale disk content. Source/target edits and workspace changes invalidate pending navigation. Destination source IDs or catalog IDs are rechecked after opening, so inserted rows do not redirect a stale hit to another record.
- Navigation reads shared authored values, includes valid unsaved edits, and never saves files. It adds no semantic validation rules.

## Intentional limits and follow-up

These are excluded or produce an explanation rather than a guessed destination:

- `skills.range` and other guide enums/functions are documentation, not editable record tables.
- `lvlprest/lvlsub.Dt1Mask` requires level context and maps bits to asset columns, not a cell value to a row.
- Item `component`, item `gemoffset`, `shrines.LevelMin` and `monstats2.HitClass` remain outside the registry until their runtime encodings/context are verified. These eight audited families are retained in the inventory.
- Numeric slots use known ID metadata when available (including MonStats `*hcIdx`, Missiles/Objects `*ID` and MonUMod `id`). Other row-slot links use exported physical row order, including custom rows. Tables with Expansion markers and no known ID column require a table-specific compiler interpretation and do not silently use a raw row number.
- Calculation navigation recognizes named `skill('name'.operand)`, `stat('name'.operand)` and `missile('name'.operand)` calls. It does not evaluate formulas or infer runtime-computed IDs.
- Cube and treasure-class parsing navigates the initial stored item/type/class/name token. Modifiers do not become keys. Runtime-generated classes, recipe operations and computed modifier results have no direct row. More complex modifier-selected identities need dedicated parsers.
- Property groups can link directly to other groups without recursive expansion, so cycles cannot hang navigation. Parameters controlled by groups or custom/unknown property functions remain explanatory until their interpretation is known. Skill-tab numbers, random ranges, time enums, socket counts and ordinary numeric stat values are not skill links.
- Asset paths, inverse explanatory mentions and function-table documentation do not receive automatic record arrows.

## Verification

Core regression coverage exercises direct and aliased families, conditional parameters and literal exclusions, duplicate groups, numeric sentinels, formula operands, recipe/treasure tokens, catalog identities, unsaved buffers, pending source, malformed/missing targets, cancellation, cache reuse/invalidation and stable destinations. The desktop smoke covers mouse/keyboard activation, frozen rows, ordinary editing, stale choosers, offscreen row reveal and full-row highlight. Validation uses synthetic project data and isolated build outputs.

For a focused desktop regression, create a fresh fixture with `ModStudio.Tests --create-ui-fixture <directory>`, then run `ModStudio.App --smoke <directory> <output> --cell-references-only`. Success writes `cell-references-passed.json` plus rendered Gamble, Amulet and missile screenshots. The focused test also rejects caught current-row initialization errors and verifies that malformed open target JSON cannot fall back to disk.
