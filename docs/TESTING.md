# Preview validation

## Verified in the macOS build environment

- .NET SDK 10.0.401 downloaded from Microsoft's release metadata and checked against its published SHA-512.
- Release solution build: zero warnings and zero errors, with warnings treated as errors.
- All 22 deterministic synchronization tests pass. Cases cover initial alignment, coalescing and held keys, external changes, mute preservation, stale operations, delayed confirmation, write failures, quantization, cancellation, and output switching.
- Self-contained Windows x64 application and worker publication.

These checks do not execute Windows COM, DDC, WPF, MSI installation, or the Dell hardware. WiX was attempted locally: it explicitly reports Windows-only support and rejects ordinary directory names on macOS, so the MSI build is blocked here. The Windows workflow is supplied but has not been run. ARM64 remains untested.

## Dell S2725QS acceptance

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
