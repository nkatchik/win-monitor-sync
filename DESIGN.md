# Monitor Sync for Windows 11

Design and implementation status, 12 September 2026. The app runs in the tray with automatic hardware volume control and direct DDC brightness keys. Its only setting is **Start with Windows**, enabled by default. Volume and brightness use temporary Windows-style indicators. MSI installation, sustained hardware reliability, and signing remain outstanding. See [README](README.md) and [validation](docs/TESTING.md).

No custom driver will be developed. Use an ordinary application and the existing Windows monitor/audio APIs. Volume keys control monitor gain while the selected monitor endpoint stays at 100%. Brightness bypasses Windows brightness integration, as requested, and controls the monitor directly.

## Requirements

- Volume keys control monitor volume, with Windows at 100% and a Windows-style indicator. Brightness media keys control the screen under the cursor and show a Windows-style indicator; fixed shortcuts remain a fallback.
- Monitor volume is authoritative. Raise Windows gain only after confirming control of the selected monitor; leave ordinary Windows volume functional when DDC is unavailable.
- No custom audio or display driver, virtual audio routing, or audio-processing bridge.
- Monitor-button volume changes update the app after readback; Windows remains at 100%. Brightness reads the current hardware value at the start of each key burst.
- Initial hardware: Dell S2725QS over HDMI, with DisplayPort available for testing. Audio over the selected video connection is the working assumption pending endpoint discovery.
- Design the monitor transport for HDMI and DisplayPort; expand compatibility only when each complete connection path is verified.
- Easy MSI installation with a verified publisher and a distribution strategy that avoids alarming security warnings.
- No settings window. The tray contains status, a brightness shortcut reminder, **Start with Windows** (on by default), and **Exit**. Everything supported is always enabled; there is no pairing or pause configuration. Diagnostics remain available through the command line.

## Which monitor to control

- **Volume:** use the currently selected Windows playback output. If it is a monitor's audio endpoint, sync only with that monitor. The mouse position and primary-display setting do not affect audio targeting. Headphones and other non-monitor outputs receive no monitor synchronization.
- **Brightness:** use the screen containing the mouse cursor when a brightness adjustment is received. This target is independent of the playback output and can be a different monitor. Moving the cursor alone must not copy a brightness value between screens or change either screen's brightness.
- **No fallback:** if the intended monitor cannot be identified unambiguously or does not support the required control, perform no synchronization writes. Do not substitute the primary screen, the audio monitor, the last working monitor, or another controllable display. Availability of another monitor is not a reason to control it.

Recheck the target before applying a queued operation. An audio-route change, a cursor-screen change during a pending brightness adjustment, or a display-topology change invalidates pending work for the old target. Readback from an old target must not overwrite the new target's indicator. Brightness does not write to any native Windows brightness control.

Volume follows this policy through the current default-endpoint and monitor-matching implementation, subject to the naming limitations documented below. Brightness resolves the cursor's logical display to a device path and requires exactly one physical monitor. Both the application and worker check that target before applying brightness.

## Feasibility assessment

| Area | Evidence | Design consequence |
| --- | --- | --- |
| Hardware brightness and volume | Windows has DDC/CI APIs; the Dell manual documents DDC/CI and monitor controls | Probe actual feature support and readback over each connection |
| Windows endpoint gain | The native slider and endpoint gain represent the same value | Keep gain at 100%; display monitor volume in the app indicator |
| Native Windows brightness | This desktop exposes no supported WMI brightness control | Bypass it with direct DDC shortcuts and a Windows-style indicator |
| MSI | Standard Windows Installer packaging is available | Bundle dependencies and support upgrade/uninstall |
| Warning-free direct download | Signing alone does not guarantee SmartScreen reputation | Prefer a Store installation path; keep the signed MSI downloadable |

