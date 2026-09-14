# Monitor Sync for Windows 11

Design and implementation status, 14 September 2026. The app runs in the tray with automatic hardware volume control and direct DDC brightness keys. **Brightness**, **Volume**, and **Start with Windows** default to on and are remembered independently. The app has no brightness or volume overlay. MSI installation, sustained hardware reliability, and signing remain outstanding. See [README](README.md) and [validation](docs/TESTING.md).

No custom driver will be developed. Use an ordinary application and the existing Windows monitor/audio APIs. Windows and the selected monitor keep matching volume percentages. Brightness bypasses Windows brightness integration, as requested, and controls the monitor directly.

## Requirements

- Windows handles volume keys and displays its normal volume UI; changes are mirrored to monitor volume with no app volume overlay. Brightness media keys control the screen under the cursor without an app overlay; fixed shortcuts remain a fallback.
- Windows and monitor percentages match after DDC confirmation. New input takes precedence over older readback; ordinary Windows volume remains functional when DDC is unavailable.
- No custom audio or display driver, virtual audio routing, or audio-processing bridge.
- Monitor-button volume changes update Windows and the app after readback. Brightness reads the current hardware value at the start of each key burst.
- Initial hardware: Dell S2725QS over HDMI, with DisplayPort available for testing. Audio over the selected video connection is the working assumption pending endpoint discovery.
- Design the monitor transport for HDMI and DisplayPort; expand compatibility only when each complete connection path is verified.
- Easy MSI installation with a verified publisher and a distribution strategy that avoids alarming security warnings.
- No settings window. The tray contains **Brightness** then **Volume**, each with an independent Active checkbox beside its name, followed by **Start with Windows** and **Exit**. Unavailable or inactive sliders remain visible and disabled, without a status row; their checkboxes remain usable. All three preferences default to on. Diagnostics remain available through the command line.

## Which monitor to control

- **Volume:** use the currently selected Windows playback output. If it is a monitor's audio endpoint, sync only with that monitor. The mouse position and primary-display setting do not affect audio targeting. Headphones and other non-monitor outputs receive no monitor synchronization.
- **Brightness:** use the screen containing the mouse cursor when a brightness adjustment is received. This target is independent of the playback output and can be a different monitor. Moving the cursor alone must not copy a brightness value between screens or change either screen's brightness.
- **No fallback:** if the intended monitor cannot be identified unambiguously or does not support the required control, perform no synchronization writes. Do not substitute the primary screen, the audio monitor, the last working monitor, or another controllable display. Availability of another monitor is not a reason to control it.

Recheck the target before applying a queued operation. An audio-route change, a cursor-screen change during a pending brightness adjustment, or a display-topology change invalidates pending work for the old target. Readback from an old target must not overwrite the new target's tray readout. Brightness does not write to any native Windows brightness control.

Volume follows this policy through the current default-endpoint and monitor-matching implementation, subject to the naming limitations documented below. Brightness resolves the cursor's logical display to a device path and requires exactly one physical monitor. Both the application and worker check that target before applying brightness.

## Feasibility assessment

| Area | Evidence | Design consequence |
| --- | --- | --- |
| Hardware brightness and volume | Windows has DDC/CI APIs; the Dell manual documents DDC/CI and monitor controls | Probe actual feature support and readback over each connection |
| Windows endpoint gain | The native slider and endpoint gain represent the same value | Match Windows and monitor percentages, accepting attenuation in both stages |
| Native Windows brightness | This desktop exposes no supported WMI brightness control | Bypass it with direct DDC controls and tray feedback |
| MSI | Standard Windows Installer packaging is available | Bundle dependencies and support upgrade/uninstall |
| Warning-free direct download | Signing alone does not guarantee SmartScreen reputation | Prefer a Store installation path; keep the signed MSI downloadable |

