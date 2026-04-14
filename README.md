# G2000 Temperature Soft Interlock

Windows desktop prototype for monitoring a laboratory plasma reactor with a HIKMICRO infrared camera workflow.

The prototype watches the HikmicroAnalyzer window, takes a screenshot of the user-selected temperature text area, reads the text with Tesseract OCR, parses the highest visible temperature, writes every sample to CSV, and trips a USB serial relay when the temperature reaches or exceeds the configured threshold. The default threshold is `90.0 C`.

The relay is intended to be wired as a passive dry contact into the G2000 external interlock / emergency-stop loop. The project does not use the HIKMICRO camera API because the current portable camera workflow exposes the needed temperature information through the HikmicroAnalyzer UI.

## Repository Layout

- `src/ReactorSoftInterlock.sln`: Visual Studio / .NET solution.
- `src/ReactorSoftInterlock.Wpf`: WPF desktop application.
- `src/ReactorSoftInterlock.Domain`: threshold, sample, relay action, and latched trip state logic.
- `src/ReactorSoftInterlock.Application`: monitoring loop, OCR text parsing, and application ports.
- `src/ReactorSoftInterlock.Infrastructure`: window capture, Tesseract CLI adapter, serial relay, CSV log, settings.
- `tests/ReactorSoftInterlock.Tests`: unit and integration-style tests for the core logic.
- `docs/g2000-soft-interlock.md`: hardware notes and lab operating workflow.
- `Doc`: project and G2000 manual PDFs.
- `HikmicroAnalyzer`: sample HIKMICRO images for reference.

## What The App Does

1. Finds a visible HikmicroAnalyzer window by title text.
2. Lets the user drag-select the region of interest (ROI) containing the maximum temperature text.
3. Periodically captures that ROI from the screen.
4. Runs Tesseract OCR on the captured image.
5. Parses values such as `Max 89.9 C`, `90.1°C`, or `最高 90.0 C`.
6. Logs every reading, OCR text, status, relay action, and ROI to CSV.
7. Enters `Tripped` when any valid reading is `>= ThresholdC`.
8. Sends the relay stop command once and stays latched until a manual reset below the threshold.

If OCR fails or no valid temperature is found, the app records `NO READING` and does not trip the relay from that sample.

## Requirements

Development machine:

- Windows 10 or later.
- Visual Studio 2022 with `.NET desktop development`, or .NET 8 SDK.
- Internet access for first-time NuGet package restore.

Runtime / lab machine:

- Windows 10 or later.
- HikmicroAnalyzer running with the maximum temperature overlay visible.
- Tesseract OCR for Windows.
- USB serial relay and its Windows driver, if using real relay output.
- G2000 interlock wiring prepared according to the lab setup.

Install Tesseract:

1. Install a Windows Tesseract build.
2. Either add `tesseract.exe` to `PATH`, or put the full path into the app's `Tesseract` field.
3. English OCR data is enough for the default configuration because the parser mainly needs digits and `C`.

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

Build Debug:

```powershell
dotnet build .\src\ReactorSoftInterlock.sln
```

Build Release:

```powershell
dotnet build .\src\ReactorSoftInterlock.sln -c Release
```

The WPF executable is produced under:

```text
src\ReactorSoftInterlock.Wpf\bin\Release\net8.0-windows\
```

## Run From Source

From the repository root:

```powershell
dotnet run --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

For Release mode:

```powershell
dotnet run -c Release --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

You can also open `src/ReactorSoftInterlock.sln` in Visual Studio 2022, set `ReactorSoftInterlock.Wpf` as the startup project, and press `F5`.

## First Run Workflow

1. Start HikmicroAnalyzer.
2. Make the maximum temperature overlay visible.
3. Keep HikmicroAnalyzer visible, preferably maximized.
4. Start `ReactorSoftInterlock.Wpf`.
5. Set `Window` to a stable substring of the HikmicroAnalyzer window title, for example `Hikmicro`.
6. Set `Tesseract` to `tesseract.exe` or the full executable path.
7. Keep `Dry run relay` enabled for the first OCR test.
8. Click `Save Settings`.
9. Click `Select ROI`.
10. Drag only around the maximum temperature text and release.
11. Click `Start`.
12. Watch `Temperature`, `Raw OCR text`, and the history table.

