# G2000 Temperature Soft Interlock

Language: [English](README.md) | [中文](README.zh-CN.md) | [Deutsch](README.de.md)

Windows desktop prototype for monitoring a laboratory plasma reactor with a HIKMICRO infrared camera workflow.

The software watches the HikmicroAnalyzer window, captures a selected temperature text area, reads it with Tesseract OCR, parses the highest visible temperature, logs readings to CSV, and sends a USB serial relay command when the temperature reaches or exceeds `90.0 C`.

This project is a laboratory prototype. It is intended to help stop the plasma generator by opening or switching the confirmed G2000 external interlock / emergency-stop path. It is not a certified industrial safety controller.

## System Overview

The full setup has four parts:

- HIKMICRO camera and HikmicroAnalyzer: displays the live thermal image and accurate temperature overlay.
- This WPF application: captures the temperature text from the screen and decides whether to trip.
- USB serial relay: receives HEX commands from the Windows PC.
- G2000 plasma generator interlock / Not-Aus loop: the relay contact is wired as a passive dry contact into the confirmed external interlock path.

The app does not use a HIKMICRO API. It reads the visible HikmicroAnalyzer overlay by screenshot OCR.

## How The App Works

1. It finds a visible HikmicroAnalyzer window using the `Window` title filter.
2. The user selects a region of interest (ROI) around the maximum temperature text.
3. The app periodically screenshots that ROI.
4. Tesseract OCR converts the screenshot to text.
5. The parser extracts temperatures such as `Max 89.9 C`, `90.1°C`, or `最高 90.0 C`.
6. Every sample is written to CSV.
7. When a valid temperature is `>= ThresholdC`, the app enters `Tripped`.
8. In `Tripped`, the stop relay command is sent once and the state stays latched.
9. Manual reset is only allowed after a valid temperature below the threshold is read.
10. If `Auto reset` is enabled, the relay reset command is sent automatically after the temperature stays below `Recovery C` for the configured stable time.

If OCR fails, the app records `NO READING`. A `NO READING` sample does not trigger the relay by itself.

## Repository Layout

- `src/ReactorSoftInterlock.sln`: Visual Studio / .NET solution.
- `src/ReactorSoftInterlock.Wpf`: WPF desktop application.
- `src/ReactorSoftInterlock.Domain`: threshold, sample, relay action, and latched trip logic.
- `src/ReactorSoftInterlock.Application`: monitoring loop, OCR text parsing, and ports.
- `src/ReactorSoftInterlock.Infrastructure`: screenshot capture, Tesseract adapter, serial relay, CSV log, settings.
- `tests/ReactorSoftInterlock.Tests`: tests for parsing, trip logic, CSV, HEX commands, and monitoring behavior.
- `docs/g2000-soft-interlock.md`: additional hardware notes.
- `Doc`: G2000 and project PDFs.
- `HikmicroAnalyzer`: sample HIKMICRO images.

## Requirements

Development PC:

- Windows 10 or later.
- Visual Studio 2022 with `.NET desktop development`, or .NET 8 SDK.
- Internet access for the first NuGet restore.

Laboratory PC:

- Windows 10 or later.
- HikmicroAnalyzer with the maximum temperature overlay visible.
- Tesseract OCR for Windows.
- USB serial relay and its Windows driver.
- G2000 external interlock / Not-Aus connection confirmed by the lab setup.
- Multimeter for checking relay contact behavior before connecting to the G2000.

## Install Required Components

For a normal laboratory PC, use the self-contained Release package when possible. It includes the .NET runtime needed by the app, so the lab PC usually does not need a separate .NET installation. The lab PC still needs HikmicroAnalyzer, Tesseract OCR, the USB relay driver, and the correct relay/G2000 wiring.

Install on the lab PC:

