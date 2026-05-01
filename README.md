# G2000 Temperature Soft Interlock

Language: [English](README.md) | [中文](README.zh-CN.md) | [Deutsch](README.de.md)

Windows desktop prototype for monitoring a laboratory plasma reactor with a HIKMICRO infrared camera workflow.

The software watches the HikmicroAnalyzer window, captures a selected temperature text area, reads it with Tesseract OCR, parses the highest visible temperature, logs readings to CSV, and sends a USB serial relay command when the temperature reaches or exceeds `90.0 C`.

This project is a laboratory prototype. It is intended to help stop the plasma generator by opening or switching the confirmed G2000 external interlock / emergency-stop path. It is not a certified industrial safety controller.

## System Overview

The full setup has four parts:

- HIKMICRO camera and HikmicroAnalyzer: displays the live thermal image and accurate temperature overlay.
- This WPF application: captures the temperature text from the screen and decides whether to trip.
- USB serial relay: receives serial relay commands from the Windows PC. In the current `relay-interlock-test` branch, the tested board is `DSD TECH SH-UR04A` and the commands are ASCII AT commands.
- G2000 plasma generator interlock / Not-Aus loop: the relay contact is wired as a passive dry contact into the confirmed external interlock path.

The app does not use a HIKMICRO API. It reads the visible HikmicroAnalyzer overlay by screenshot OCR.

## Current Verified Lab Branch

The current `relay-interlock-test` branch is already aligned with the relay that was physically tested on the lab bench:

- Relay board: `DSD TECH SH-UR04A 4CH`
- USB chip / driver: `Silicon Labs CP210x`
- COM port seen during validation: `COM3`
- Relay protocol: ASCII AT commands over `9600 8N1`
- Verified channel 1 behavior:
  - `AT+CH1=1` -> CH1 ON -> `COM1-NO1` closed
  - `AT+CH1=0` -> CH1 OFF -> `COM1-NC1` closed
- Verified G2000 interlock test path:
  - `G2000 I1 -> COM1`
  - `G2000 I2 -> NO1`

This branch now defaults to a four-channel engineering relay bank:

- CH1 -> Interlock A negative -> `I1-I2`
- CH2 -> Interlock A positive -> `I5-I6`
- CH3 -> Interlock B negative -> `I3-I4`
- CH4 -> Interlock B positive -> `I7-I8`
- Trip action: open all enabled channels
- Restore action: close all enabled channels

This branch is also being extended for `AMC2100` gas mass flow control:

- Interface: `RS485 Modbus RTU`
- Relevant DB9 pins:
  - Pin 3 = `RS485B`
  - Pin 4 = `RS485A`
  - Pin 5 = `+24V`
  - Pin 6 = `GND`
- Default slave address: `1`
- Default baud rate: `19200`
- Relevant registers:
  - `0-1` = actual flow, `32-bit float`
  - `2-3` = set flow, `32-bit float`
  - `11` = control mode, `1` = digital control, `2` = analog voltage control

The current software-side AMC2100 logic is:

- On trip, write the AMC2100 set-flow register to `0`
- Then open the G2000 interlock channels
- On reset / auto reset, close the interlock channels first, then restore the AMC2100 set flow from the cached previous setpoint or from a configured fallback setpoint
- The top dashboard also shows a live `Gas Flow` readout from the AMC2100 actual-flow registers over RS485

This means the software can implement AMC2100 gas start/stop in software. It does not mean the device has a separate documented hard-reset register. In the current prototype, "software reset" for AMC2100 means "write 0 to stop gas, then write back the desired setpoint to resume flow."

In bench testing, the AMC2100 local panel may continue to show `0` or may not visibly refresh the setpoint unless you navigate on the device itself. For RS485 validation in this branch, use the software `Gas Flow` card as the primary live indicator.

For a practical software-driven validation flow, use the engineering checklist:

- [docs/Engineering-Checklist.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.md)
- [docs/Engineering-Checklist.zh-CN.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.zh-CN.md)
- [docs/Engineering-Checklist.de.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.de.md)

## How The App Works

