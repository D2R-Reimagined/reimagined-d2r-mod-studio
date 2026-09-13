# Import mod and folder selection

**Import mod…** on the toolbar is the single entry point for anything that is not already a Studio project: a mod folder, an unpacked `.mpq` directory, a `data` folder, the game's `mods` folder, or the earlier one-file-per-record JSON format. Opening such a folder with **Open project** offers the same window.

One window collects every decision, pre-filled where possible:

- **Original mod:** the folder you want to edit. Studio finds native data at that root, in `data/`, or nested below it, searching up to 12 directory levels and excluding build/dependency/cache folders and directory links. When several data roots exist, a dropdown lists them; Studio does not merge them.
- **Mod name:** suggested from the folder; used for deployment under `mods/<name>`.
- **Project folder (where you edit):** defaults to `<Documents>/D2R Mod Studio/<name>` (the `ProjectsFolder` preference). This must not be inside the game's `mods` folder: the project holds editable JSON source, not the files the game reads.
- **Game mods folder (where Studio deploys):** auto-detected from the installed game as `<game>/mods/<name>` and written to Run settings for every profile after import. Leave it empty when no installation is detected and set it later in Run settings.
- **Mode:** create a new project, or convert in place.

- **Create a new project:** the original mod is left unchanged. Additional project files are retained under `legacy/`; Git metadata and caches are excluded.
- **Convert in place:** the verified conversion replaces the selected project at its existing path. A timestamped sibling backup keeps the entire original. Git metadata and supporting files remain in the converted project. Close other tools that are writing to the repository. Linked directories are rejected. If the final replacement fails, Studio attempts to restore the original root from the backup.

Both modes verify table conversion and profile builds before publication. Conversion records its backup location in `migration-report.json`. Backup folders are not removed automatically. Studio never updates external scripts to understand the new source format; review project-specific build scripts after migrating.

## Run settings

The **source project** is where you edit. It is not your deployment destination.

For a mod named `Reimagined`, choose a deployment folder such as:

`C:/Games/Diablo II Resurrected/mods/Reimagined`

Select the final `Reimagined` folder, not the installation directory, `mods`, `.mpq`, or `data`. Studio writes the native `.mpq/data` structure under it. The folder can be new; type its path if it does not exist yet.

Select `D2R.exe` for the game executable, or the appropriate loader for your profile. Play requires the deployment folder under `mods/<mod-name>` beside that executable. Selecting an executable suggests that deployment path when the destination is blank. Wine/Proton users can specify a separate runner executable.

Settings displays path warnings as you edit. Incomplete settings can be saved; Build works without game paths. Deployment and Play perform their own checks, including source separation, ownership and existing files, before copying or launching.

**Overwrite destination** in Run settings is enabled by default, including for existing settings, and saved separately for each profile. Deploy and Play can replace existing files included in the build, including files edited outside Studio. Unrelated destination files remain untouched. Uncheck it to reject differing unowned files and externally edited deployed files. Another project's deployment remains protected, as do externally edited files that would be removed rather than replaced. Interrupted deployments retain the existing backup and rollback behavior.

Migration copies and fingerprints files using at most four workers, with file counts, processed MiB and current paths. Convert existing leaves old generated `.studio/builds/` only in the complete original backup; recovery files and Git metadata are preserved. Source conversion and profile validation remain ordered. Cancellation is checked between file chunks; cleanup can take additional time.