1. Install HikmicroAnalyzer and confirm that the camera image and maximum temperature overlay are visible.
2. Install Tesseract OCR for Windows. After installation, either add the Tesseract installation folder to `PATH`, or copy the full path to `tesseract.exe` into the app field `Tesseract`.
3. Install the USB serial relay driver. Common relay boards use CH340, CH341, CP210x, or FTDI drivers; use the driver matching the relay board.
4. Open Windows Device Manager, expand `Ports (COM & LPT)`, plug the relay in, and note the `COMx` value.
5. Keep `Dry run relay` enabled until OCR is working and the relay has been tested without the G2000.
6. Confirm with the G2000 manual and the lab wiring which external interlock / Not-Aus contact pair must be opened or closed to disable high voltage.

Install on a development PC:

1. Install Visual Studio 2022.
2. In Visual Studio Installer, select the workload `.NET desktop development`.
3. Make sure `.NET 8.0 SDK` is installed in the individual components, or install the .NET 8 SDK separately.
4. If Visual Studio opens the solution as unsupported, install `.NET desktop development`, close Visual Studio, and reopen `src/ReactorSoftInterlock.sln`.
5. If Visual Studio asks for non-functional solution changes, it is safe to accept; the solution must still contain Debug and Release configurations.

## Build From Source

Open PowerShell in the repository root:

```powershell
cd "L:\Documents\files\Yu Zhang TU Clausthal\ProjectShukang\raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor"
```

Restore dependencies:

```powershell
dotnet restore .\src\ReactorSoftInterlock.sln
```

Run tests:

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Build Release:

```powershell
dotnet build .\src\ReactorSoftInterlock.sln -c Release
```

The executable is built under:

```text
src\ReactorSoftInterlock.Wpf\bin\Release\net8.0-windows\
```

Create the same self-contained package used for lab deployment:

```powershell
dotnet publish .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\ReactorSoftInterlock-win-x64
```

The published folder can be zipped and moved to the lab PC. It contains `ReactorSoftInterlock.Wpf.exe` and the .NET runtime files needed by the app.

## Run

Run from source:

```powershell
dotnet run --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

Run Release:

```powershell
dotnet run -c Release --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

You can also open `src/ReactorSoftInterlock.sln` in Visual Studio 2022, set `ReactorSoftInterlock.Wpf` as the startup project, and press `F5`.

Run the lab Release package:

1. Unzip `ReactorSoftInterlock-win-x64-latest.zip`.
2. Run `ReactorSoftInterlock.Wpf.exe`.
3. If Windows SmartScreen appears, confirm that the file came from this project before continuing.
4. Keep `appsettings.json` next to the executable; the app updates it when settings are saved.
5. If the app warns that Tesseract, ROI, HikmicroAnalyzer, COM port, or HEX commands are missing, fix that item before starting monitoring.

## First Software Setup

1. Start HikmicroAnalyzer.
2. Make sure the maximum temperature text is visible.
3. Keep HikmicroAnalyzer visible, preferably maximized.
4. Start this application.
5. Set `Window` to part of the HikmicroAnalyzer title, for example `Hikmicro`.
6. Set `Tesseract` to `tesseract.exe` or the full path to `tesseract.exe`.
7. Keep `Dry run relay` checked.
8. Click `Save Settings`.
9. Click `Select ROI`.
10. Drag only around the maximum temperature text.
11. Click `Start`.
12. Check `Temperature`, `Raw OCR text`, and the history table.

The ROI depends on the screen position and size of the HikmicroAnalyzer window. Select the ROI again if the window is moved, resized, minimized, covered, or if Windows display scaling changes.

## Connecting To The Plasma Generator

The relay must be treated as a passive contact. The PC, USB port, serial module, or this software must not feed voltage into the G2000 interlock terminals.

Connection principle:

- The USB side of the relay connects to the Windows PC.
- The relay contact side connects only as a dry contact.
- The relay contact must be inserted into the lab-confirmed G2000 external interlock / Not-Aus loop.
- In the normal state, the contact state must allow the G2000 high voltage path.
- In the trip state, the contact state must prevent or disable the G2000 high voltage path.