1. It finds a visible HikmicroAnalyzer window using the `Window` title filter.
2. The user selects a region of interest (ROI) around the maximum temperature text.
3. The app periodically screenshots that ROI.
4. Tesseract OCR converts the screenshot to text.
5. The parser extracts temperatures such as `Max 89.9 C`, `90.1°C`, or `最高 90.0 C`.
6. Every sample is written to CSV.
7. When a valid temperature is `>= ThresholdC`, the app enters `Tripped`.
8. In `Tripped`, the software opens all enabled interlock channels once and the state stays latched.
9. Manual reset is only allowed after a valid temperature below the threshold is read.
10. If `Auto reset` is enabled, the relay bank is closed automatically after the temperature stays below `Recovery C` for the configured stable time.

If OCR fails, the app records `NO READING`. A `NO READING` sample does not trigger the relay by itself.

## Repository Layout

- `src/ReactorSoftInterlock.sln`: Visual Studio / .NET solution.
- `src/ReactorSoftInterlock.Wpf`: WPF desktop application.
- `src/ReactorSoftInterlock.Domain`: threshold, sample, relay action, and latched trip logic.
- `src/ReactorSoftInterlock.Application`: monitoring loop, OCR text parsing, and ports.
- `src/ReactorSoftInterlock.Infrastructure`: screenshot capture, Tesseract adapter, serial relay, CSV log, settings.
- `tests/ReactorSoftInterlock.Tests`: tests for parsing, trip logic, CSV, relay command parsing, and monitoring behavior.
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
2. Install Tesseract OCR for Windows. A community Windows build such as the UB Mannheim installer is acceptable for this lab prototype. Install it to a normal Windows folder such as `C:\Program Files\Tesseract-OCR`. Do not point the installer directly into `artifacts\...`.
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

For this branch, the preferred deployment folder and zip name are:

```text
artifacts\ReactorSoftInterlock-win-x64-dsd-offline\
artifacts\ReactorSoftInterlock-win-x64-dsd-offline.zip
```

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

1. Unzip `ReactorSoftInterlock-win-x64-dsd-offline.zip`.
2. Run `ReactorSoftInterlock.Wpf.exe`.
3. If Windows SmartScreen appears, confirm that the file came from this project before continuing.
4. Keep `appsettings.json` next to the executable; the app updates it when settings are saved.
5. If the app warns that Tesseract, ROI, HikmicroAnalyzer, COM port, or relay commands are missing, fix that item before starting monitoring.

## First Software Setup

1. Start HikmicroAnalyzer.
2. Make sure the maximum temperature text is visible.
3. Keep HikmicroAnalyzer visible, preferably maximized.
4. Start this application.
5. Set `Window` to part of the HikmicroAnalyzer title, for example `Hikmicro`.
6. Set `Tesseract` to one of these:
   - keep the default `offline-deps\tesseract\tesseract.exe` if a full portable Tesseract folder is bundled with the release,
   - or set the full installed path such as `C:\Program Files\Tesseract-OCR\tesseract.exe`.
7. Keep `Dry run relay` checked.
8. Click `Save Settings`.
9. Click `Select ROI`.
10. Drag only around the maximum temperature text.
11. Click `Start`.
12. Check `Temperature`, `Raw OCR text`, the chart, and the history table.
13. Use the `Engineering` tab only for relay and interlock validation.

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
4. Fill in `COM Port`, `Baud`, `Stop command`, and `Reset command`.
5. Keep the relay contact unconnected from the G2000.
6. Uncheck `Dry run relay`.
7. Click `Test Relay Stop`.
8. Use a multimeter to check the relay contact state between `COM-NO` and `COM-NC`.
9. Decide whether the G2000 interlock must open on alarm or close on alarm.
10. Connect the relay contact into the confirmed G2000 interlock / Not-Aus loop.
11. Test G2000 response in a low-risk state before running plasma.
12. Only then use the relay for real over-temperature trips.

Recommended engineering mapping for the current branch:

