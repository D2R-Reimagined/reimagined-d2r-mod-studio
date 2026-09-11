# Releases and application updates

Run the **Release** GitHub Actions workflow from the intended release commit and enter a new stable version such as `0.2.1`. The workflow tests all three platforms, packages them, uploads the assets to a draft release, then publishes it. It uses the built-in GitHub token with contents-write permission only in the publishing job; no launcher-specific GitHub App secrets are needed.

Windows x64 gets a Velopack installer, portable archive and update feed on channel `win`. Linux x64 gets an AppImage and update feed on channel `linux`. macOS ARM64 retains a portable app bundle; automatic macOS updates are not configured. Packages include the CLI and licenses. Signing/notarization is not configured. Linux desktop/AppImage prerequisites still apply.

Studio checks on startup when running a Velopack installation. The status-bar button also checks manually, downloads an available stable release, and offers an explicit restart. Unsaved documents use the existing save/recovery prompt. Builds, migrations tracked as operations, and a running owned game block the restart. A failed check or download can be retried. Portable/development builds explain how to obtain an installed release. No project content is uploaded.

The release build stamps its version and GitHub repository URL into the application. Forks therefore use their own public release feed. No version commit is made automatically. Keep the package ID and channel names stable. These packages use full updates; fetching previous versions for delta generation is not configured.

Local Windows packaging:

```powershell
./scripts/package-release.ps1 -Version 0.2.1 -Runtime win-x64
```

Use `-Runtime linux-x64` for Linux, optionally cross-packaging from Windows. Both scripts accept `-Dotnet` and `-PackageCache`; the release script accepts `-RepositoryUrl`. Output is under `artifacts/release/<version>/<runtime>`; existing publish directories are rejected to prevent mixing builds.

If publication fails after creating a draft, inspect that draft and its assets before retrying. Existing version tags are rejected. Release uploads do not require installing the packages locally. Verify a real installed-version-to-new-version update before announcing automatic updates as end-to-end tested.