Do not guess the G2000 terminal numbers from this README. Use the G2000 manual and the actual lab wiring. This README describes the software and relay logic, not a certified wiring diagram.

Recommended commissioning sequence:

1. Keep `Dry run relay` checked and verify OCR only.
2. Connect the USB relay to the PC, but do not connect it to the G2000 yet.
3. Find the COM port in Windows Device Manager.
4. Fill in `COM Port`, `Baud`, `Stop HEX`, and `Reset HEX`.
5. Keep the relay contact unconnected from the G2000.
6. Uncheck `Dry run relay`.
7. Click `Test Relay Stop`.
8. Use a multimeter to check the relay contact state between `COM-NO` and `COM-NC`.
9. Decide whether the G2000 interlock must open on alarm or close on alarm.
10. Connect the relay contact into the confirmed G2000 interlock / Not-Aus loop.
11. Test G2000 response in a low-risk state before running plasma.
12. Only then use the relay for real over-temperature trips.

Contact choice:

- If the G2000 interlock must be closed to permit operation, the usual choice is `COM` + `NC`, so a relay trip opens the circuit.
- If the lab wiring or relay logic is opposite, use `COM` + `NO` or swap the relay state assigned to `Stop HEX` and `Reset HEX`.
- Always verify with a multimeter. Do not rely only on relay LEDs.

## COM Port Setup

How to find the COM port:

1. Open Windows Device Manager.
2. Expand `Ports (COM & LPT)`.
3. Unplug the USB relay.
4. Plug the USB relay back in.
5. Watch which `COMx` appears, for example `COM3` or `COM4`.
6. Enter that value exactly into `COM Port`.

Baud rate:

- Check the relay board manual first.
- Common values are `9600` and `115200`.
- The app `Baud` value must match the relay board.
- If `Test Relay Stop` does nothing, try the documented baud rate before changing HEX commands.

HEX commands:

- `Stop HEX` is sent when over-temperature trip occurs.
- `Reset HEX` is sent when the operator clicks `Reset`.
- Accepted formats:

```text
A0 01 00 A1
A0-01-00-A1
A0:01:00:A1
```

If the relay protocol is unknown, keep `Dry run relay` checked and do not connect the relay to the G2000.

Dry Run behavior:

- Checked: the app simulates relay actions and does not open the COM port.
- Unchecked: the app opens the COM port and sends the configured HEX bytes.

## Normal Lab Operation Checklist

Before experiment:

- HikmicroAnalyzer is open.
- Maximum temperature text is visible.
- HikmicroAnalyzer is not minimized.
- No other window covers the temperature text.
- ROI has been selected after the current window position was chosen.
- `Temperature` displays a reasonable value.
- CSV logging is working.
- `Dry run relay` state matches the current test stage.
- `Test Relay Stop` has been verified with a multimeter.
- The G2000 interlock response has been verified in a low-risk state.

During experiment:

- Do not move or resize HikmicroAnalyzer.
- Do not minimize HikmicroAnalyzer.
- Do not cover the temperature text.
- Watch `Raw OCR text` if the displayed temperature looks wrong.
- If `NO READING` appears repeatedly, pause and fix ROI/OCR before relying on the software.

After trip:

- Confirm plasma has stopped or entered the intended safe state.
- Keep the CSV log for the experiment record.
- Wait until the app reads a valid temperature below the threshold.
- If `Auto reset` is disabled, click `Reset`.
- If `Auto reset` is enabled, confirm that the relay reset happens only after temperature stays below `Recovery C` for the configured stable time.
- Re-check OCR and relay state before continuing.

## Status Meaning

- `Monitoring`: valid temperature below threshold.
- `NoReading`: OCR did not produce a valid temperature. The sample is logged, and relay is not tripped from that sample.
- `Tripped`: threshold reached or exceeded. The stop command was sent once and the trip is latched.
- `RelayTestFailed`: relay test failed or produced an exception.

