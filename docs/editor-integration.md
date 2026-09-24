# Level Editor integration

Use **Level Editor → Open selection in Level Editor** on a `levels` row, a `lvlprest` File cell, or a paired scene asset. The Level Builder also has an **Open in Level Editor** button. Multiple preset variants are offered in a picker; unassigned generated areas are not guessed.

Use **Level Editor → Companion applications** to inspect detection, browse to another launch application, test compatibility, or reset automatic detection. Start each new portable copy once to register it. Installed Windows applications are found using their Velopack package IDs; manual selections take precedence. A missing override produces an error instead of launching a different copy.

Level Editor's **Level** panel returns to Studio's exact level/preset record or Vis/Warp cell. The selected profile travels with the request. Existing application windows are reused; switching projects/scenes retains the normal unsaved-change prompts. One UI instance is used per installation, with a separate project lease preventing two installations from editing the same project.

## Data ownership

Studio compiles profile-specific read-only TXT snapshots under `.studio/integration/<profile>/`. This uses the same table override implementation as builds. It does not build/deploy the mod. Saved source changes refresh the snapshot while the linked project is open; Level Editor marks offline or failed refreshes.

Scene JSON, DS1 and sidecars are saved into their authoritative project files. Missing base scene assets require an explicit copy into the project before editing. Direct profile asset overrides resolve to their real source. Generated asset transforms are rejected for scene editing. Authored level metadata and explicitly saved scene pairs are respected.

Level Editor routes table editing back to Studio while linked; its standalone native-workspace editing remains available. Studio's existing external TXT editor feature is separate.

## Platforms and discovery

Studio remains cross-platform. Its Level Editor launch actions explain the current Windows requirement on Linux/macOS. The shared layer supports Windows executables/Velopack, AppImage paths, native executables and macOS bundles for future native Level Editor releases. No Wine or remote launching is enabled.

Per-user registrations and selections live in `D2RReimagined/EditorIntegration` under the platform local application-data directory. They are outside application installations. Windows Velopack launch uses `Update.exe start <mainExe> -- <arguments>`; temporary AppImage mount locations are not persisted. Several portable copies require explicit selection.

## Protocol and tests

Both apps accept `--integration-request <json-file>` and `--integration-probe <reply-file>`. Protocol version 1 carries project ID/root/profile, source record identity plus key fallback, optional column, and project-relative scene paths. Current-user named pipes route running-instance requests. Replies distinguish opened/cancelled/unsupported/failed and include the receiving PID. Paths and action/version are validated; the protocol does not execute arbitrary commands.

The source-distributed contract at `src/ModStudio.Core/Companion/EditorIntegration.cs` must be byte-identical to `src/D2RLevel.Core/Companion/EditorIntegration.cs` in Level Editor. Update both copies together. This lets either repository build independently without a sibling checkout or a published package. Core tests cover snapshots, stale overrides, native TXT, IPC, identity, path containment and structured launch arguments.

For a Windows round trip with real extracted assets, run:

```powershell
./scripts/Test-CompanionIntegration.ps1 -StudioExecutable <ModStudio.App.exe> -LevelEditorExecutable <D2RLevel.App.exe> -GameData <folder-containing-hd-and-global> -Dotnet <dotnet.exe>
```

The smoke uses an isolated fixture and preferences, opens a real town scene, edits and saves it, follows the Level Editor return link to `levels.Id=1 / Vis0`, saves a Studio table change, verifies the same Level Editor process receives the new table value, and captures both UIs. Extracted game assets are read only. It does not deploy or publish anything.

`REIMAGINED_INTEGRATION_HOME` and `MOD_STUDIO_PREFERENCES` can isolate portable test sessions. Level Editor's `--settings-file` isolates its preferences. A direct command-line project/preset open is forwarded to an existing compatible instance.