MonitorControl uses DDC/CI on supported external displays, and also offers other hardware protocols and software dimming. Its behavior is not universally “all software values at 100%.” The useful goal here is one hardware control represented by the operating system. [MonitorControl](https://github.com/MonitorControl/MonitorControl)

## Dell S2725QS baseline

Dell's manual lists two HDMI inputs, one DisplayPort input, integrated speakers, a 0–100 speaker-volume control, and a DDC/CI setting under Others. It does not establish reliable support for every DDC feature on every input. Keep brightness, volume, mute, and readback as separate probe results.

The manual also states that manual brightness/contrast adjustment is unavailable when Smart HDR is active and HDR content is displayed. Begin hardware brightness acceptance in SDR. An HDR limitation must be surfaced honestly; changing Windows SDR-content brightness is a different operation. [Dell S2725QS user guide, manufacturer document mirrored by Device.report](https://device.report/m/fddc2bf0d49c623147f5d24edc666ca98fd9f670c968ff870157cbb205763c49)

Support means the entire PC/GPU-driver/cable/adapter/monitor combination passes. Start with direct HDMI, repeat with direct DP, and later test docks and USB-C-to-DP paths. Merely changing the connector does not solve Windows' native-slider integration.

## Hardware volume with Windows at 100%

The monitor's speaker-volume control is authoritative. Dedicated Windows volume keys (`VK_VOLUME_UP`, `VK_VOLUME_DOWN`, and `VK_VOLUME_MUTE`) are intercepted only for a successfully discovered monitor route. Up/down applies 2% steps through DDC; mute changes the Windows endpoint mute state. A Windows-style indicator appears on the audio monitor, independently of cursor location. Individual application/session volumes remain untouched.

The native Windows slider and its endpoint gain are the same control, so it cannot simultaneously show monitor volume and stay at 100%. The app provides the indicator. An external endpoint change below 100% becomes an absolute monitor request; Windows returns to 100% only after confirmed DDC readback and a compare-before-set check against newer intent. No monitor-volume request is generated by that restoration. [Endpoint volume controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls)

On discovery, read live values. If Windows already equals 100%, preserve current monitor volume. Otherwise write and confirm the lower current percentage before restoring Windows to 100%. Removing Windows attenuation can increase perceived loudness even though the monitor percentage stays the same or decreases. The monitor's gain curve is not calibrated, and no acoustic equivalence is claimed.

The keyboard hook performs no COM, DDC, waits, or UI operations. It only queues intent. Core Audio default-endpoint notifications immediately invalidate its eligibility on a route change; the controller and worker also check the actual default endpoint before device operations. During DDC failure or suspension, keys return to Windows. If the input hook cannot be installed, hardware volume control stays inactive. No headphone endpoint is pinned or assigned the monitor's volume.

The volume engine coalesces requests over 100 ms and checks readback after 200 ms, with at most three confirmation reads. New key/slider input supersedes delayed writes and readback. Idle polls every five seconds update the app's hardware level without changing Windows gain. Cancellation, output changes, and feature-range changes invalidate the current connection.

## Direct brightness with a Windows-style indicator

Windows' system brightness integration is provided through `Monitor.sys`, graphics-driver brightness interfaces, and/or ACPI. Microsoft documents the operating system's brightness slider using that integration. [Integrated-panel architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/supporting-brightness-controls-on-integrated-display-panels)

Microsoft also documents a `BrightnessControl` override for internal panels wired through external connectors. It designates one target, still requires OEM brightness implementation integrated with the graphics driver, and does not provide general independent brightness for multiple external displays. A monitor INF or registry edit alone is not a DDC-to-slider adapter. [External-connector requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/supporting-brightness-controls-for-external-display-connectors)

Twinkle Tray's project documentation likewise reports no official API for modifying the Windows Quick Settings flyout. A Windows-looking popup is therefore not evidence of native integration. [Twinkle Tray integration FAQ](https://github.com/xanderfrangos/twinkle-tray/wiki/Common-requests-%26-FAQs#integrating-with-the-windows-quick-settings-flyout)

The target desktop reports `Not supported` for `WmiMonitorBrightness` and `WmiMonitorBrightnessMethods`. The user therefore chose direct monitor control with on-screen feedback that resembles Windows. A laptop panel's native slider is not forwarded to an external monitor, and no native Quick Settings integration is claimed.

**Screen-brightness up/down media keys** change brightness by 5%; **Ctrl+Alt+Page Up / Page Down** remains a fallback. A key burst reads the current brightness, applies ordered steps with 0–100% clamping, coalesces pending writes for 100 ms, and confirms changes after at least 200 ms. Held keys keep making progress. The indicator displays the last verified value with a subdued appearance while a change is pending; failed or unconfirmed operations show “Brightness unavailable.” It does not retry a failed write automatically or restore saved brightness at launch.

Media-key input uses background Raw Input and Windows' descriptor-based HID parser. It supports Consumer display brightness (`0C:6F/70`), Apple Vendor Keyboard (`FF01:20/21`), and Apple Top Case (`00FF:04/05`). Apple vendor-page interpretation requires vendor ID `05AC`; keyboard illumination, Fn, and ordinary F1/F2 are excluded. [USB definitions](https://www.usb.org/sites/default/files/hut1_21_0.pdf), [Apple usage definitions in the upstream HID library](https://github.com/pqrs-org/cpp-hid/blob/main/include/pqrs/hid/usage.hpp), [usage pages](https://github.com/pqrs-org/cpp-hid/blob/main/include/pqrs/hid/usage_page.hpp)

Registrations cover every top-level collection on the Consumer and Apple pages. Other HID collections that declare supported brightness controls are discovered at startup and every five seconds. The parser reads button arrays, individual buttons, and scalar value fields through `HidP_GetUsages`/`HidP_GetUsageValue`, including multiple report IDs. Relative value reports produce pulses rather than stuck held keys. State is tracked per device and report ID; held keys repeat after 400 ms and then every 100 ms. Removal, suspend, and topology changes clear repeats. Diagnostics report declared controls without storing typing or input history. [Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input), [HID parsing](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getusages)

Raw Input observes reports and does not suppress native/OEM brightness handling. Magic Keyboard and Boot Camp support requires the driver to expose a supported report. Fn consumed inside firmware, keyboard-stack-only vendor fields, or ordinary F1/F2 translations cannot be distinguished automatically. No filter driver is installed and no ordinary function key is hijacked. Physical Apple USB/Bluetooth and laptop-panel validation remain outstanding.

The noninteractive indicator has a sun icon, level bar, percentage, rounded corners, and Windows light/dark colours. It appears at the bottom centre of the target's work area, scales with that display's DPI, and fades after about two seconds. It neither activates nor appears in the taskbar and lets mouse input pass through. High contrast uses system colours and disables the shadow and animation. The tray remains the only place for configuration.

Brightness and volume share one serialized DDC worker. Brightness acquires a fresh physical handle for each operation, checks the cursor's monitor after queue waits and immediately before writing, and releases the handle afterward. Failed reads have up to three attempts with fresh handles, 500 ms apart, inside the worker's existing deadline; each attempt rechecks the target. Moving screens discards pending input and readback without restarting the shared worker. No fixed-target setting, linked display group, or fallback is used.

## Shared monitor-control engine

Use physical-monitor enumeration and `Dxva2.dll`. Read with `GetVCPFeatureAndVCPFeatureReply`, write with `SetVCPFeature`. The documented typical call times are about 40 ms for reads and 50 ms for writes, so work must be queued and coalesced. [Read API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply), [write API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)

Probe brightness (`0x10`) and speaker volume (`0x62`). Investigate mute (`0x8D`) separately because encoding varies with MCCS version and can include screen-blanking semantics. Do not send guessed mute values. [Feature-code reference](https://www.ddcutil.com/vcpinfo_output/)

DDC reads have up to three attempts, 500 ms apart. A reported write failure is checked by readback before declaring failure; an uncertain write is never repeated. Volume requests carry the expected audio endpoint ID and recheck it in the worker after queue waits, read retries, and immediately before writing. Keep native DDC calls in a restartable worker process, separate from audio notifications and UI. Process isolation can contain a hung user-mode call; it cannot protect against a kernel driver fault. Serialize operations initially across the worker, and re-enumerate handles after restarting it.

The coordinator discovers a fresh connection automatically. It requires an HDMI/DisplayPort audio endpoint and matches the driver's endpoint description to exactly one enumerated monitor model, ignoring connector suffixes and Windows' numeric prefixes. Duplicate models are rejected even if only one reports readable volume. This naming heuristic supports the initial Dell setup; it is not a hardware identity guarantee and cannot resolve every driver or multi-display topology. Missing, mismatched, or ambiguous descriptions leave the app waiting without adding a pairing setting. Once selected, the live endpoint ID and monitor device path identify the current connection. [Display audio form factor](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-formfactor), [device properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)

Synchronization rules:

1. Read live values on discovery. Do not apply a stale saved profile at startup.
2. Normalize against verified feature ranges. Coalesce rapid requests to the newest value, with roughly 100 ms as an initial tuning target.
3. Read back after settling. Publish the value actually applied, including clamping, without confusing it with newer pending intent.
4. Poll slowly when idle, initially about every five seconds. Failed or unconfirmed operations end the current connection; automatic discovery retries after one second. Readback is eventual, not instantaneous.
5. Observed monitor changes update the app indicator/status without changing Windows gain or generating a new hardware command. Tag origin/revision to prevent feedback loops.
6. Discard work from old connections. Re-enumerate after sleep, display changes, or audio-route changes.
7. Mark unreadable or unavailable controls as such. Cached values must not be presented as verified hardware state.

## Audio lifecycle and recovery

Only manage the default playback endpoint when it is display audio with one matching monitor. Switching to headphones suspends monitor control. The app checks the playback route every two seconds while waiting, and rebuilds the connection after a route change. Initial connection and recovery adopt live monitor volume; when Windows is below 100%, a confirmed lower monitor setting precedes restoring Windows gain. No saved pairing or enabled/paused state is used.

Preserve ordinary Windows mute behavior. Hardware mute, if separately offered, needs verified monitor-specific support. The initial volume mapping applies to ordinary shared-mode playback; exclusive-mode playback can bypass Windows software attenuation, changing the effective response. [Shared and exclusive audio controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls)

If a DDC operation fails, keep Windows volume functional, show an unavailable status in the tray, and retry with a fresh connection. Sleep cancels outstanding work until resume. A single asynchronous loop owns connection discovery and sync, preventing overlapping reconnect operations. On exit or a crash, ordinary Windows audio continues because no audio stream is routed through our process. Do not restore stale values or force monitor volume to maximum during recovery.

## Components and packaging

The implementation uses C# with a WPF dispatcher and temporary volume/brightness indicators, a Windows Forms tray icon, Core Audio callbacks for change revisions, and an isolated DDC worker. The SDK is pinned to .NET 10.0.401 and the runtime is bundled. No custom driver, virtual audio device, audio bridge, or administrative service is part of this design. [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

Build a per-user MSI with WiX, bundle all runtime dependencies, and target normal installation without elevation on an unmanaged Windows 11 PC. Startup is enabled on a fresh installation or first portable launch. An empty startup registry value records an explicit opt-out; initialization, upgrade, and repair preserve it. Enabled startup entries refresh to the current executable path, and MSI ownership removes the entry on uninstall. WiX's current release policy includes a maintenance fee for revenue-generating use. [Installation contexts](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context), [WiX](https://docs.firegiant.com/wix/)

Sign and timestamp application payloads, verify dependency signatures, assemble the MSI, then sign and timestamp it. No custom kernel driver means no driver-submission or driver-certification dependency. Choose the application signing provider after establishing publisher eligibility.

A fresh signed download may still receive SmartScreen warnings. Microsoft states that Store installation avoids the SmartScreen prompt, including its MSI/EXE path. Keep the signed standalone MSI and investigate an accepted Store listing as the consumer distribution path. [SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation), [signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

For Store MSI submissions, provide signed installer/PE payloads, immutable versioned HTTPS URLs, silent installation support, and all dependencies. Store acceptance is a release dependency; the Store does not re-sign the MSI. Do not require users to disable security protections or install a custom root certificate. [Store MSI requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)

## Work order and acceptance

1. **Read-only probe:** identify Windows build, GPU/driver, built-in-panel state, DDC ranges, current audio routing, and existing hardware-volume support.
2. **Controlled Dell test:** verify reversible brightness/volume writes and monitor-button readback in SDR over HDMI, then DP. Restore test values. Record HDR behavior separately.
3. **Direct brightness:** verify shortcuts, confirmed readback, indicator appearance and focus behavior, and cursor targeting. Unsupported or ambiguous targets must cause no writes to another screen. Repeat on multiple displays and DPI scales.
4. **Hardware volume:** verify Windows stays at 100%, monitor keys/readback agree, mute is preserved, and headphones retain ordinary Windows behavior. Test the useful listening range; do not infer acoustical gain from raw percentages alone.
5. **Recovery and polish:** test feedback suppression and stale operations with a fake transport, then sleep, reconnects, endpoint changes, DDC failure, application exit, accessibility, and multiple displays on Windows.
6. **Release installation:** verify signed per-user installation, upgrade, repair, rollback, uninstall, startup cleanup, and clean-machine behavior with Windows protections enabled.
7. **Distribution:** test the browser-downloaded MSI and accepted Store install separately. Signature verification does not prove reputation or Store acceptance.

No custom driver will be created. Hardware volume with Windows at 100% and direct DDC brightness are the selected implementation. Remaining validation includes the measured listening response, multiple displays, HDR behavior, and release installation.