MonitorControl uses DDC/CI on supported external displays, and also offers other hardware protocols and software dimming. Its behavior is not universally “all software values at 100%.” The useful goal here is one hardware control represented by the operating system. [MonitorControl](https://github.com/MonitorControl/MonitorControl)

## Dell S2725QS baseline

Dell's manual lists two HDMI inputs, one DisplayPort input, integrated speakers, a 0–100 speaker-volume control, and a DDC/CI setting under Others. It does not establish reliable support for every DDC feature on every input. Keep brightness, volume, mute, and readback as separate probe results.

The manual also states that manual brightness/contrast adjustment is unavailable when Smart HDR is active and HDR content is displayed. Begin hardware brightness acceptance in SDR. An HDR limitation must be surfaced honestly; changing Windows SDR-content brightness is a different operation. [Dell S2725QS user guide, manufacturer document mirrored by Device.report](https://device.report/m/fddc2bf0d49c623147f5d24edc666ca98fd9f670c968ff870157cbb205763c49)

Support means the entire PC/GPU-driver/cable/adapter/monitor combination passes. Start with direct HDMI, repeat with direct DP, and later test docks and USB-C-to-DP paths. Merely changing the connector does not solve Windows' native-slider integration.

## Matching Windows and monitor volume

Windows handles volume and mute keys normally; the app installs no volume keyboard hook. Windows endpoint changes become absolute DDC volume requests. The tray slider updates Windows immediately and queues the same monitor percentage. The app creates no volume overlay; Windows and the monitor may provide their usual indicators. Confirmed monitor volume remains available in the tray slider. Sync does not change mute or individual application/session volumes.

The native Windows slider and its endpoint gain are the same control. The selected behavior keeps Windows and monitor percentages equal instead of pinning Windows to maximum. Windows input takes effect immediately; DDC readback must match the requested raw value on the same route and range before settling the tray or reflecting hardware quantization back into Windows. A failed or stale readback does not roll Windows back. Compare-before-set checks preserve newer volume, mute, and output changes; our own endpoint updates never generate a DDC feedback loop. [Endpoint volume controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls)

On discovery, read live values and align to the lower current percentage. If Windows is higher, lower it to the verified monitor reading; if the monitor is higher, write and confirm the Windows percentage through DDC. Equal levels require no write. A Windows change during discovery overrides the initial alignment. This also migrates the previous Windows-at-100% mode without raising the monitor to maximum. Both stages now attenuate audio, so the same number can sound quieter than with Windows at maximum or another computer using only monitor gain. The monitor's gain curve is not calibrated, and no acoustic equivalence is claimed.

Core Audio default-endpoint notifications immediately invalidate tray control on a route change; the controller and worker also check the actual default endpoint before device operations. During DDC failure or suspension, native Windows volume continues working. No headphone endpoint is assigned the monitor's volume.

The volume engine coalesces requests over 20 ms and checks readback after 200 ms, with at most three confirmation reads. New Windows/tray input supersedes delayed writes and readback. Idle polls every five seconds reflect monitor-button changes into Windows and the tray, unless newer input invalidated the snapshot. Cancellation, output changes, and feature-range changes invalidate the current connection.

## Fluent tray menu

The menu labels are **Brightness** and **Volume**, in alphabetical order, each with a percentage readout. It does not show Windows gain, a status row, or an instruction to use special brightness keys. With a non-monitor audio output, the volume slider is disabled. Each slider stays visible and is enabled only when its own checkbox is checked and the intended target has a valid reading and is controllable; brightness availability is independent of audio selection. A detected DDC failure dims the affected name and slider and clears its percentage. The checkbox remains enabled because it represents the user's preference, not hardware availability. Volume explicitly publishes loss of availability after clearing a failed connection.

The UI uses a WPF `ContextMenu` with standard Fluent command rows and separators. The slider rows embed standard WPF sliders and checkboxes without a command highlight. **Start with Windows** is a checkable menu item; **Exit** is an ordinary menu command. `ThemeMode="System"` on the application follows Windows' app light/dark preference and system accent colours. The standalone menu explicitly uses the framework's ContextMenu style and shares the application's live resource dictionary so theme changes reach it without an owning Window. The Fluent slider's disabled appearance is additionally dimmed. Checked/unchecked events support mouse, keyboard, and UI Automation toggles, while guarded preference refreshes generate no setting changes. If a focused slider becomes unavailable, focus moves to its checkbox to keep the menu usable. [WPF Fluent theme](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)

The same compact menu opens from left or right tray clicks, without creating a settings window or taskbar/Alt+Tab entry. WPF owns popup placement, DPI handling, focus, and outside-click/Escape dismissal; the tray-only app activates the popup's HWND to receive keyboard input. Alt+F4 also dismisses the menu; Exit stops the app. Sliders and checkable commands keep the menu open while being used. Live readback supplies the initial value. During dragging and after release, the thumb and readout retain the requested value throughout the queued request, DDC write, and confirmation read. Both engines count an in-flight write as pending, so releasing mouse capture cannot expose an old reading as settled. Successful confirmation supplies the final hardware value, including quantization; a failed or canceled operation releases the in-flight state. Programmatic refresh never generates a volume or brightness request.

While brightness is active, opening the menu probes it without writing, and idle brightness is refreshed every two seconds while the menu is open. Availability checks run while either control is active, including when only volume is enabled; menu polling stops on close or when both are unchecked. A cursor-target change invalidates the old brightness row and queued work. Key input and absolute slider input share one brightness engine, preserving order and superseding stale probes/readback. Neither keyboard nor slider adjustments create an app overlay. The volume slider updates Windows immediately while retaining its requested position through DDC confirmation.

## Direct DDC brightness

Windows' system brightness integration is provided through `Monitor.sys`, graphics-driver brightness interfaces, and/or ACPI. Microsoft documents the operating system's brightness slider using that integration. [Integrated-panel architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/supporting-brightness-controls-on-integrated-display-panels)

Microsoft also documents a `BrightnessControl` override for internal panels wired through external connectors. It designates one target, still requires OEM brightness implementation integrated with the graphics driver, and does not provide general independent brightness for multiple external displays. A monitor INF or registry edit alone is not a DDC-to-slider adapter. [External-connector requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/supporting-brightness-controls-for-external-display-connectors)

Twinkle Tray's project documentation likewise reports no official API for modifying the Windows Quick Settings flyout. A Windows-looking popup is therefore not evidence of native integration. [Twinkle Tray integration FAQ](https://github.com/xanderfrangos/twinkle-tray/wiki/Common-requests-%26-FAQs#integrating-with-the-windows-quick-settings-flyout)

The target desktop reports `Not supported` for `WmiMonitorBrightness` and `WmiMonitorBrightnessMethods`. Brightness therefore uses direct monitor control with feedback in the tray. A laptop panel's native slider is not forwarded to an external monitor, and no native Quick Settings integration is claimed.

**Screen-brightness up/down media keys** change brightness by 5%; **Ctrl+Alt+Page Up / Page Down** remains a fallback. A key burst reads the current brightness, applies ordered steps with 0–100% clamping, coalesces pending writes for 20 ms, and confirms changes after at least 200 ms. Held keys keep making progress. The tray reports confirmed hardware values after adjustment; failed or unconfirmed operations clear the reading and disable the slider. It does not retry a failed write automatically or restore saved brightness at launch.

Media-key input uses background Raw Input and Windows' descriptor-based HID parser. It supports Consumer display brightness (`0C:6F/70`), Apple Vendor Keyboard (`FF01:20/21`), and Apple Top Case (`00FF:04/05`). Apple vendor-page interpretation requires vendor ID `05AC`; keyboard illumination, Fn, and ordinary F1/F2 are excluded. [USB definitions](https://www.usb.org/sites/default/files/hut1_21_0.pdf), [Apple usage definitions in the upstream HID library](https://github.com/pqrs-org/cpp-hid/blob/main/include/pqrs/hid/usage.hpp), [usage pages](https://github.com/pqrs-org/cpp-hid/blob/main/include/pqrs/hid/usage_page.hpp)

Registrations cover every top-level collection on the Consumer and Apple pages. Other HID collections that declare supported brightness controls are discovered at startup and every five seconds. The parser reads button arrays, individual buttons, and scalar value fields through `HidP_GetUsages`/`HidP_GetUsageValue`, including multiple report IDs. Relative value reports produce pulses rather than stuck held keys. State is tracked per device and report ID; held keys repeat after 400 ms and then every 100 ms. Removal, suspend, and topology changes clear repeats. Diagnostics report declared controls without storing typing or input history. [Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input), [HID parsing](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getusages)

Raw Input observes reports and does not suppress native/OEM brightness handling. Magic Keyboard and Boot Camp support requires the driver to expose a supported report. Fn consumed inside firmware, keyboard-stack-only vendor fields, or ordinary F1/F2 translations cannot be distinguished automatically. No filter driver is installed and no ordinary function key is hijacked. Physical Apple USB/Bluetooth and laptop-panel validation remain outstanding.

The user observed that Windows already shows a brightness indicator on this desktop even without the app, although it does not control the monitor. The app's additional indicator has been removed to avoid duplicate overlays. Native/OEM brightness UI remains untouched and is not driven by DDC readback; the confirmed monitor value is available in the tray. The tray remains the only place for configuration.

Brightness and volume share one serialized DDC worker. Brightness acquires a fresh physical handle for each operation, checks the cursor's monitor after queue waits and immediately before writing, and releases the handle afterward. Failed reads have up to three attempts with fresh handles, 500 ms apart, inside the worker's existing deadline; each attempt rechecks the target. Moving screens discards pending input and readback without restarting the shared worker. No fixed-target setting, linked display group, or fallback is used.

## Shared monitor-control engine

Use physical-monitor enumeration and `Dxva2.dll`. Read with `GetVCPFeatureAndVCPFeatureReply`, write with `SetVCPFeature`. The documented typical call times are about 40 ms for reads and 50 ms for writes, so work must be queued and coalesced. [Read API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply), [write API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)

Probe brightness (`0x10`) and speaker volume (`0x62`). Investigate mute (`0x8D`) separately because encoding varies with MCCS version and can include screen-blanking semantics. Do not send guessed mute values. [Feature-code reference](https://www.ddcutil.com/vcpinfo_output/)

DDC reads have up to three attempts, 500 ms apart. A reported write failure is checked by readback before declaring failure; an uncertain write is never repeated. Volume requests carry the expected audio endpoint ID and recheck it in the worker after queue waits, read retries, and immediately before writing. Keep native DDC calls in a restartable worker process, separate from audio notifications and UI. Process isolation can contain a hung user-mode call; it cannot protect against a kernel driver fault. Serialize operations initially across the worker, and re-enumerate handles after restarting it.

The coordinator discovers a fresh connection automatically. It requires an HDMI/DisplayPort audio endpoint and matches the driver's endpoint description to exactly one enumerated monitor model, ignoring connector suffixes and Windows' numeric prefixes. Duplicate models are rejected even if only one reports readable volume. This naming heuristic supports the initial Dell setup; it is not a hardware identity guarantee and cannot resolve every driver or multi-display topology. Missing, mismatched, or ambiguous descriptions leave the app waiting without adding a pairing setting. Once selected, the live endpoint ID and monitor device path identify the current connection. [Display audio form factor](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-formfactor), [device properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)

Synchronization rules:

1. Read live values on discovery. Do not apply a stale saved profile at startup.
2. Normalize against verified feature ranges. Coalesce rapid requests to the newest value over 20 ms. Both control loops check pending work every 20 ms; scheduling and serialized DDC I/O add latency, so this is not a guarantee of 50 hardware updates per second.
3. Read back after settling. Publish the value actually applied, including clamping, without confusing it with newer pending intent.
4. Poll slowly when idle, initially about every five seconds. Failed or unconfirmed operations end the current connection; automatic discovery retries after one second. Readback is eventual, not instantaneous.
5. Observed monitor changes update Windows and the tray slider without generating a new hardware command. Tag origin/revision to prevent feedback loops.
6. Discard work from old connections. Re-enumerate after sleep, display changes, or audio-route changes.
7. Mark unreadable or unavailable controls as such. Cached values must not be presented as verified hardware state.

## Audio lifecycle and recovery

Only manage the default playback endpoint when active and when it is display audio with one matching monitor. Switching to headphones suspends monitor control. The app checks the playback route every two seconds while waiting, and rebuilds the connection after a route change. Initial connection and recovery align to the lower live percentage unless newer Windows input supersedes discovery. No saved pairing or hardware level is used.

Brightness and volume activation are separate per-user registry preferences, independent of startup. Each falls back to the legacy global Active value until explicitly set, preserving an existing opt-out during upgrade. Turning a feature off immediately detaches its controller and keyboard inputs and cancels its work; brightness also stops HID discovery/repeat and shortcuts. The other feature retains its controller and inputs. The shared DDC client is released after both are off and their pending work has drained. Checkboxes remain usable during cancellation; reconciliation uses the latest choices before starting replacement controllers, so rapid toggles cannot replay canceled input or restart an unchecked feature. Current levels remain in place, and an already-issued hardware command cannot be recalled. Opening the menu, display changes, and resume do not restart unchecked features. Re-enabling starts from fresh readings. Sliders stay enabled during normal adjustment so continuous dragging remains possible.

Preserve ordinary Windows mute behavior. Hardware mute, if separately offered, needs verified monitor-specific support. The initial volume mapping applies to ordinary shared-mode playback; exclusive-mode playback can bypass Windows software attenuation, changing the effective response. [Shared and exclusive audio controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls)

If a DDC operation fails, keep Windows volume functional, disable the affected tray slider, and retry with a fresh connection while active. Sleep cancels outstanding work until resume. A single asynchronous loop owns connection discovery and sync, preventing overlapping reconnect operations. On exit or a crash, ordinary Windows audio continues because no audio stream is routed through our process. Do not restore stale values or force monitor volume to maximum during recovery.

## Components and packaging

The implementation uses C# with a WPF dispatcher, a Fluent context menu, a Windows Forms notification icon, Core Audio callbacks for change revisions, and an isolated DDC worker. WinForms no longer draws the menu or its controls. The SDK is pinned to .NET 10.0.401 and the runtime is bundled; the Fluent theme ships in that desktop runtime and adds no third-party UI dependency. No custom driver, virtual audio device, audio bridge, or administrative service is part of this design. [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

Build a per-user MSI with WiX, bundle all runtime dependencies, and target normal installation without elevation on an unmanaged Windows 11 PC. Startup is enabled on a fresh installation or first portable launch. An empty startup registry value records an explicit opt-out; initialization, upgrade, and repair preserve it. Enabled startup entries refresh to the current executable path, and MSI ownership removes the entry on uninstall. WiX's current release policy includes a maintenance fee for revenue-generating use. [Installation contexts](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context), [WiX](https://docs.firegiant.com/wix/)

Sign and timestamp application payloads, verify dependency signatures, assemble the MSI, then sign and timestamp it. No custom kernel driver means no driver-submission or driver-certification dependency. Choose the application signing provider after establishing publisher eligibility.

A fresh signed download may still receive SmartScreen warnings. Microsoft states that Store installation avoids the SmartScreen prompt, including its MSI/EXE path. Keep the signed standalone MSI and investigate an accepted Store listing as the consumer distribution path. [SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation), [signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

For Store MSI submissions, provide signed installer/PE payloads, immutable versioned HTTPS URLs, silent installation support, and all dependencies. Store acceptance is a release dependency; the Store does not re-sign the MSI. Do not require users to disable security protections or install a custom root certificate. [Store MSI requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)

## Work order and acceptance

1. **Read-only probe:** identify Windows build, GPU/driver, built-in-panel state, DDC ranges, current audio routing, and existing hardware-volume support.
2. **Controlled Dell test:** verify reversible brightness/volume writes and monitor-button readback in SDR over HDMI, then DP. Restore test values. Record HDR behavior separately.
3. **Direct brightness:** verify shortcuts, confirmed tray readback, absence of an app overlay, and cursor targeting. Unsupported or ambiguous targets must cause no writes to another screen. Repeat on multiple displays and DPI scales.
4. **Matching volume:** verify Windows/tray changes reach the monitor, monitor-button changes reach Windows, mute is preserved by sync, and headphones retain ordinary Windows behavior. Test the useful listening range; do not infer acoustical gain from raw percentages alone.
5. **Recovery and polish:** test feedback suppression and stale operations with a fake transport, then sleep, reconnects, endpoint changes, DDC failure, application exit, accessibility, and multiple displays on Windows.
6. **Release installation:** verify signed per-user installation, upgrade, repair, rollback, uninstall, startup cleanup, and clean-machine behavior with Windows protections enabled.
7. **Distribution:** test the browser-downloaded MSI and accepted Store install separately. Signature verification does not prove reputation or Store acceptance.

No custom driver will be created. Matching Windows/monitor volume and direct DDC brightness are the selected implementation. Remaining validation includes the measured listening response, multiple displays, HDR behavior, and release installation.
