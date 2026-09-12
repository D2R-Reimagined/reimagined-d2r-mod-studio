# Table editing

The table view behaves like a spreadsheet on top of Avalonia''s row-based DataGrid (`EditorPane.Cells.cs`).

## Selecting cells

- Click a cell to select it. Drag to select a rectangle. Ctrl+click adds or removes individual cells; Shift+click extends a rectangle from the last clicked cell.
- Click the row number on the left to select whole rows; Shift/Ctrl there work as usual for rows.
- Selected cells are painted gold. The row-wide blue highlight is turned off in `App.axaml` so the selection is unambiguous.
- Selection is stored as table coordinates, so it survives column windows, sorting and filtering. `EditorPane.SelectedCells` exposes it.

## Editing many cells at once

- Just start typing: the current cell opens for editing with what you typed (no second click; F2 or double-click still work to edit the existing text). Every selected editable cell displays the same text as you type, erase, or paste. Enter or clicking away commits the group as one undo step; Escape cancels and restores the original values. Locked cells and catalog identity fields stay unchanged.
- Delete clears every selected cell. The row menu has the same **Clear cells** action.
- Paste with one value on the clipboard fills every selected cell; a multi-cell block pastes from the current cell across rows and columns, appending rows at the bottom when it runs past the last one.
- Ctrl+C copies the selected cell, the bounding rectangle of a multi-cell selection (tab-separated, unselected cells inside the rectangle blank), or the full displayed rows when rows were picked by their header. The grid''s built-in whole-row copy is disabled (`ClipboardCopyMode = None`) so a single click + Ctrl+C really copies one cell.

## Adding and removing rows

- The blank line marked **＋** at the bottom of every game table creates a row as soon as something is typed into it; a fresh blank line appears below.
- Right-click a row for **Add row above / below** (one per selected row) and **Delete row(s)**. Rows that came from the imported game data cannot be deleted (clear their cells instead); rows added in Studio can.
- New rows get a random `sourceId`, later rows keep theirs (profile overrides stay attached), and only the physical `order` is renumbered. Inserting before original rows adds a warning to Problems, because in tables where the row number is the game ID (skills, missiles, uniqueitems, setitems…) that shifts existing IDs.
- Focusing a field in the Row Editor scrolls the table to that cell and selects it (moving the column window if needed) while the field keeps keyboard focus, so you can see where the value you are typing lives. Undo, paste and grid edits update the Row Editor's existing inputs in place.
- Undo works per committed value, not per keystroke: the grid and the Row Editor bracket each edit in a `Document` edit session (`BeginEditGroup`/`EndEditGroup`), so typing "something" into a blank cell and leaving it undoes back to blank in one step. Insert, delete and multi-cell edits are single undo steps; typing into the blank row is two (create, then set).
- String catalogs (`source/strings`) do not support row insertion yet: their identities are protected. Add entries in Source view.