## CSV Logging

The app writes history to:

```text
src\ReactorSoftInterlock.Wpf\bin\<Configuration>\net8.0-windows\data\temperature-history.csv
```

CSV columns:

```text
timestamp,temperature_c,raw_ocr_text,status,alarm_reason,relay_action,screenshot_roi
```

Use `Export CSV` to copy the current CSV to a selected location.

## Troubleshooting

### HikmicroAnalyzer window not found

- Check that HikmicroAnalyzer is open.
- Check that `Window` contains part of the actual window title.
- Do not minimize HikmicroAnalyzer.
- Try a shorter title filter such as `Hikmicro`.

### OCR shows `NO READING`

- Click `Select ROI` again.
- Select only the maximum temperature text, not the whole image.
- Maximize HikmicroAnalyzer.
- Remove any window covering the temperature text.
- Check the `Tesseract` path.
- Reselect ROI after changing Windows display scaling.

### Temperature is wrong

- Make the ROI tighter.
- Make sure the temperature overlay is sharp and high contrast.
- Avoid selecting labels, timestamps, or other numbers near the maximum temperature.
- Write down the `Raw OCR text`; it can be used later to improve the parser.

### `Test Relay Stop` does nothing

- Check whether `Dry run relay` is still checked.
- Confirm the COM port in Device Manager.
- Confirm the USB relay driver is installed.
- Close serial terminal tools that may occupy the port.
- Confirm `Baud` matches the relay board.
- Confirm `Stop HEX` is not empty and matches the relay protocol.

### COM port access denied

- Close Arduino Serial Monitor, PuTTY, serial assistants, or vendor relay tools.
- Unplug and reconnect the USB relay.
- Check whether the COM number changed.
- Restart this application if needed.

### COM port not found

- Check USB cable and USB port.
- Check Device Manager for unknown devices.
- Install the relay driver.
- Update the `COM Port` field after reconnecting the device.

### Relay moves but G2000 does not stop

- Verify the real contact with a multimeter.
- Check whether the wiring uses `COM-NO` or `COM-NC`.
- Confirm the relay contact is actually in the G2000 external interlock / Not-Aus loop.
- Confirm the stop command sets the relay to the intended state.
- Do not continue plasma tests until the interlock behavior is verified without plasma.

### G2000 never allows start

- The contact may be wired opposite to the required logic.
- The relay initial state may be wrong.
- Try the other contact pair, `COM-NO` or `COM-NC`, after confirming with the manual.
- Check whether `Stop HEX` and `Reset HEX` are swapped for the relay board.
- Use a multimeter to confirm that reset returns the interlock loop to the allowed state.

### Over-temperature trip cannot reset

- This is expected if the current reading is still above threshold.
- Reset requires a valid temperature below threshold.
- If OCR is stuck at `NO READING`, fix OCR/ROI first so the app can confirm the temperature is below threshold.

## Configuration File

The app creates and updates `appsettings.json` next to the executable.

Important fields:

- `ThresholdC`: default `90.0`.
- `PollIntervalMs`: default `1000`.
- `WindowTitleContains`: finds HikmicroAnalyzer by title.
- `Roi`: saved after `Select ROI`.
- `Ocr.TesseractExePath`: `tesseract.exe` or full path.
- `Ocr.Language`: default `eng`.
- `Relay.DryRun`: default `true`.
- `Relay.PortName`: default `COM3`.
- `Relay.BaudRate`: default `9600`.
- `Relay.StopCommandHex`: stop command bytes.
- `Relay.ResetCommandHex`: reset command bytes.
- `DataDirectory`: default `data`.
- `Language`: `en`, `zh-CN`, or `de`.
- `AutoResetEnabled`: default `true`.
- `RecoveryThresholdC`: default `85.0`.
- `RecoveryStableSeconds`: default `30`.

## Developer Test Command

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Expected current result:

```text
Passed: 20, Failed: 0, Skipped: 0
```
