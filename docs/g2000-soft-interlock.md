# G2000 Temperature Soft Interlock

This prototype monitors the HikmicroAnalyzer window with OCR and trips an external relay when the detected maximum temperature reaches the configured threshold.

## Hardware Integration

Use a USB serial relay as a dry contact in the G2000 external interlock / emergency-stop loop. The G2000 manual describes an external `Verriegelung (Interlock Schnittstelle)` intended for external safety or emergency-stop circuits. This is the preferred prototype stop path because it does not require implementing CAN/RS485 control frames and keeps the trip behavior independent of the G2000 control mode.

Before running with plasma enabled:

1. Wire the relay contact only as a passive contact.
2. Confirm the relay stop action opens the interlock circuit.
3. Verify with a multimeter that `Test Relay Stop` changes the relay contact as expected.
4. Confirm the G2000 disables high voltage when that contact opens.

## Windows Setup

Install:

1. Visual Studio 2022 with `.NET desktop development`, or .NET 8 SDK.
2. Tesseract OCR for Windows.
3. The USB relay driver so the relay appears as a COM port.

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
- `Relay.DryRun`: keep `true` until the relay wiring has been verified.
- `Relay.PortName`: for example `COM3`.
- `Relay.BaudRate`: for example `9600`.
- `Relay.StopCommandHex`: bytes sent on over-temperature trip.
- `Relay.ResetCommandHex`: bytes sent after manual reset.

## Normal Use

1. Start HikmicroAnalyzer and make sure the maximum temperature text is visible.
2. Start this application.
3. Set the window title filter, Tesseract path, relay settings, and threshold.
4. Click `Select ROI` and drag over the maximum temperature text.
5. Keep relay in dry-run mode and click `Start`.
6. Once OCR is stable, configure the real COM port and HEX commands.
7. Click `Test Relay Stop`.
8. Disable dry-run only after the relay contact has been verified.

When a reading is `>= 90.0 C`, the app enters `Tripped`, sends the stop relay command once, records the event to CSV, and stays latched until a manual reset is performed below the threshold.
