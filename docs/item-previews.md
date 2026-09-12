# Item tooltips and calculations

Hover a row in `uniqueitems` or `setitems` for a text-only item tooltip. The **Item Preview** inspector tab keeps the selected item visible and lets you choose character level (1–99) and a locale from the project's string catalogs (`enUS` by default; the game's 13 locales when no catalog declares any). Column headers show their complete names on hover, or a data-guide card when the column is documented (see `column-guide.md`).

Item names, properties and issue text are selectable in both the inspector and hover card. The hover card stays open when the pointer moves from its row into the card, so you can drag across the text, scroll, and use the platform copy shortcut. Leaving the row before the 350 ms hover delay elapses cancels the request, so a card never opens under a pointer that has already moved elsewhere.

The resolver uses the project's base-item tables, `properties`, `itemstatcost`, string catalogs and `sets`. It applies the selected profile's table overrides before resolving references. Standard/full localization follows the profile's string mode, including review checks on compact translations. Item names, base names and supported stat descriptions come from the project, rather than a Reimagined-only label list.

Unique and set roll ranges are displayed. Set-item partial bonuses, set-wide partial bonuses and full-set bonuses are separated; they are not silently treated as active. The selected row includes unsaved table edits. Other edited dependencies must currently be saved first; the preview reports their path instead of mixing their disk version with unsaved values. Bank-scoped overrides, stale expected values, ambiguous base codes, missing data and unsupported property/display functions produce explicit errors.

## Calculation scope

This is a supported subset of Diablo II property interpretation, not a complete engine emulator:

- Property functions 1/2/3, minimum/maximum damage (5/6), enhanced damage (7), supported level scaling (17), and ethereal (23) contribute to calculations.
- Authored metadata renders speed, skills and skill tabs, procs, durability, sockets, elemental ranges, time effects, charges, class skills, auras, non-class skills and state effects (functions 8–16, 18–22, 24/25 and 36) without treating them as numeric subtotals.
- Stat descriptions 1–5 and single-number localization substitution (19).
- One-hand, two-hand and throwing physical damage subtotals with separate minimum/maximum enhanced damage and flat bonuses.
- Defense subtotals, including maximum base defense + 1 for enhanced defense, ethereal rounding, and supported flat level-scaled defense.
- Strength/dexterity requirement adjustments and authored item/base required level.
- Level scaling reads `itemstatcost`'s divisor and requires its supported additive level operation.

Unknown effects stay visible with their code and min/max/parameter arguments. Derived totals are withheld when unsupported properties could make them misleading. Subtotals exclude character bonuses, automods/staffmods, socket contents, upgrades and activated set bonuses. Displayed granted skills, charges/procs and socket counts are authored values and do not attempt to simulate their runtime effects. Monster health/experience calculations remain future work. Required level currently reflects authored item/base requirements, not additional requirements introduced by effects.

Rows without a base-item code are treated as inactive/header rows rather than broken items. Missing custom property definitions and missing localization remain explicit project-data errors.

Defense rounding reference: Blizzard's [item basics](https://classic.battle.net/diablo2exp/items/basics.shtml) and [magic prefixes](https://classic.battle.net/diablo2exp/items/magic/pre.shtml). Stat names, localization and property wiring are resolved from the user's data.

## Responsiveness

Hover requests wait 350 ms, selected-row requests 120 ms. Only one resolution job runs at a time. New requests cancel old work, and results are rejected after row edits, project/profile changes or filesystem changes. Closing a tab closes its tooltip. Only the selected record is cloned on the UI thread; file loading, lookup construction, localization and arithmetic run on a worker.

Dependency parsing and localization indexes are cached by path, size and modification time. Filesystem changes clear the worker cache on its next request. Retention is bounded to 64 MiB of source bytes (object/index overhead is additional), with a 32 MiB per-input limit. This is not a process memory cap. No calculation runs for every realized grid row.

## Verification

Core tests cover ranges, localization, profile overrides, stale and bank-scoped overrides, missing effects, enhanced damage/defense, level scaling, dependency invalidation and set-bonus separation. UI smoke checks cover headers, hover opening/closing, latest-request behavior and the inspector card. Real Reimagined Unique and Set rows were also resolved read-only; unsupported effects were reported explicitly. These checks do not establish complete in-game parity.

For read-only troubleshooting:

```text
dotnet run --project src/ModStudio.Cli -- item-preview <project> uniqueitems 0 standard 80 enUS
dotnet run --project src/ModStudio.Cli -- item-preview-audit <project> standard 80 enUS
```
