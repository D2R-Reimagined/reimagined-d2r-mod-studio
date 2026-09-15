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

## Copying table rows

Click a row number to select the whole row, then press Ctrl+C (or use **Copy row** in the context menu). Click another row number and press Ctrl+V to replace its values across all columns, including columns outside the current grid window. Selecting several destination rows pastes one copied row into each of them. One undo restores the whole paste. Protected identity columns keep the destination row's identity; locked rows or columns block the paste.

## Keep using your TXT editor

Migration does not require using Studio's grid. **External editor** in the toolbar provides:

* **Open External Editor…** for the current table (also in explorer and document-tab context menus).
* **Open External Editor Workspace…** for the deployed `global/excel` folder (also in explorer folder menus).
* Editor settings, synchronization/conflict review, and **End external editing**.

Choose a deployment folder in Run settings and configure your editor executable. Opening externally saves source documents, reconciles an existing session, builds/deploys the selected profile, then launches the editor. For a table with multiple output banks, choose which TXT to open. Both launch modes track all generated TXT tables, so sibling-file edits made by your editor are included.

Native TXT projects open their existing `data/global/excel` files directly, without building, deploying, or claiming a deployment folder. Their external saves already update source. Native TXT files also have the single-file external editor action in the explorer and document tabs. The deployment/synchronization workflow applies to generated JSON source tables.

If the deployment belongs to an earlier conversion or another project, Studio offers **Back up and reuse this folder** after building. Approval applies to the owner record you reviewed; changing that record invalidates approval. Replaced files and the old ownership record are retained under `.studio/deployment-backups` with a `backup.json` listing the destination and changes. Files outside the new build remain in place. Cancel leaves the deployment unchanged. This option is available for external editing, Deploy, and Play; the Overwrite destination setting still controls replacement of edited files.

Arguments are entered **one argument per line**, without surrounding quotes:

| Placeholder | Meaning |
| --- | --- |
| `{file}` | Selected deployed TXT file |
| `{workspace}` | Deployed `global/excel` folder |
| `{files}` | Every TXT below that folder, each passed as a separate argument |

The **Use TXTeditor arguments** preset uses `{file}` and `{files}`. TXTeditor [0.5.4's launch handler](https://github.com/yinyin333333/TXTeditor/blob/0.5.4/src-tauri/src/launch_paths.rs) accepts text-file paths, so this opens the workspace's tables as individual files; it does not invoke TXTeditor's native Open Folder command. Use Open Folder inside TXTeditor if you prefer that workspace view. Editors that accept a directory can use `{workspace}` directly. An empty executable uses the system file association, or the file manager for folders. Very large `{files}` launches may exceed Windows' command-line limit; Studio reports this without truncating the selection.

### Synchronization

While Studio is open, saved TXT changes are imported after filesystem activity settles. Saved JSON changes are exported back to the tracked TXT files. Clean open Studio documents refresh automatically; unsaved documents are preserved and must be saved before merging. The external editor controls how it reloads files changed on disk.

The session is stored under `.studio/external-editor` and resumes when the project reopens, including edits saved while Studio was closed. A periodic scan covers missed watcher events. Only generated TXT tables are synchronized; binary files, localization JSON, newly created unmanaged TXT files, and editor configuration files are outside this feature.

Different-cell edits merge automatically. For conflicting edits to the same cell, use **Synchronize / resolve conflicts…** and choose Studio or external values for the conflicts; other edits still merge. Edits to existing profile overrides stay in their override files, including target-specific banks. Divergent edits to a shared cell in different TXT banks require reconciling those files first.

Existing-cell edits and appended rows are supported. Existing source IDs and table metadata are retained; appended rows receive new IDs. Removed/reordered rows, changed headers or protected identities, malformed/partial saves, and stale overrides pause synchronization with a diagnostic. TXT has no Studio row IDs, so keep existing rows in their original order. Unidentifiable structural edits require manual reconciliation.

Build and Deploy refuse to overwrite pending TXT changes even when **Overwrite destination** is enabled. End the session before deploying a different profile or target. Synchronization validates all proposed changes and uses a recoverable journal for multi-file writes; interrupted commits resume only if the files still match the recorded versions. Recovery data is retained when a newer edit prevents safe completion. Source/game checks still run on Build; successful synchronization alone does not prove in-game correctness.
