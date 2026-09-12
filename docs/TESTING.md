# Preview validation

## Verified in the macOS build environment

- .NET SDK 10.0.401 downloaded from Microsoft's release metadata and checked against its published SHA-512.
- Release solution build: zero warnings and zero errors, with warnings treated as errors.
- All 22 deterministic synchronization tests pass. Cases cover initial alignment, coalescing and held keys, external changes, mute preservation, stale operations, delayed confirmation, write failures, quantization, cancellation, and output switching.
- Self-contained Windows x64 application and worker publication.

These macOS checks do not execute Windows COM, DDC, WPF, MSI installation, or the Dell hardware. WiX explicitly reports Windows-only support, so the final MSI build runs in [Windows CI](https://github.com/nkatchik/win-monitor-sync/actions/workflows/build.yml). CI builds the application, runs the deterministic tests, builds and validates the MSI, and uploads the packages. Refer to each run for its actual result. Initial Windows hardware results are recorded below; installation and ARM64 remain untested.

Both workflows pass actionlint validation. A local build using `-p:Version=1.2.3` was also inspected: both app and worker assemblies report `1.2.3`. The manual publication workflow is available with a required version input; creating a release is separate from verifying an automatic build.

The [first Windows CI run](https://github.com/nkatchik/win-monitor-sync/actions/runs/34363624736) passed on 9 September 2026: build, tests, MSI packaging, and artifact upload. This verifies package creation, not installation or live DDC behavior.

## Dell S2725QS acceptance

### Original windowed preview debug session, 11 September 2026

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

### Tray-only revision, 11 September 2026

- Debug build: zero warnings and errors; all 30 deterministic tests passed, including eight automatic-selection cases covering display-audio matching, headphones, similar model names, duplicate models, unreadable duplicates, and missing descriptions.
- Live Dell discovery reports `MonitorName: DELL S2725QS` and `IsDisplayAudio: true`. The app starts syncing without saved pairing or an enable action.
- A temporary integration harness ran the production app and verified zero application windows, a visible tray icon, and only **Start with Windows** and **Exit** as interactive menu items. First-run startup default, toggle behavior, and persistence of an explicit opt-out passed; the harness restored the previous startup registry value afterward.
- Simulated suspend/resume, termination of the actual DDC worker, and a controlled failed worker response all recovered automatically. These exercise application lifecycle handling, not a real PC sleep or cable reconnect.
- The debug script publishes both executable hosts under `artifacts/debug/app` with the local SDK's relative location. Direct launch with the original Windows environment succeeded; duplicate launch exited silently. The actual tray app was left running with startup enabled and live Windows/monitor volumes both at 30%.
- Installer XML was checked for the quoted, enabled-by-default startup command. Installation, upgrade/repair behavior, and an actual sign-out/sign-in remain unverified for this revision.

Local integration traces and the final read-only report are under ignored `artifacts/debug`.

### Direct brightness revision, 12 September 2026

- Debug build: zero warnings and errors; all 43 deterministic tests passed. Thirteen brightness cases cover fresh readings, clamping, held input, delayed write/readback, target changes, cancellation, failed confirmation, quantization, and changed ranges.
- The production tray app registered both brightness shortcuts and still exposed only **Start with Windows** and **Exit** as interactive tray items. No window existed at startup.
- A temporary integration harness sent `WM_HOTKEY` to the registered handler. The real worker changed Dell brightness **100 → 95 → 100**, confirmed both values through DDC, and preserved Windows volume/mute. This passed both with headphones selected and with Dell audio selected; neither test changed the selected playback device.
- An incorrect monitor ID was rejected without changing brightness or restarting the shared worker. Simulated suspend suppressed brightness requests and hid the indicator; exit closed the app and worker cleanly.
- At 150% scaling on the 3840×2160 Dell, the indicator's 510×144 physical-pixel window was centred within the work area. The foreground window was unchanged, and automatic dismissal passed. The dark-theme WPF rendering was inspected for layout, icon, bar, and text.
- Intermittent DDC errors (`0xC0262589`) occurred with both cached and fresh handles. Reopening and retrying reads after 500 ms recovered during the completed checks; writes remain single-attempt. This does not establish sustained reliability.
- Synthetic `SendInput` key injection made cursor capture unavailable and did not deliver the shortcut in this session. The successful checks used the registered hotkey message handler directly. Physical keyboard operation, cross-monitor movement, mixed DPI, high contrast, screen readers, HDR, and DisplayPort remain interactive acceptance items.

Brightness and the startup registry entry were restored after the harness. Traces and the rendered indicator are under ignored `artifacts/debug`.

### Brightness media-key revision, 12 September 2026

- Debug build passed with zero warnings and errors; **49/49** deterministic tests passed. Six additional cases cover initial press, release, repeat timing, duplicate reports, independent report IDs, conflicting directions, device removal, and delayed dispatch.
- Read-only HID discovery found Consumer Control report ranges including display-brightness up/down on the Logitech receiver and two Razer collections. A report range alone does not prove that a physical key sends a particular usage.
- The production HID parser was tested with Windows-generated reports constructed only in memory from those three live descriptors. Brightness up, down, and release decoded correctly. Volume-up and keyboard-backlight usages did not trigger brightness; malformed report lengths were ignored. No keyboard input was injected and no device state was changed.
- Background Consumer Control registration and disposal cleanup passed. The successful integration trace is in ignored `artifacts/debug/brightness-media-smoke.log`.
- Physical brightness-key delivery, OEM/Fn behavior, and interaction with native laptop-panel brightness remain unverified. The existing shortcut remains a fallback.

### Hardware volume and expanded HID revision, 12 September 2026

- The previous app log showed DDC invalid-command, checksum, and I²C transmission errors (`0xC0262589`, `0xC026258B`, `0xC0262582`). Each failure previously disconnected volume sync for ten seconds. Reads now retry up to three times, 500 ms apart; failed connections retry after one second. A reported failed write is checked by readback, never blindly repeated.
- Debug build passed with zero warnings/errors; **53/53 deterministic tests passed**. The suite covers hardware gain at Windows 100%, confirmation before pinning, coalesced keys, mute, newer input during DDC I/O, delayed callback revisions, cancellation, output changes, failed readback, and quantization. The obsolete equal-percentage engine and its tests were replaced.
- A temporary harness ran the production tray app and volume-key callback against the real Dell. Windows adopted **100%**, monitor volume followed **34 → 32 → 34**, mute toggled and restored, and native endpoint requests **30 → 34** reached hardware before Windows returned to 100%. Wrong-output writes were rejected inside the worker without changing the monitor or restarting the shared worker. Simulated suspend released volume keys; resume rediscovered the monitor.
- The volume indicator displayed confirmed hardware volume, remained on the audio monitor without taking focus, and dismissed automatically. Its 150%-scale dark rendering was inspected. No window appeared at startup; only **Start with Windows** and **Exit** remained interactive in the tray. Test cleanup restored the original Windows/monitor volumes and mute.
- Terminating the actual DDC worker during idle control caused automatic worker recreation and live rediscovery. Windows remained at 100% and the Dell retained 34%; no stale volume was restored.
- Native HID report tests passed against three connected Consumer descriptors, including brightness up/down/release, unrelated volume/backlight usages, and malformed lengths. Page-wide Raw Input registration and cleanup passed. Apple Vendor Keyboard and Top Case usage mappings, including the Apple vendor-ID restriction, are covered by deterministic tests.
- No Apple keyboard was connected. Physical Magic Keyboard USB/Bluetooth, Boot Camp translations, scalar/relative HID fields on real keyboards, actual volume-key delivery, headphone switching, and long-duration DDC reliability remain interactive acceptance items. The volume test invoked the installed hook's callback directly; it did not inject or physically press a key.

Reports and traces are in ignored `artifacts/debug`.

### Volume overlay removal, 12 September 2026

- Removed the app's volume overlay and the volume-specific display lookup. Brightness retains its existing indicator; confirmed volume remains in tray status.
- Debug build passed with zero warnings/errors; **55/55 tests passed**. Added regression cases proving that a successful DDC set with stale readback cannot raise Windows gain, and that readback failure after a successful set preserves the Windows volume request. The existing readback guard was retained.
- The attempted live Dell check stopped before changing any device values because a non-monitor output was selected. A separate production-app check verified no app window or overlay, volume/mute requests passing through to Windows, and the selected Steam Streaming Microphone endpoint remaining at 60%, unmuted. Its startup preference was restored afterward.
- Live Dell volume-key testing without the overlay remains an interactive check. Earlier Dell write/readback results above still describe the unchanged volume engine; they do not establish whether a monitor displays its own OSD for DDC changes.

### Tray sliders and wording, 12 September 2026

- Debug build passed with zero warnings/errors; **64/64 deterministic tests passed**. Nine new cases cover absolute volume/brightness requests, mixed slider/key input, coalesced drags, unchanged values, changed targets, and new input during volume confirmation without premature Windows gain changes.
- The production tray menu showed **Volume 2%** and **Brightness 100%**, with the former Windows-gain suffix and vague brightness-key hint removed. Slider availability followed the selected audio route and cursor brightness support. Opening the menu preserved brightness and audio values.
- The volume slider changed Dell volume **2 → 0 → 2** while Windows stayed at 100%. Native brightness-slider arrow input changed **100 → 95**; mouse dragging also produced confirmed DDC readback, and the slider restored brightness to **100**. No app volume or brightness overlay was created.
- Mouse capture and arrow-key input kept the menu open. Availability updates hid the brightness control on simulated suspend and restored it on resume. The menu closed normally and stayed inside the monitor work area while rows changed. The 150%-DPI native-control layout and bounds were inspected. Traces and render captures are under ignored `artifacts/debug`.
- Cross-monitor movement, mixed DPI, screen-reader navigation, and physical monitor OSD behavior remain interactive acceptance items. Tests sent messages to the native trackbar control; no global key injection or audio-route switch was performed.

### Remaining interactive checks

Record the Windows build, GPU model/driver, connector/cable, active monitor input, audio endpoint, HDR state, and a diagnostic report for each run. Use direct HDMI first, then direct DisplayPort. Keep the initial listening level comfortable; compare percentages separately from perceived loudness.

| Check | Expected result |
| --- | --- |
| Command-line diagnostics | Reports live Windows and monitor values; changes neither values nor startup preference |
| Windows volume change with the app exited | Establish whether monitor OSD already follows it; if it does, investigate existing hardware integration before adding duplicate control |
| Launch with unequal values | Confirm the lower monitor setting before restoring Windows to 100%; if Windows is already 100%, preserve live monitor volume |
| Volume media keys | Monitor changes by 2%, Windows stays at 100%, and the app creates no volume overlay; confirmed volume appears in tray status |
| Tray sliders | Available controls appear with plain percentage labels; dragging/arrow keys adjust the intended monitor, the menu stays open, and opening/refreshing causes no writes |
| Quick Settings | A new endpoint percentage is applied to hardware and Windows returns to 100% after confirmation; no reset feedback loop |
| Drag slider and hold volume key | Values make progress during continuous input; no feedback oscillation or late rollback |
| Dell volume buttons | Tray monitor level follows within approximately five seconds while Windows stays at 100% |
| Windows mute | Remains muted through monitor-volume updates; unmute still works normally |
| Per-app volume | Individual app settings remain unchanged |
| Switch to headphones | Monitor sync suspends; headphone level is not copied from the Dell |
| Switch back | Monitor is rediscovered automatically; Windows returns to 100% only after adoption succeeds |
| Exit | No further sync writes; normal Windows playback continues |
| Sleep / wake / cable reconnect | Pending operations are canceled; live handles are recreated; stale requests do not alter a new output |
| Different input / port | New endpoint and monitor are discovered automatically when descriptions match uniquely |
| Disable/re-enable DDC/CI | Tray shows unavailability and automatic retry recovers; Windows audio remains usable |
| Brightness in SDR | Screen-brightness media keys and fallback shortcuts change only the cursor's screen by 5%; confirm visible backlight change and monitor OSD readback |
| Brightness in HDR | Record limitations; do not treat Windows SDR-content brightness as physical backlight control |
| Multi-display / clone | Duplicate model names and ambiguous physical mappings leave sync waiting |
| Audio output on display A, cursor on display B | Volume sync controls only A; cursor position does not redirect audio |
| Selected audio monitor unavailable while another supports DDC | Neither that other monitor nor the Windows endpoint is changed by sync |
| Worker/app termination | No audio interruption or forced volume reset; helper exits with its owner |
| Brightness indicator | Resembles Windows in light/dark/high-contrast modes, remains readable at each DPI, takes no focus, and dismisses automatically |
| High DPI, keyboard, screen reader | Tray status, startup checkbox, and Exit are usable; shortcut conflicts are reported; brightness announcements are usable |

A successful DDC write reply is not sufficient: check the monitor's displayed value and audible/visible behavior. Measure the useful quiet-to-loud range before deciding whether a different mapping is needed. Exclusive-mode playback can have a different gain response from shared-mode playback.

Brightness now bypasses the native Windows slider by design. It uses DDC directly and provides its own temporary indicator.

On multiple displays, verify that brightness targets only the screen under the cursor, even when audio plays through another monitor. Moving the cursor alone must not change brightness. An unsupported or ambiguous cursor target must produce no writes to any screen. Crossing to another screen or changing display topology during a pending adjustment must discard stale commands and readback. These remain hardware acceptance requirements; the corresponding engine guards are covered by deterministic tests.

## Windows installer and distribution

1. Run `scripts/build.ps1` on Windows; require WiX validation to pass without suppressing ICE checks.
2. Install as a standard user on a clean Windows 11 machine with no separate .NET runtime. Verify the Start-menu entry, app/worker launch, and optional startup behavior.
3. Install a higher-version MSI while the app is closed, then repeat while it is open. Verify Restart Manager/files-in-use behavior, a single installed-product entry, settings retention, and startup preference retention.
4. Test repair, canceled installation, rollback, downgrade rejection, and ordinary uninstall. Verify installed files, shortcut, and startup entry are removed; user settings/logs remain.
5. Verify startup is enabled by default, sign out/in, and check for exactly one tray instance and no app window. Disable startup through the tray and confirm the choice survives relaunch and upgrade. Exiting the app must not prevent Windows sign-out or shutdown.
6. With a real signing identity, run `-RequireSigned`. Verify all installed EXE/DLL and MSI signatures. Test an actual browser download with normal Windows protections enabled.
7. Test a Store install separately if a listing is approved. Signing, direct-download reputation, and Store acceptance are distinct outcomes.

None of these installer/distribution checks has been completed in this environment. Do not describe the preview as warning-free or production-ready.
