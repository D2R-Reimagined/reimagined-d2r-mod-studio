# Reimagined D2R Mod Studio

### So why the hell does this tool exist?
Well, there are a few reasons. First off I can't stand the amount of merge conclicts that happen on the game's text files when trying to work in a shared space. It becomes very difficult to view diffs and track changes. Having to juggle multiple tools and constantly navigate the windows explorer made me wanna scream as well

### What were the goals of this project?

* Handles ALL file types that exist in d2r modding. Json, txt, d1s, etc. (Relative tools are opened for each, level editor for example is opened for you when editing those files upon confirmation)
* * I did not want to have to jump between different tools to different thing. You still need to for level editing, sprite editing, etc but this will serve as the entry point to all of that.
* A "Source Directory" and "Deployment Directory" (Store source files, and your mod deployment files in different area. Deploy/Play will bundle and move for you)
* * The ability to seperate your mod install directory and your source directories allows you to add additional files that don't belong in your mod directory. 
* A single "Play" button from within the editor. With options (D2RLoader, vanilla, whatever)
* Fully handles linting and checks for you.
* * Runs the D2RLint tool automatically and keeps your files in check
* Adding in common calculations
* * Hate having to go figure out what the hell the end Health of a monster is? Or what the skill description is? Yea me too. Hopefully this solves that
* Item tooltip rendering in certain views (Hover unique row in unique editor, and see the built item - or error if incorrect)
* New file system - Move away from the .txt files for much better collaboration and less merge conflicts
* Built-in Git (Git tab beside the project tree, Ctrl+K): tick the files to include, commit, push, pull, switch branches, view diffs and history - driven by your installed `git`, so SSH keys and credential managers just work

## Table editor notes

* Every column of a table is shown. Under the hood only the columns near the horizontal viewport exist in the grid (the rest are stood in for by spacers), which is what keeps 300-column tables such as skills, missiles and monstats smooth to scroll in both directions.
* Adding, deleting, pasting, saving, undo and redo keep the grid where you were scrolled; only a new filter goes back to the top.
* **Column guide**: press **F1** (or use the toolbar's ? button, right-click a column header, or click a field name in the Row Editor) for the searchable d2rdoc guide of the current column, with the current cell's code highlighted and a link to the online page. Hovering a header shows a short summary only.
* **Hover cards** on unique/set item rows can be switched off with the toolbar toggle (or in the Item Preview tab); the Item Preview tab always shows the selected item.
* **Column letters**: the toolbar's View menu can put the spreadsheet letter of each column (A … Z, AA …) in front of its name, in headers and in the Row Editor, for people who know the TXT files by letter. Letters follow the file's column order even when columns are frozen or moved on screen, and the Row Editor's search finds a field by its letter. The choice is remembered.
* **Frozen columns** are tinted (cells and header) so they read as one pinned block, the way frozen rows are outlined.
* While a cell is being typed, the status line under the table shows the **character count against the game's 255 limit** and whether **parentheses/brackets balance**; the Row Editor shows the same in its status line and the Details tab under its value box.
* **Arrow keys stay in the cell** while it is being edited: Left/Right move the caret, Up/Down jump to the start/end of the text. Enter and Tab commit, Escape puts the value back as it was.
* **Document tabs** can be dragged sideways to reorder them: a ghost of the tab follows the pointer and a bar marks where it will land; the order is kept with the session.
* **View settings** (toolbar *View* button, or the table toolbar's View menu): table font and size, source editor font and size (a list of common fonts, or any installed one), column letters and item hover cards. **Ctrl + mouse wheel**, **Ctrl + plus / minus** change the text size of the view under the pointer (table or source), **Ctrl + 0** restores the default; all of it is remembered.
* **Colour transforms** (monstats `TransLvl`, superuniques `Utrans`/`(N)`/`(H)`): right-click the cell → *Pick colour transform…*, or the *Pick colour…* button beside the field in the Row Editor. Every hue table of the act palette is drawn as a strip of the colours it produces, with the current value highlighted; clicking a row writes its number into the selected cells. Studio ships no game data: the swatches come from a `pal.pl2` found under the project (`data/global/palette/ACT1/pal.pl2`), the deployed mod, or the game folder, or one you choose once from your extracted game data (remembered in preferences).
* **Source view**: right-click for cut/copy/paste/undo/redo; the bracket at the caret and its partner are highlighted; JSON that no longer parses is marked on its line as you type, with the message in the status line; the editor font and size are set in View settings.
* Build, Deploy and Play report warnings such as an item name with no string-catalog key; the details are in the Problems tab and the **Log** tab keeps every status-bar message (click the status bar to open it).
* The **deployment folder's name is the mod's name**: Studio lays the build out as `<folder>.mpq` and launches the game with `-mod <folder>`. Deploying to `mods/MyMod-test` therefore runs the same project as a separate mod beside `mods/MyMod`; the folder no longer has to be named after the project.

## Keep using your TXT editor

Migration does not require using Studio's grid. **External editor** in the toolbar provides:

* **Open External Editor…** for the current table (also in explorer and document-tab context menus).
* **Open External Editor Workspace…** for the deployed `global/excel` folder (also in explorer folder menus).
* Editor settings, synchronization/conflict review, and **End external editing**.

### Synchronization

While Studio is open, saved TXT changes are imported after filesystem activity settles. Saved JSON changes are exported back to the tracked TXT files. Clean open Studio documents refresh automatically; unsaved documents are preserved and must be saved before merging. The external editor controls how it reloads files changed on disk.

The session is stored under `.studio/external-editor` and resumes when the project reopens, including edits saved while Studio was closed. A periodic scan covers missed watcher events. Only generated TXT tables are synchronized; binary files, localization JSON, newly created unmanaged TXT files, and editor configuration files are outside this feature.