The ROI is screen/window-position dependent. If HikmicroAnalyzer is moved, resized, minimized, hidden behind another window, or the Windows display scale changes, select the ROI again.

## Relay Configuration

The first version intentionally uses configurable HEX commands instead of a hard-coded relay model.

Fields:

- `Dry run relay`: when checked, the app simulates relay actions and does not write to the serial port.
- `COM Port`: Windows serial port, for example `COM3`.
- `Baud`: relay baud rate, for example `9600`.
- `Stop HEX`: bytes sent when the temperature trips the interlock.
- `Reset HEX`: bytes sent when the operator resets the relay from the UI.

Example HEX formats accepted by the parser:

```text
A0 01 00 A1
A0-01-00-A1
A0:01:00:A1
```

Recommended commissioning order:

1. Keep `Dry run relay` enabled.
2. Verify OCR is stable.
3. Connect the USB relay and confirm the COM port in Windows Device Manager.
4. Enter the relay's stop/reset HEX commands.
5. Click `Test Relay Stop`.
6. Verify the relay contact changes with a multimeter.
7. Wire the relay contact as a passive contact in the G2000 external interlock / emergency-stop loop.
8. Disable `Dry run relay` only after the contact behavior has been verified.

## Normal Lab Operation

Before enabling plasma:

1. Confirm HikmicroAnalyzer is visible and not covered by other windows.
2. Confirm the app shows a valid live temperature.
3. Confirm the threshold is `90.0 C` unless intentionally changed.
4. Confirm `Test Relay Stop` disables or opens the intended G2000 interlock path.
5. Click `Start`.

During operation:

- `Monitoring`: valid readings below threshold.
- `NoReading`: OCR did not produce a valid temperature; sample is logged, relay is not tripped from that sample.
- `Tripped`: threshold reached or exceeded; stop command has been sent once and the state is latched.

After a trip:

1. Stop or cool the process as required by the lab procedure.
2. Wait until the app reads a valid temperature below the threshold.
3. Click `Reset`.
4. Start monitoring again if needed.

## CSV Logging

The app writes history to:

```text
src\ReactorSoftInterlock.Wpf\bin\<Configuration>\net8.0-windows\data\temperature-history.csv
```

CSV columns:

```text
timestamp,temperature_c,raw_ocr_text,status,alarm_reason,relay_action,screenshot_roi
```

Use `Export CSV` in the UI to copy the current history file to a selected location.

## Configuration File

The app creates and updates `appsettings.json` next to the executable.

Important fields:

- `ThresholdC`: default `90.0`.
- `PollIntervalMs`: default `1000`.
- `WindowTitleContains`: window title filter used to find HikmicroAnalyzer.
- `Roi`: saved after `Select ROI`.
- `Ocr.TesseractExePath`: `tesseract.exe` or full path.
- `Ocr.Language`: default `eng`.
- `Relay.DryRun`: default `true`.
- `Relay.PortName`: default `COM3`.
- `Relay.BaudRate`: default `9600`.
- `Relay.StopCommandHex`: stop command bytes.
- `Relay.ResetCommandHex`: reset command bytes.
- `DataDirectory`: default `data`.

## Test Coverage

Current tests cover:

- OCR temperature text parsing.
- `89.9 C` below-threshold behavior.
- `90.0 C` and `90.1 C` trip behavior.
- Latched trip sends stop only once.
- Reset is allowed only below threshold with a valid reading.
- HEX relay command parsing.
- CSV header and sample output.
- Monitoring service integration with fake OCR reader and fake relay.

Run:

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Expected current result:

```text
Passed: 16, Failed: 0, Skipped: 0
```

## Notes

- HikmicroAnalyzer does not need to be maximized, but it must stay visible and stable. Maximized is recommended for lab use.
- Do not minimize HikmicroAnalyzer while monitoring.
- Do not cover the selected temperature text with another window.
- If OCR is unreliable, reselect a tighter ROI around the temperature text.
- If the HikmicroAnalyzer theme or overlay changes, reselect ROI and verify the raw OCR text before enabling the real relay.

More hardware-specific notes are in `docs/g2000-soft-interlock.md`.
