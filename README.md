# Monitor Sync

A Windows 11 tray app that controls monitor speaker volume and brightness through DDC/CI, with a Windows-style brightness indicator. Volume follows the selected audio output; brightness follows the mouse cursor. There is no custom driver, audio routing, or administrator service.

The first target is the Dell S2725QS over HDMI, followed by DisplayPort. HDMI checks confirmed direct monitor volume control with Windows held at 100%. DDC remains intermittent on this setup; bounded read recovery and automatic reconnection handle failures. Physical keyboard acceptance and DisplayPort testing remain incomplete. See [validation results](docs/TESTING.md).

## Preview behavior

- Volume keys adjust the monitor by 2%. The app shows no volume overlay; the monitor may show its own OSD. The selected monitor's Windows endpoint stays at **100%** during normal operation.
- The tray menu includes **Volume XX%** and **Brightness XX%** sliders for currently available controls. Volume targets the selected monitor speakers; brightness targets the screen under the cursor. Opening the menu reads live levels without changing them. Dragging or using arrow keys adjusts the level without closing the menu.
- Monitor-button changes are checked every five seconds and update the tray's displayed level. Windows remains at 100%.
- Control starts automatically from live readings. If Windows is below 100%, the app confirms a write at the lower current percentage before raising Windows to 100%. This removes one attenuation stage and can change perceived loudness. Saved volume levels are never restored.
- Rapid input is coalesced and writes are read back. Failed reads get up to three attempts, 500 ms apart. A failed connection retries after one second; unsupported-monitor discovery repeats every two seconds.
- Quick Settings and other endpoint writes below 100% are treated as absolute monitor-volume requests. Windows returns to 100% only after DDC readback matches the requested monitor setting; a successful write return alone is insufficient. The native slider therefore does not display the monitor level; use the volume keys and check the monitor OSD or tray status.
- Volume follows the currently selected Windows playback output when it is a supported monitor. Switching to headphones suspends monitor control; returning to monitor speakers resumes it. Cursor position does not select the audio target.
- The mute key toggles Windows endpoint mute; volume adjustment preserves it and individual application volumes. Monitor hardware mute is not synchronized.
- The app runs entirely in the notification area. Its only setting is **Start with Windows**, enabled by default; the tray also shows status and an **Exit** action. When a non-monitor output is selected, the status reads **No monitor speakers selected** and the volume slider is hidden. The tray does not display Windows gain.
- Keyboard **brightness keys (usually marked with sun icons)** adjust brightness by 5% on the screen under the mouse cursor, independently of the audio output. Hold a key to keep adjusting. **Ctrl+Alt+Page Up / Page Down** remains available as a fallback; the tray slider works without special keys.
- Keyboard brightness changes show a temporary indicator at the bottom centre of that screen. It follows the Windows light/dark theme and fades without taking focus. Tray slider adjustments use the menu itself for feedback, without an extra overlay.
- There is no settings window, pairing step, or pause switch. Brightness bypasses the native Windows slider; the indicator is provided by this app.

If the cursor's screen cannot be identified unambiguously or controlled through DDC/CI, brightness changes nothing. It never falls back to another screen. Moving the cursor alone does not change brightness; crossing screens discards pending work for the old target. Each new key burst starts with a fresh hardware reading, including changes made using the monitor's own buttons.

Brightness input supports standard HID Consumer display-brightness usages (`0C:6F/70`), Apple Vendor Keyboard (`FF01:20/21`), and Apple Top Case (`00FF:04/05`). Apple usages require Apple vendor ID `05AC`. The parser handles button arrays, individual buttons, scalar values, multiple report IDs, and relative pulses. It listens across Consumer/Apple collections and discovers other HID collections that declare brightness controls.

Magic Keyboard/Boot Camp support depends on the driver exposing one of those reports. Firmware or drivers that consume Fn internally, or expose only ordinary F1/F2, cannot be distinguished automatically. Plain function keys and keyboard-backlight keys are left alone; the fallback shortcuts remain available. No Apple keyboard was connected for physical validation.

## First run on Windows

1. Enable **DDC/CI** in the Dell's on-screen menu. Start with a direct HDMI connection and select the Dell speakers as the default Windows playback output.
2. Set both volumes to comfortable levels. Extract the complete preview ZIP into a folder and run `MonitorSync.exe`; keep the worker and runtime files together.
3. Sync starts in the tray without opening a window. Right-click its icon to use the available sliders, check status, or change **Start with Windows**.
4. Try the **Volume** slider or volume keys, then the Dell's volume buttons. The tray shows monitor volume while Windows stays at 100%; allow up to five seconds for monitor-button changes to appear in the menu.
5. Use the **Brightness** slider on the screen you want to adjust. Alternatively, move the pointer onto that screen and use the keyboard's brightness keys (sun icons) or **Ctrl+Alt+Page Up / Page Down**. Keyboard-backlight keys are different, and Fn keys consumed by firmware or vendor software may not reach the app.

