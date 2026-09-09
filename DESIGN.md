# Monitor Sync for Windows 11

Design and implementation status, 9 September 2026. Native Windows sliders remain a requirement. The selected first step is simple equal-percentage volume synchronization. A WPF app, isolated DDC worker, deterministic sync tests, and per-user MSI authoring are implemented. The Windows x64 build has been cross-compiled; Windows execution, Dell testing, final MSI validation, signing, and native brightness integration remain outstanding. See [README](README.md) and [validation](docs/TESTING.md).

No custom driver will be developed. Use an ordinary application and the existing Windows monitor/audio APIs. Native Windows sliders remain the desired interface; hardware-only volume and native external brightness must not be promised where those APIs cannot provide them.

## Requirements

- Windows Quick Settings volume and brightness sliders show and control the monitor values.
- Volume should feel like one control. Choose the tradeoff between equal Windows/monitor percentages and the combined volume curve; do not introduce a custom driver to bypass Windows attenuation.
- No custom audio or display driver, virtual audio routing, or audio-processing bridge.
- Monitor-button changes flow back into Windows after readback.
- Initial hardware: Dell S2725QS over HDMI, with DisplayPort available for testing. Audio over the selected video connection is the working assumption pending endpoint discovery.
- Design the monitor transport for HDMI and DisplayPort; expand compatibility only when each complete connection path is verified.
- Easy MSI installation with a verified publisher and a distribution strategy that avoids alarming security warnings.
- A settings window can handle setup and diagnostics. A replacement tray slider does not satisfy the native-control requirement.

## Feasibility assessment

| Area | Evidence | Design consequence |
| --- | --- | --- |
| Hardware brightness and volume | Windows has DDC/CI APIs; the Dell manual documents DDC/CI and monitor controls | Probe actual feature support and readback over each connection |
| Native Windows volume | An ordinary app can observe endpoint changes and mirror them to DDC | Equal-value synchronization selected for the first preview |
| Native Windows brightness | Windows uses monitor/graphics-driver brightness integration; there is no general app extension for adding our control to Quick Settings | Critical feasibility gate; no validated universal solution yet |
| MSI | Standard Windows Installer packaging is available | Bundle dependencies and support upgrade/uninstall |
| Warning-free direct download | Signing alone does not guarantee SmartScreen reputation | Prefer a Store installation path; keep the signed MSI downloadable |

