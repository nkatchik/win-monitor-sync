# Preview validation

## Verified in the macOS build environment

- .NET SDK 10.0.401 downloaded from Microsoft's release metadata and checked against its published SHA-512.
- Release solution build: zero warnings and zero errors, with warnings treated as errors.
- All 22 deterministic synchronization tests pass. Cases cover initial alignment, coalescing and held keys, external changes, mute preservation, stale operations, delayed confirmation, write failures, quantization, cancellation, and output switching.
- Self-contained Windows x64 application and worker publication.

These macOS checks do not execute Windows COM, DDC, WPF, MSI installation, or the Dell hardware. WiX explicitly reports Windows-only support, so the final MSI build runs in [Windows CI](https://github.com/nkatchik/win-monitor-sync/actions/workflows/build.yml). CI builds the application, runs the 22 sync tests, builds and validates the MSI, and uploads the packages. Refer to each run for its actual result. Initial Windows hardware results are recorded below; installation and ARM64 remain untested.

Both workflows pass actionlint validation. A local build using `-p:Version=1.2.3` was also inspected: both app and worker assemblies report `1.2.3`. The manual publication workflow is available with a required version input; creating a release is separate from verifying an automatic build.

The [first Windows CI run](https://github.com/nkatchik/win-monitor-sync/actions/runs/34363624736) passed on 9 September 2026: build, tests, MSI packaging, and artifact upload. This verifies package creation, not installation or live DDC behavior.

## Dell S2725QS acceptance

### Windows hardware debug session, 11 September 2026

Tested on Windows 11 Pro build 26200, x64, with an NVIDIA GeForce RTX 3070 (driver `32.0.15.9186`) and a Dell S2725QS on HDMI1. The default output was `DELL S2725QS (NVIDIA High Definition Audio)`. The repository-local .NET 10.0.401 SDK was downloaded from Microsoft and its archive SHA-512 verified against Microsoft's release metadata.

- Debug solution build: zero warnings and errors; all 22 deterministic tests passed.
- The app's read-only diagnostics successfully exercised WPF startup, Core Audio, worker launch, physical-monitor enumeration, and DDC reads. Initial readings were Windows volume 14%, monitor volume 45/100, brightness 100/100, and Windows mute off.
- Direct DDC volume changes 45 → 44 → 45 and brightness changes 100 → 99 → 100 were confirmed by readback.
- A temporary hardware harness using the production sync engine and Windows adapters confirmed initial alignment to 14%, a Windows endpoint change to 13% reaching the monitor, and a direct monitor change to 12% reaching Windows through the default five-second poll. The initially unmuted state was preserved.
- Cleanup restored Windows volume 14%, monitor volume 45%, and brightness 100%.
- A subsequent read-only probe completed 80/80 volume reads successfully. The final app diagnostic report confirmed the original values.
- `scripts/debug.ps1 -DiagnosticsOnly` passed on Windows PowerShell 5.1 and restored the caller's runtime environment. The visible Debug app reached its ready-to-pair state with both controls available and sync disabled.

The first hardware sync attempt encountered a DDC volume-read failure; cleanup still restored the initial values. A traced repeat passed. Native DDC errors now include the Windows error code and system message to make a recurrence diagnosable. This does not establish sustained reliability.

These checks used API writes and readback. Physical monitor buttons, Quick Settings/media-key interaction, audible/visible effects, mute toggling, HDR behavior, DisplayPort, reconnect/sleep, and installation still need interactive validation. Local reports and traces are under ignored `artifacts/debug`; device identifiers are not checked into the repository.

### Remaining interactive checks

Record the Windows build, GPU model/driver, connector/cable, active monitor input, audio endpoint, HDR state, and a diagnostic report for each run. Use direct HDMI first, then direct DisplayPort. Keep the initial listening level comfortable; compare percentages separately from perceived loudness.

| Check | Expected result |
| --- | --- |
| Discovery before enabling | Reports live Windows and monitor values; changes neither |
| Windows volume change with sync paused | Establish whether monitor OSD already follows it; if it does, investigate existing hardware integration before enabling duplicate control |
| Enable with unequal values | Both align to the lower current value; neither is forced to 100% |
| Quick Settings and media keys | Monitor follows the latest Windows volume; native slider remains at the confirmed normalized monitor percentage |
| Drag slider and hold volume key | Values make progress during continuous input; no feedback oscillation or late rollback |
| Dell volume buttons | Windows follows within approximately five seconds while idle |
| Windows mute | Remains muted through monitor-volume updates; unmute still works normally |
| Per-app volume | Individual app settings remain unchanged |
| Switch to headphones | Monitor sync suspends; headphone level is not copied from the Dell |
| Switch back | Saved pairing is rechecked and resumes from the lower current value |
| Pause / Exit | No further sync writes; normal Windows playback continues |
| Sleep / wake / cable reconnect | Pending operations are canceled; live handles are recreated; stale requests do not alter a new output |
| Different input / port | Re-pair if Windows creates a new endpoint; do not assume matching friendly names prove identity |
| Disable DDC/CI | Failure is shown and sync pauses; settings window stays responsive |
| Brightness in SDR | App slider changes actual backlight and confirms readback |
| Brightness in HDR | Record limitations; do not treat Windows SDR-content brightness as physical backlight control |
| Multi-display / clone | Only explicitly selected, unambiguous targets can be enabled |
| Worker/app termination | No audio interruption or forced volume reset; helper exits with its owner |
| High DPI, keyboard, screen reader | Setup, selection, status, tray and closing behavior are usable |

A successful DDC write reply is not sufficient: check the monitor's displayed value and audible/visible behavior. Measure the useful quiet-to-loud range before deciding whether a different mapping is needed. Exclusive-mode playback can have a different gain response from shared-mode playback.

Native Windows brightness support remains a separate unresolved requirement. Passing the app's brightness test does not satisfy it.

## Windows installer and distribution

1. Run `scripts/build.ps1` on Windows; require WiX validation to pass without suppressing ICE checks.
2. Install as a standard user on a clean Windows 11 machine with no separate .NET runtime. Verify the Start-menu entry, app/worker launch, and optional startup behavior.
3. Install a higher-version MSI while the app is closed, then repeat while it is open. Verify Restart Manager/files-in-use behavior, a single installed-product entry, settings retention, and startup preference retention.
4. Test repair, canceled installation, rollback, downgrade rejection, and ordinary uninstall. Verify installed files, shortcut, and startup entry are removed; user settings/logs remain.
5. Enable startup, sign out/in, and verify exactly one tray instance. Exiting the app must not prevent Windows sign-out or shutdown.
6. With a real signing identity, run `-RequireSigned`. Verify all installed EXE/DLL and MSI signatures. Test an actual browser download with normal Windows protections enabled.
7. Test a Store install separately if a listing is approved. Signing, direct-download reputation, and Store acceptance are distinct outcomes.

None of these installer/distribution checks has been completed in this environment. Do not describe the preview as warning-free or production-ready.
