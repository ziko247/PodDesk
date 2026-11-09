# PodDesk

A native C# and WPF dashboard for AirPods on Windows. The dashboard and notification area show battery, connection state, and listening modes. The Accessibility tab shows iOS 27 settings and their availability.

## Run or build

- Open `Run\PodDesk.exe` from File Explorer for the hardware-tested battery build. Keep its files together. This x64 build uses the .NET 10 Desktop Runtime already present on the test PC.
- `Run-AutoBattery\PodDesk.exe` is the new build with remembered battery tracking. The source built and the simulated matching checks passed. Windows Application Control blocked its launch on this PC with error `0x800711C7`, so automatic case reopening still needs a live test. Use `Run` for the working dashboard on this PC.
- Close the window to keep monitoring from the notification area. Double-click the PodDesk icon to reopen it. Right-click it for battery, listening modes, and **Exit**. **Ctrl+Q** also exits from the window.
- To build from source with the .NET 10 SDK, run the following in this folder.

```powershell
dotnet build .\PodDesk.slnx -c Release
dotnet publish .\PodDesk.App -c Release -o .\Run-AutoBattery
dotnet run --project .\PodDesk.Probe -- --self-test
```

On this PC, the system `dotnet` is runtime-only. Use `..\..\work\dotnet\dotnet.exe` in place of `dotnet` to use the SDK staged in this workspace. Exit a running copy before publishing over its folder.

To run the hardware probe for 90 seconds, run `dotnet run --project .\PodDesk.Probe -- 90` from a normal Windows session. The probe writes `probe-results.txt` beside its executable, including local Bluetooth identifiers. Open the case while it scans. Run PodDesk and the probe from your usual Windows desktop account so the Bluetooth broker can enumerate and scan.

## Battery on this PC

1. Select your paired AirPods in **Device**. On this PC the pair appears as **Oppo earphones**.
2. Under **Battery**, choose **Find battery**. Seat both earbuds, close the case for a few seconds, then reopen it near the PC. Keep other AirPods cases closed during the search.
3. Choose the fresh report for your case and click **Use report**. This links the current broadcast to the selected pair. If a later case opening rotates its address and readings stop refreshing, use **Find battery** again.

The **Device** list contains paired AirPods. The battery search separately lists recent open-case reports for the selected model, with complete reports first. BLE percentages come from the AirPods in 10-point steps. The battery cards display them with **≈**. Each card keeps its own timestamp. A case reading goes stale after 45 seconds without a fresh reading and a bud reading after 120 seconds. The Windows whole-device property, if available, is shown separately as cached data.

The updated search was tested on this PC by reopening the case and linking a live report under **Oppo earphones**. The dashboard showed approximately 80% left, 80% right, and 40% case.

### Remembered battery build