MonitorControl uses DDC/CI on supported external displays, and also offers other hardware protocols and software dimming. Its behavior is not universally “all software values at 100%.” The useful goal here is one hardware control represented by the operating system. [MonitorControl](https://github.com/MonitorControl/MonitorControl)

## Dell S2725QS baseline

Dell's manual lists two HDMI inputs, one DisplayPort input, integrated speakers, a 0–100 speaker-volume control, and a DDC/CI setting under Others. It does not establish reliable support for every DDC feature on every input. Keep brightness, volume, mute, and readback as separate probe results.

The manual also states that manual brightness/contrast adjustment is unavailable when Smart HDR is active and HDR content is displayed. Begin hardware brightness acceptance in SDR. An HDR limitation must be surfaced honestly; changing Windows SDR-content brightness is a different operation. [Dell S2725QS user guide, manufacturer document mirrored by Device.report](https://device.report/m/fddc2bf0d49c623147f5d24edc666ca98fd9f670c968ff870157cbb205763c49)

Support means the entire PC/GPU-driver/cable/adapter/monitor combination passes. Start with direct HDMI, repeat with direct DP, and later test docks and USB-C-to-DP paths. Merely changing the connector does not solve Windows' native-slider integration.

## Native volume without a custom driver

An ordinary app can subscribe to Windows endpoint-volume changes, send corresponding values to the Dell using DDC/CI, and reflect monitor-button changes back into the Windows endpoint with feedback suppression. [Endpoint volume callbacks](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolumecallback)

First inspect endpoint hardware-volume support and verify whether Windows already operates the same monitor amplifier. If it does, do not apply that control a second time through DDC. The rest of this section assumes independent Windows and monitor volume stages. [Hardware support API](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-queryhardwaresupport)

For independent stages, normalized signal amplitude is `G = W(s) * M(h)`, where `s` is the Windows slider fraction, `h` is the monitor setting fraction, and W/M are their actual gain curves. Attenuation in decibels adds. Per-application volume and monitor processing are held constant in this model.

Equal-value synchronization sets `h = s`. If both stages were linear in amplitude, the result would be `G = s²`: at two 50% settings, amplitude would be 25% of full scale. That is a quadratic amplitude curve, not a statement that sound is perceived as 25% as loud. Actual Windows percentages use a nonlinear audio-tapered curve, and the Dell's curve is not yet measured, so the real result is not necessarily a parabola. [Windows volume taper](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-setmastervolumelevelscalar)

| Driver-free approach | What the Windows slider means | Main tradeoff |
| --- | --- | --- |
| Equal-value sync | Same normalized setting as the monitor | Both attenuation stages vary, producing a different volume curve |
| Mapped sync | Logical volume; monitor follows a separately chosen curve | Values differ, but the extra attenuation can be reduced |
| Fixed monitor level | Ordinary Windows software volume | Preserves the Windows curve; monitor percentage is not synchronized |

Equal-value sync is technically viable. It changes the volume response, not inherently the waveform fidelity. Test actual quiet-to-loud travel before deciding that the response is unusable. It may provide useful quiet listening levels; that is a listening-test outcome, not a guarantee.

For mapped sync, keep the monitor in a useful upper portion of its range while Windows supplies the full adjustment range. An illustrative uncalibrated mapping is `h = 0.5 + 0.5*s`; choose actual limits only after testing the Dell. This removes the second near-zero control range but cannot preserve matching percentages. At zero, use mute explicitly. Both increase and decrease transitions need coalescing and readback because Windows reacts before DDC settles.

Calibration can target a chosen combined curve `T(s)` using `M(h(s)) = T(s) / W(s)` where W is nonzero and the required gain lies inside the monitor's available range. It cannot manufacture unavailable gain. To preserve exactly the ordinary Windows curve scaled by a fixed maximum, `T(s) = C*W(s)`, the monitor gain must simply stay constant at C. Applying a square root to the monitor percentage alone does not cancel two unknown nonlinear curves.

The fixed-level option uses a user-selected comfortable maximum monitor level, not an automatic jump to 100%. Windows then controls listening volume normally. Changing monitor buttons changes that ceiling unless the user explicitly restores it; do not fight physical-button changes.

Equal-value sync is selected for the first preview. Measure and listen on the Dell before considering mapped sync. Fixed-level operation remains a possible later option if preserving the Windows volume response matters more than matching the monitor percentage; neither alternative is implemented.

Resetting the same physical endpoint to 100% also resets its native slider. A change callback cannot disconnect those two meanings. No such reset loop will be implemented. [Endpoint volume controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls)

## Native brightness: first feasibility gate

Windows' system brightness integration is provided through `Monitor.sys`, graphics-driver brightness interfaces, and/or ACPI. Microsoft documents the operating system's brightness slider using that integration. [Integrated-panel architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/supporting-brightness-controls-on-integrated-display-panels)

Microsoft also documents a `BrightnessControl` override for internal panels wired through external connectors. It designates one target, still requires OEM brightness implementation integrated with the graphics driver, and does not provide general independent brightness for multiple external displays. A monitor INF or registry edit alone is not a DDC-to-slider adapter. [External-connector requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/supporting-brightness-controls-for-external-display-connectors)

Twinkle Tray's project documentation likewise reports no official API for modifying the Windows Quick Settings flyout. A Windows-looking popup is therefore not evidence of native integration. [Twinkle Tray integration FAQ](https://github.com/xanderfrangos/twinkle-tray/wiki/Common-requests-%26-FAQs#integrating-with-the-windows-quick-settings-flyout)

Within the no-custom-driver constraint, determine whether the PC already exposes a native brightness target. An existing laptop panel's brightness event can drive DDC writes to the Dell, but it also changes the laptop panel and does not create an independent Dell slider. A built-in-screen requirement must not be hidden.

If the PC has no usable existing brightness target, there is currently no verified supported app-only route to add the required native slider. Leave this requirement unresolved rather than substituting a custom popup without agreement. No new monitor filter/provider driver will be developed. Shell injection or a dummy display would also require a separate product decision and are not part of this proposal.

The minimum successful demonstration is the real Windows brightness slider appearing on the intended PC, changing the physical Dell backlight, and receiving changes from the Dell's own controls. It must survive reboot, reconnect, and supported Windows updates without altering resolution, refresh rate, HDR capability, or desktop layout. On a desktop with no existing brightness control, this is the highest-risk part of the project.

Initially one Dell display is the native brightness target. A single global slider has no inherent way to select among several independent external panels. Later choose a fixed target or explicit linked group through settings; do not promise new per-monitor native Windows controls without evidence.

## Shared monitor-control engine

Use physical-monitor enumeration and `Dxva2.dll`. Read with `GetVCPFeatureAndVCPFeatureReply`, write with `SetVCPFeature`. The documented typical call times are about 40 ms for reads and 50 ms for writes, so work must be queued and coalesced. [Read API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply), [write API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)

Probe brightness (`0x10`) and speaker volume (`0x62`). Investigate mute (`0x8D`) separately because encoding varies with MCCS version and can include screen-blanking semantics. Do not send guessed mute values. [Feature-code reference](https://www.ddcutil.com/vcpinfo_output/)

Keep native DDC calls in a restartable worker process, separate from audio notifications and UI. Process isolation can contain a hung user-mode call; it cannot protect against a kernel driver fault. Serialize operations initially across the worker, and re-enumerate handles after restarting it.

The coordinator tracks stable display identity, explicit endpoint pairing, capability status, raw range, desired value, observed value/time, operation revision, and connection generation. EDID identifiers and device paths assist pairing; display numbers and friendly names alone are insufficient. Ambiguous pairing disables control until resolved.

Synchronization rules:

1. Read live values on discovery. Do not apply a stale saved profile at startup.
2. Normalize against verified feature ranges. Coalesce rapid requests to the newest value, with roughly 100 ms as an initial tuning target.
3. Read back after settling. Publish the value actually applied, including clamping, without confusing it with newer pending intent.
4. Poll slowly when idle, initially about every five seconds. The preview pauses on failed or unconfirmed operations and requires a refresh after DDC failure. Readback is eventual, not instantaneous.
5. Observed external changes update native logical controls without generating a new hardware command. Tag origin/revision to prevent feedback loops.
6. Discard work from old connections. Re-enumerate after sleep, display changes, or audio-route changes.
7. Mark unreadable or unavailable controls as such. Cached values must not be presented as verified hardware state.

## Audio lifecycle and recovery

Only manage an explicitly paired active playback endpoint. Switching to headphones or an unrelated device suspends monitor-volume synchronization. Discovery reads current state without changing audio levels; enabling synchronization must not unexpectedly raise the monitor volume.

In equal-value mode, a confirmed monitor-button change updates the Windows setting once; self-originated notifications must not cause another DDC write. With mapped sync, reverse updates require a defined invertible mapping, quantization tolerance, and behavior for hardware values outside its range. Do not claim exact reverse synchronization where those conditions are not met.

Preserve ordinary Windows mute behavior. Hardware mute, if separately offered, needs verified monitor-specific support. The initial volume mapping applies to ordinary shared-mode playback; exclusive-mode playback can bypass Windows software attenuation, changing the effective response. [Shared and exclusive audio controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls)

If a DDC write fails, keep Windows volume functional, suspend failed hardware synchronization, and show the mismatch. On exit or a crash, ordinary Windows audio continues because no audio stream is routed through our process. Do not restore stale values or force monitor volume to maximum during recovery.

## Components and packaging

The implementation uses C# with WPF, Core Audio callbacks for change revisions, a DDC worker, and a settings/diagnostics window. The SDK is pinned to .NET 10.0.401 and the runtime is bundled. No custom driver, virtual audio device, audio bridge, or administrative service is part of this design. [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

Build a per-user MSI with WiX, bundle all runtime dependencies, and target normal installation without elevation on an unmanaged Windows 11 PC. Include upgrade, repair, uninstall, and optional start-at-login behavior. WiX's current release policy includes a maintenance fee for revenue-generating use. [Installation contexts](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context), [WiX](https://docs.firegiant.com/wix/)

Sign and timestamp application payloads, verify dependency signatures, assemble the MSI, then sign and timestamp it. No custom kernel driver means no driver-submission or driver-certification dependency. Choose the application signing provider after establishing publisher eligibility.

A fresh signed download may still receive SmartScreen warnings. Microsoft states that Store installation avoids the SmartScreen prompt, including its MSI/EXE path. Keep the signed standalone MSI and investigate an accepted Store listing as the consumer distribution path. [SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation), [signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

For Store MSI submissions, provide signed installer/PE payloads, immutable versioned HTTPS URLs, silent installation support, and all dependencies. Store acceptance is a release dependency; the Store does not re-sign the MSI. Do not require users to disable security protections or install a custom root certificate. [Store MSI requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)

## Work order and acceptance

1. **Read-only probe:** identify Windows build, GPU/driver, built-in-panel state, DDC ranges, current audio routing, and existing hardware-volume support.
2. **Controlled Dell test:** verify reversible brightness/volume writes and monitor-button readback in SDR over HDMI, then DP. Restore test values. Record HDR behavior separately.
3. **Native brightness assessment:** determine whether an existing Windows brightness target can satisfy the requirement without a custom driver. Report any unmet requirement explicitly.
4. **Volume comparison:** compare ordinary Windows volume at a fixed monitor setting, equal-value synchronization, and a mapped curve. Inspect the endpoint's reported dB levels, measure monitor gain if calibration is needed, and test useful listening range, mute, transition timing, and readback. Do not infer acoustical gain from raw percentages alone.
5. **Recovery and polish:** test feedback suppression and stale operations with a fake transport, then sleep, reconnects, endpoint changes, DDC failure, application exit, accessibility, and multiple displays on Windows.
6. **Release installation:** verify signed per-user installation, upgrade, repair, rollback, uninstall, startup cleanup, and clean-machine behavior with Windows protections enabled.
7. **Distribution:** test the browser-downloaded MSI and accepted Store install separately. Signature verification does not prove reputation or Store acceptance.

No custom driver will be created. Equal-value volume sync is the selected first implementation. The remaining product decisions depend on its measured listening response and whether existing Windows brightness integration can meet the native-slider requirement on the target PC.
