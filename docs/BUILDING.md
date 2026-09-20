# Building MonitorSync

Run these commands from the repository root.

## Build and package

Use the SDK pinned in `global.json` (.NET 10.0.401). The app bundles its runtime, so end users do not need to install .NET.

For local debugging on Windows, use Windows PowerShell 5.1 or PowerShell 7:

```powershell
./scripts/debug.ps1
# Build and test, then collect device readings without enabling sync:
./scripts/debug.ps1 -DiagnosticsOnly
```

The script builds Debug binaries, runs the tests, publishes the app and worker to `artifacts/debug/app`, and starts the tray app using its saved brightness/volume preferences. The diagnostics option instead saves a timestamped JSON report under `artifacts/debug` without changing preferences or enabling sync. Use **Exit** in the tray before rebuilding.

The script uses an SDK extracted into `.tools/dotnet` when present, otherwise `dotnet` from PATH. With a local SDK it also embeds its relative location into the development executables, allowing the app and worker to start at login without this shell's environment. Keep the repository and SDK together. SDK setup is separate; the script does not download it or change machine-wide environment settings.

On Windows, with PowerShell 7 and the SDK installed:

```powershell
./scripts/build.ps1
```

The script builds the solution, runs the sync tests, publishes the app and worker, then builds an unsigned per-user MSI and an optional portable ZIP under `artifacts/releases`. The MSI is the recommended installation: it installs under `%LOCALAPPDATA%\Programs\MonitorSync`, creates a Start-menu shortcut, and launches the app in the tray after a successful install or upgrade, including silent installation. Startup is enabled by default; existing control and startup preferences are preserved. Repair and uninstall do not launch the app. Uninstall removes the startup entry; settings and diagnostic logs are retained in `%LOCALAPPDATA%\MonitorSync`.

`-Runtime win-arm64` selects an ARM64 package. Only the x64 publication has been verified so far. Use a higher three-part `-Version` for upgrades. Cross-architecture upgrades are not validated.

To require a signed release, supply a code-signing certificate available to SignTool in the current user's certificate store and install the Windows SDK:

```powershell
./scripts/build.ps1 -Version 0.1.0 -Publisher 'Your publisher name' `
    -CertificateThumbprint 'YOUR_CERTIFICATE_THUMBPRINT' -RequireSigned
```

The script preserves valid dependency signatures, signs unsigned EXE/DLL payloads, timestamps signatures, signs the MSI, and verifies the result. Cloud signing providers need a corresponding signing adapter. A signing identity and Store distribution have not been configured.

Installer authoring uses WiX 6.0.2 and generated components with stable identifiers and per-user registry key paths. WiX's [maintenance fee terms](https://docs.firegiant.com/wix/osmf/) apply to qualifying use. Windows is required for the final MSI build and validation.

## GitHub Actions

The project is hosted at [nkatchik/win-monitor-sync](https://github.com/nkatchik/win-monitor-sync) with two workflows:

| Workflow | Trigger | Result |
| --- | --- | --- |
| [Build and test](https://github.com/nkatchik/win-monitor-sync/actions/workflows/build.yml) | Every push and pull request | Builds on Windows, runs all sync tests, builds the MSI and ZIP, and retains downloadable artifacts for 14 days |
| [Publish release](https://github.com/nkatchik/win-monitor-sync/actions/workflows/release.yml) | Manual, with a required `version` | Builds and tests the selected commit, packages that version, and publishes a GitHub release tagged `v<version>` with MSI, ZIP, and SHA-256 checksum files |

To publish, open **Actions → Publish release → Run workflow**, select `main` (or the branch to release), and enter a version such as `0.1.0`. The version must be three integers without a `v` prefix or prerelease suffix. MSI limits the first two numbers to 255 and the third to 65535. Existing tags are rejected; published packages are not overwritten.

Leave **commit** blank to build the latest commit on the selected branch when the workflow is dispatched. To release a specific revision, supply its full commit hash. The release tag always points to the commit actually checked out and built.

The equivalent command is:

```sh
gh workflow run release.yml --repo nkatchik/win-monitor-sync --ref main -f version=0.1.0
```

Add `-f commit=<full-commit-hash>` to select a specific revision.

The supplied workflows produce **unsigned** packages. They require no signing secrets. The manual workflow verifies checksums and attaches all packages to a draft before publishing it. If publication fails after creating the draft, review that draft before retrying the same version. Only the publication job receives permission to create releases.

The version input is applied to the MSI, filenames, application assemblies, and diagnostics. Build and release workflows do not establish live monitor compatibility or installation behavior on a real PC.

## Tests and diagnostics

The synchronization state machine and installer-source generator can build on macOS/Linux with Windows targeting enabled:

```sh
dotnet restore MonitorSync.slnx
dotnet build MonitorSync.slnx -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tests/MonitorSync.Tests/bin/Release/net10.0/MonitorSync.Tests.dll
```

The Windows build/debug scripts also run the controller integration tests. To run them separately after building:

```powershell
dotnet tests/MonitorSync.Windows.Tests/bin/Release/net10.0-windows/MonitorSync.Windows.Tests.dll
```

These tests run the real connection/retry loop on a dispatcher with simulated audio and DDC. They create no windows and do not access hardware or preferences. For a read-only hardware report:

```powershell
Start-Process ./MonitorSync.exe -ArgumentList '--diagnostics', 'report.json' -Wait
```

Reports include the Windows version, architecture, current audio endpoint and hardware-support flags, monitor device paths, DDC feature ranges/errors, and detected HID brightness controls. Device identifiers can identify the connected equipment. No report is uploaded automatically. Current runtime errors are logged to `%LOCALAPPDATA%\MonitorSync\app.log`.

Build and unit-test results do not establish real monitor support. See [Windows acceptance checks](TESTING.md) for the remaining validation.

## Source layout

| Project | Purpose |
| --- | --- |
| `MonitorSync.Core` | Transport-independent synchronization and conflict handling |
| `MonitorSync.Windows` | Core Audio COM and monitor DDC interop |
| `MonitorSync.Worker` | Isolated, serialized DDC calls with parent-exit cleanup |
| `MonitorSync.App` | Tray, automatic connection lifecycle, worker deadlines, startup preference |
| `MonitorSync.Tests` | Deterministic tests with fake audio and monitor transports |
| `MonitorSync.Windows.Tests` | Dispatcher-based connection/recovery tests with simulated platform I/O |
| `MonitorSync.Packaging` | WiX payload authoring for per-user installation |

The worker has a three-second deadline for individual operations and twenty seconds for enumeration. A timeout terminates the helper; the app retries with a fresh connection after one second. Volume keys always use ordinary Windows control, including while DDC is unavailable. Windows audio is never passed through the app. This contains a hung user-mode call, but cannot protect Windows from a fault in an existing graphics driver.