The first confirmation uses **Use and remember**. It saves the Windows-paired identity and the confirmed broadcast address to local PodDesk settings and selects that pair on startup. PodDesk uses Windows-resolved identities and the confirmed address when available. For a rotating address, it can infer a match for a single paired model using a recent reconnect or matching lid and battery activity. It labels those values **Auto-matched BLE estimate** and requests a manual choice when competing case reports could fit. The inferred match is a proximity estimate. AirPods with an available identity resolving key can be identified more precisely. [Windows Bluetooth address API](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.frombluetoothaddressasync) · [AirPods identity keys](https://github.com/d4rken-org/capod/wiki/airpod-Keys)

## Compatibility on the test PC

**AirPods 5** renamed **Oppo earphones**, Apple Bluetooth product `0x2036`. The model mapping indicates the **USB-C charging case** variant. Firmware **9A350** and **iOS 27.0.1** were supplied by the user. Windows **26H2, 26300.9711**, Intel Wireless Bluetooth `8087:0037`.

| Feature | Status on this setup | Dependency or remaining test |
| --- | --- | --- |
| Renamed paired discovery and case variant | Windows paired identity and Apple AAP SDP service verified. USB-C case inferred from `0x2036`. | Apple lists different case and earbud model numbers for the two variants. |
| Left and right earbud batteries | Hardware BLE verified. Observed about **70% left** and **60–70% right**, with charging flags when seated. | Rotating BLE identities are kept separate. Use **Find battery** from the paired device to link the current report. |
| Case battery | Hardware BLE verified at about **10%** with the case open. Closed reports omitted the case. A new open report restored it. The live dashboard retained and marked an aged reading stale. | Continuous reporting and fine-grained percentages depend on what the device sends. Case charging flag is decoded. A charging transition of the case itself still needs hardware testing. |
| Windows whole-device battery and GATT Battery Service | Tried from the desktop. Windows battery property was unavailable and Windows returned no matching LE endpoint for the paired identity. | BLE proximity broadcasts provided the readings above. |
| Connection and recovery | Verified paired discovery, disconnect after lid close, and reconnect after reopening in the hardware probe. | Windows sleep and wake handling is implemented. A physical sleep and wake test is pending. |
| Active listening mode and stem changes | Device report handling implemented. Hardware test blocked. | Requires a working NTPodsAAP interface and firmware validation. |
| Off, Transparency, Adaptive Audio, Active Noise Cancellation | Four AAP commands implemented with pending, device confirmation, timeout, and disconnect states. Hardware test blocked. | Requires a working NTPodsAAP interface and firmware validation. |
| Press Speed, Press and Hold Duration, Tone Volume | Unknown here. Read-only if a recognized control-link report arrives. | Use iPhone for changes. Reading and writing on `9A350` remain unverified. |
| Spatial Audio Head Tracking | iPhone setting, current value unknown. | Use **Follow iPhone** in iPhone Accessibility. |
| Volume Swipe, Volume Swipe Speed | Wireless-case model rows. Excluded for the detected USB-C model. | Wireless model needs iPhone confirmation and Windows protocol testing. |
| Charging Case Sounds | Wireless-case model setting under **Audio & Routing**. Excluded for the detected USB-C model. | Use a supported Apple device. |
| Noise Cancellation with One AirPod | Omitted. Apple's iOS 27 Accessibility page documents the row for AirPods Pro. | An exact screenshot from the user's iPhone would confirm the displayed settings on this pair. |

## Listening-mode transport

PodDesk opens classic Bluetooth L2CAP PSM `0x1001` through the optional **NTPodsAAP** KMDF Bluetooth profile driver. It talks directly to the driver interface for the selected paired address. The NTPods daemon, virtual microphone driver, and companion service are unnecessary for this implementation. Keep the NTPods app and daemon closed when PodDesk uses the driver's exclusive handle.

Windows returned Winsock error **10044** when the probe requested a classic L2CAP socket. The Apple AAP service is enumerated on this PC, but the NTPodsAAP service and interface were absent. The inspected prebuilt NTPodsAAP `.sys` and `.cat` both returned **NotSigned**. Upstream documents local test signing and Test Mode with Secure Boot off. This PC reports Secure Boot enabled. The INF matches the AAP service UUID exposed by this pair. Driver load and AirPods 5 control on Windows build **26300.9711** have yet to be verified. Driver installation and any security changes require separate approval.

If a working driver is available for a hardware test, wear both earbuds and connect them to Windows. Select the paired entry and wait for the device to report its current mode. Select each of the four modes in turn and check that the pending state clears only after the corresponding report. Press and hold a stem until you hear the listening-mode chime and verify the dashboard updates without a click. Finally, disconnect and reconnect using Windows Bluetooth settings and repeat a command. Use an iPhone screenshot of **Settings → Accessibility → AirPods → your pair** to confirm the exact iOS list.

### Transport alternatives checked on November 9, 2026

- **Native Windows.** Microsoft documents RFCOMM for desktop Bluetooth sockets. The original hardware probe returned `10044` when opening the classic L2CAP socket used by AAP. Windows Application Control blocked a new Winsock/SDP probe before it could run. The Microsoft Update Catalog searches returned no AAP driver for this device. [Microsoft Winsock Bluetooth support](https://learn.microsoft.com/en-us/windows/win32/bluetooth/bluetooth-and-socket)
- **MagicAAP.** The official package requires Test Mode. Windows reported `UnknownError` for the Community v1 driver's catalog and binary because the timestamp could not be verified. The Community setup script adds a Defender exclusion, and Windows' April 2026 policy update removes default trust for legacy cross-signatures. [MagicAAP driver variants](https://magicpods.app/magicaap/) · [Windows Driver Policy](https://support.microsoft.com/en-us/windows/hardware/drivers/the-windows-driver-policy)
- **Older AirPods 4 ANC drivers.** SoundStage `0.3.0` and `0.4.0` list AirPods 4 ANC product `201B` and a generic AAP match that applies to this PC's AirPods 5 `2036`. The developer tested `0.4.0` with AirPods 4 ANC on Windows 10. Its published package uses a test certificate. The verified vendor archive for MagicAAP Official `0.0.0.1` has the same generic match and an untrusted WDK test certificate. The LibrePods bridge driver also has the generic match and an untrusted test catalog. These checks verified package matching and signing evidence. A driver-load and mode-control trial still requires approval. [SoundStage release](https://github.com/capriqqw/soundstage-for-airpods/releases/tag/v0.4.0) · [MagicAAP Official](https://help.magicpods.app/fun-magicaap/) · [LibrePods bridge driver](https://github.com/will-ch-h/l2cap-windowsdriver/releases/tag/driver-latest)
- **USB HID.** The Raspberry Pi Pico 2 W `airpods-dongle` firmware has a published UF2 and source for reading and writing the four modes. Its firmware profiles include both AirPods 5 variants. Windows uses its built-in USB drivers. The AirPods pair to the dongle and playback uses its USB audio endpoint. AirPods 5 firmware `9A350` still needs a hardware test, and PodDesk would need a USB HID transport. [Firmware release](https://github.com/Ripthulhu/airpods-dongle/releases/tag/v0.1.64-741c3d6ac145) · [Mode readback and command source](https://github.com/Ripthulhu/airpods-dongle/blob/741c3d6ac145f0e0c53f3017ed9c81681065a959/tools/airpods.py)

## Testing without hardware

Check **Demo mode** or launch `PodDesk.exe --demo`. Mode changes are confirmed by the simulator. Clear **Demo case open** and wait 10 seconds to see the case age to stale. Reopen it to refresh the reading. The Accessibility tab shows sample values. The probe's `--self-test` exercises synthetic BLE and AAP frames separately from the hardware probe.

Verified in the running app on this PC: paired discovery, selection of a real BLE broadcast, manual linking, three live battery cards, case staleness, and the USB-C model's Accessibility statuses. Separately, all four demo mode transitions, demo case staleness, and the wireless model's demo Accessibility rows passed. The tray menu and physical sleep/wake still need interactive checks.

## Sources

- [Microsoft BLE advertisement watcher](https://learn.microsoft.com/en-us/windows/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher) and [Bluetooth desktop API support](https://learn.microsoft.com/uwp/api/windows.devices.bluetooth).
- [Microsoft kernel L2CAP request](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/bthddi/ns-bthddi-_brb_l2ca_open_channel) and [Windows driver signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-signing).
- [LibrePods at `89356c0`](https://github.com/librepods-org/librepods/tree/89356c08cea3db0b6b54792d9329d09a87cc85fa) and [NTPods at `188e45d`](https://github.com/arctumn/ntpods/tree/188e45df20259b616ce155be812086259d1a2488), both GPL-3.0. LibrePods lists both AirPods 5 models. NTPods matches the Apple service UUID. No upstream test for this exact firmware and Windows combination was found. PodDesk implements the wire formats and driver interface in C#. The distribution contains no upstream source files, graphics, or driver binaries.
- [Apple model and case identifiers](https://support.apple.com/en-us/109525), [firmware](https://support.apple.com/en-us/106340), and [Bluetooth product ID mapping from Apple platform data](https://github.com/mooons/airpods-fw).
- [Apple iOS 27 AirPods Accessibility](https://support.apple.com/en-gb/guide/iphone/iph345efe861/27/ios/27), [force-sensor settings](https://support.apple.com/en-us/guide/airpods/dev68116dd52/27/web/27), [case sounds](https://support.apple.com/en-ie/guide/airpods/dev57e5b7e58/27/web/27), and [listening modes](https://support.apple.com/guide/airpods/switch-between-listening-modes-dev9812f5cc3/27/web/27).
