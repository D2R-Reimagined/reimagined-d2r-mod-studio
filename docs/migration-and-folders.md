# Migration and folder selection

Select your project root, such as `C:/dev/d2r/d2r-reimagined-mod`. Studio finds native data at that root, in `data/`, or nested below it. It searches up to 12 directory levels, excluding build/dependency/cache folders and directory links. When several data roots exist, select the intended one; Studio does not merge them. The review shows the project root and exact native data path separately.

- **Create a copy:** choose a parent directory. Studio creates a new subfolder using the mod name. Additional project files are retained under `legacy/`; Git metadata and caches are excluded.
- **Convert existing:** the verified conversion replaces the selected project at its existing path. A timestamped sibling backup keeps the entire original. Git metadata and supporting files remain in the converted project. Close other tools that are writing to the repository. Linked directories are rejected. If the final replacement fails, Studio attempts to restore the original root from the backup.

Both modes verify table conversion and profile builds before publication. Conversion records its backup location in `migration-report.json`. Backup folders are not removed automatically. Studio never updates external scripts to understand the new source format; review project-specific build scripts after migrating.

## Run settings

The **source project** is where you edit. It is not your deployment destination.

For a mod named `Reimagined`, choose a deployment folder such as:

`C:/Games/Diablo II Resurrected/mods/Reimagined`

Select the final `Reimagined` folder, not the installation directory, `mods`, `.mpq`, or `data`. Studio writes the native `.mpq/data` structure under it. The folder can be new; type its path if it does not exist yet.

Select `D2R.exe` for the game executable, or the appropriate loader for your profile. Play requires the deployment folder under `mods/<mod-name>` beside that executable. Selecting an executable suggests that deployment path when the destination is blank. Wine/Proton users can specify a separate runner executable.

Settings displays path warnings as you edit. Incomplete settings can be saved; Build works without game paths. Deployment and Play perform their own checks, including source separation, ownership and existing files, before copying or launching.