- CH1 -> Interlock A negative -> `I1-I2`
- CH2 -> Interlock A positive -> `I5-I6`
- CH3 -> Interlock B negative -> `I3-I4`
- CH4 -> Interlock B positive -> `I7-I8`
- Use `COMx` + `NOx` so that the branch is closed when the relay channel is ON and opened when the relay channel is OFF.
- Monitoring mode in this branch is designed to open all enabled channels on trip and close all enabled channels on restore.
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
- If `Test Relay Stop` does nothing, try the documented baud rate before changing relay commands.

Relay behavior in this branch:

- The main window no longer expects ordinary users to edit low-level AT strings.
- The `Engineering` tab provides grouped actions:
  - `Disconnect All Interlocks`
  - `Connect All Interlocks`
  - per-channel `Disconnect` / `Connect`
- `Advanced Settings` keeps full engineering access to:
  - baud rate
  - CH1-CH4 open / close commands
  - per-channel enable flags
  - restore DSD defaults
- These commands belong to the external USB relay board, not to the G2000 itself.
- In the current `relay-interlock-test` branch, the tested relay is `DSD TECH SH-UR04A`.
- Default serial settings:
  - `COM Port = COM3` during the original validation
  - `Baud = 9600`
  - `Data bits = 8`
  - `Parity = None`
  - `Stop bits = 1`
- Default per-channel commands:

```text
CH1 open  = AT+CH1=0
CH1 close = AT+CH1=1
CH2 open  = AT+CH2=0
CH2 close = AT+CH2=1
CH3 open  = AT+CH3=0
CH3 close = AT+CH3=1
CH4 open  = AT+CH4=0
CH4 close = AT+CH4=1
```

Current UI structure in this branch:

- `Monitor` tab:
  - daily setup
  - OCR monitoring
  - chart and history
  - grouped relay actions
- `Engineering` tab:
  - explicit interlock test checklist
  - single-channel buttons for CH1-CH4
  - grouped connect / disconnect actions
- `Tools -> Advanced Settings`:
  - full relay command editing for engineering use only

- The configuration keys in `appsettings.json` are still named `StopCommandHex` and `ResetCommandHex` for backward compatibility, but in this branch they now store ASCII AT commands rather than hexadecimal bytes.

If no external relay board has been selected yet, keep `Dry run relay` checked and do not connect the app output to the G2000.

G2000 computer control:

- The G2000 manual mentions industrial control interfaces such as CAN and RS485. Those are G2000-native interfaces and are separate from the external USB relay path.
- This prototype does not yet implement G2000 CAN/RS485 frames. To control the G2000 directly from the PC, the exact protocol pages from the G2000 manual must be decoded first: connector pinout, bus type, baud rate, node address, message/register map, enable command, stop command, status word, and any watchdog or cyclic telegram requirement.
- Until that protocol is implemented and tested, the recommended stop path remains the external interlock / Not-Aus circuit, because it can be verified with a multimeter and does not depend on G2000 software mode.

Dry Run behavior:

- Checked: the app simulates relay actions and does not open the COM port.
- Unchecked: the app opens the COM port and sends the configured relay commands.

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
- Confirm `Stop command` is not empty and matches the relay protocol, for example `AT+CH1=0` for the tested DSD board.

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
- Check whether `Stop command` and `Reset command` are swapped for the relay board.
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
- `Ocr.TesseractExePath`: `offline-deps\tesseract\tesseract.exe`, `tesseract.exe`, or a full installed path. The app also checks common Windows Tesseract install locations such as `C:\Program Files\Tesseract-OCR\tesseract.exe`.
- `Ocr.Language`: default `eng`.
- `Relay.DryRun`: default `true`.
- `Relay.PortName`: default `COM3`.
- `Relay.BaudRate`: default `9600`.
- `Relay.StopCommandHex`: in this branch stores the stop relay command text. Default: `AT+CH1=0`.
- `Relay.ResetCommandHex`: in this branch stores the reset relay command text. Default: `AT+CH1=1`.
- `DataDirectory`: default `data`.
- `Language`: `en`, `zh-CN`, or `de`.
- `AutoResetEnabled`: default `true`.
- `RecoveryThresholdC`: default `85.0`.
- `RecoveryStableSeconds`: default `30`.

## Developer Test Command

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Expected result: all tests should pass.
