# G2000 Temperature Soft Interlock

This prototype monitors the HikmicroAnalyzer window with OCR and trips the G2000 through either an external relay loop or a verified G2000 CAN control path when the detected maximum temperature reaches the configured threshold.

## Hardware Integration

Two hardware control paths are now supported:

1. A USB serial relay as a dry contact in the G2000 external interlock / emergency-stop loop.
2. A PEAK-compatible CAN adapter talking to the G2000 CAN option at `125 kbit/s`.

The relay path remains useful as a hardware-independent stop path. The CAN path is now verified on hardware for the basic command states needed by this application.

Before running with plasma enabled:

1. Wire the relay contact only as a passive contact.
2. Confirm the relay stop action opens the interlock circuit.
3. Verify with a multimeter that `Test Relay Stop` changes the relay contact as expected.
4. Confirm the G2000 disables high voltage when that contact opens.

## Windows Setup

Install:

1. Visual Studio 2022 with `.NET desktop development`, or .NET 8 SDK.
2. Tesseract OCR for Windows.
3. The USB relay driver so the relay appears as a COM port, or a PEAK-compatible CAN driver such as `PCAN-USB FD`.

Build and test:

```powershell
dotnet restore .\src\ReactorSoftInterlock.sln
dotnet test .\src\ReactorSoftInterlock.sln
dotnet build .\src\ReactorSoftInterlock.sln -c Release
```

Run:

```powershell
dotnet run --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

## Configuration

The app creates and edits `appsettings.json` next to the executable.

Important fields:

- `ThresholdC`: default `90.0`.
- `WindowTitleContains`: text used to find the HikmicroAnalyzer window.
- `Roi`: saved after using `Select ROI`.
- `Ocr.TesseractExePath`: path to `tesseract.exe`, or just `tesseract.exe` if it is on `PATH`.
- `Relay.Mode`: leave empty for legacy behavior, or set to `G2000Can` for the CAN controller.
- `Relay.DryRun`: legacy fallback for the original serial relay path.
- `Relay.PortName`, `Relay.BaudRate`, `Relay.StopCommandHex`, `Relay.ResetCommandHex`: serial relay settings.
- `Relay.G2000Can.Channel`: for example `UsbBus1`.
- `Relay.G2000Can.NodeId`: the G2000 `KNO` node address, for example `0`.
- `Relay.G2000Can.CommandPeriodMs`: keepalive period, verified at `100`.

Verified G2000 CAN command frames with `KNO = 0`:

- `80 00 03 00 00 00 00 00`: `HV AUS`, source `CAN-BUS`
- `82 00 03 00 00 00 00 00`: `HV bereit`
- `83 00 03 00 00 00 00 00`: `HV EIN`

Important manual-backed protocol finding:

- `0x300+n` is the writable setpoint for `Zwischenkreisspannung / V (U2)`, the internal DC-link voltage.
- It is not the same thing as the external HV output terminal voltage.
- The manual also states that the value actually achievable by the generator can deviate from the requested setpoint because of internal controller limits.
- The software now defaults to the manual-backed U2 range of `0 .. 300 V` so the writable range matches the panel.
- This is still a U2 range, not an external HV output range.
- Out-of-range writes are blocked unless `Relay.G2000Can.AllowUnsafeU2Writes` is explicitly enabled.

Verified `0x180` status frames:

- `01 00 03 00 00 00 00 00`: ready, CAN source, no fault
- `09 00 03 00 00 00 00 00`: ready + HV enable
- `0D 00 03 00 00 00 00 00`: ready + HV enable + HV on
- `02 00 03 09 00 00 00 00`: CAN source with external CAN timeout after keepalive loss

## Normal Use

1. Start HikmicroAnalyzer and make sure the maximum temperature text is visible.
2. Start this application.
3. Set the window title filter, Tesseract path, relay settings, and threshold.
4. Click `Select ROI` and drag over the maximum temperature text.
5. Keep relay in dry-run mode and click `Start`.
6. Once OCR is stable, configure the real COM port and relay commands.
7. Click `Test Relay Stop`.
8. Disable dry-run only after the chosen hardware path has been verified.

When a reading is `>= 90.0 C`, the app enters `Tripped`, sends the stop command, records the event to CSV, and stays latched until a manual reset is performed below the threshold.

For the current first-pass CAN integration:

- Trip uses the verified `HV AUS` frame.
- Reset uses the verified `HV bereit` frame.
- The app does not yet issue `HV EIN` automatically.
- Once the app takes G2000 CAN control, it must keep sending periodic command frames. Stopping the app while the G2000 is still under CAN control will lead to the verified external CAN timeout condition unless control is handed back or another sender keeps the keepalive active.