Use **Exit** in the tray to stop the app. It leaves current volumes in place and starts syncing again on the next launch. Turning off **Start with Windows** is remembered across launches and upgrades; it does not stop the current session. Old saved pairing and pause settings are no longer used.

If DDC fails, the tray shows that the monitor is unavailable while automatic retries continue. Ordinary Windows volume remains usable. Enable DDC/CI in the monitor's menu and select its speakers in Windows; no in-app configuration is required.

Automatic selection requires Windows to identify the output as display audio and its driver-provided monitor name to match exactly one enumerated monitor model after connector suffixes are removed. This is a conservative naming heuristic, not a hardware identity guarantee. Missing/mismatched names, duplicate models, clone mode, and ambiguous physical mappings remain unsupported; the app waits rather than guessing.

The generated preview is unsigned. A trusted public MSI has not been produced. Signing and real Windows installation tests are required before consumer distribution; a fresh signature alone cannot guarantee SmartScreen reputation. [Microsoft's guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)

## Build and package

Use the SDK pinned in `global.json` (.NET 10.0.401). The app bundles its runtime, so end users do not need to install .NET.

For local debugging on Windows, use Windows PowerShell 5.1 or PowerShell 7:

```powershell
./scripts/debug.ps1
# Build and test, then collect device readings without enabling sync:
./scripts/debug.ps1 -DiagnosticsOnly
```

The script builds Debug binaries, runs the tests, publishes the app and worker to `artifacts/debug/app`, and starts the tray app with automatic sync. The diagnostics option instead saves a timestamped JSON report under `artifacts/debug` without changing startup preferences or enabling sync. Use **Exit** in the tray before rebuilding.

The script uses an SDK extracted into `.tools/dotnet` when present, otherwise `dotnet` from PATH. With a local SDK it also embeds its relative location into the development executables, allowing the app and worker to start at login without this shell's environment. Keep the repository and SDK together. SDK setup is separate; the script does not download it or change machine-wide environment settings.

On Windows, with PowerShell 7 and the SDK installed:

```powershell
./scripts/build.ps1
```

The script builds the solution, runs the sync tests, publishes the app and worker, then builds an unsigned per-user MSI and ZIP under `artifacts/releases`. It installs under `%LOCALAPPDATA%\Programs\MonitorSync`, creates a Start-menu shortcut, and removes the startup entry during uninstall. The settings and diagnostic logs are retained in `%LOCALAPPDATA%\MonitorSync`.

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

The equivalent command is:

```sh
gh workflow run release.yml --repo nkatchik/win-monitor-sync --ref main -f version=0.1.0
```

The supplied workflows produce **unsigned** packages. They require no signing secrets. The manual workflow verifies checksums and attaches all packages to a draft before publishing it. If publication fails after creating the draft, review that draft before retrying the same version. Only the publication job receives permission to create releases.

The version input is applied to the MSI, filenames, application assemblies, and diagnostics. Build and release workflows do not establish live monitor compatibility or installation behavior on a real PC.

## Tests and diagnostics

The synchronization state machine and installer-source generator can build on macOS/Linux with Windows targeting enabled:

```sh
dotnet restore MonitorSync.slnx
dotnet build MonitorSync.slnx -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tests/MonitorSync.Tests/bin/Release/net10.0/MonitorSync.Tests.dll
```

Run this on Windows for a read-only JSON report:

```powershell
Start-Process ./MonitorSync.exe -ArgumentList '--diagnostics', 'report.json' -Wait
```

Reports include the Windows version, architecture, current audio endpoint and hardware-support flags, monitor device paths, DDC feature ranges/errors, and detected HID brightness controls. Device identifiers can identify the connected equipment. No report is uploaded automatically. Current runtime errors are logged to `%LOCALAPPDATA%\MonitorSync\app.log`.

Build and unit-test results do not establish real monitor support. See [Windows acceptance checks](docs/TESTING.md) for the remaining validation.

## Source layout

| Project | Purpose |
| --- | --- |
| `MonitorSync.Core` | Transport-independent synchronization and conflict handling |
| `MonitorSync.Windows` | Core Audio COM and monitor DDC interop |
| `MonitorSync.Worker` | Isolated, serialized DDC calls with parent-exit cleanup |
| `MonitorSync.App` | Tray, automatic connection lifecycle, worker deadlines, startup preference |
| `MonitorSync.Tests` | Deterministic tests with fake audio and monitor transports |
| `MonitorSync.Packaging` | WiX payload authoring for per-user installation |

The worker has a three-second deadline for individual operations and twenty seconds for enumeration. A timeout terminates the helper; the app retries with a fresh connection after one second. During unavailable periods volume keys return to ordinary Windows control and the app does not force Windows to 100%. Windows audio is never passed through the app. This contains a hung user-mode call, but cannot protect Windows from a fault in an existing graphics driver.
