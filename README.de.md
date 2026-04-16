# G2000 Temperatur-Soft-Interlock

Sprache: [English](README.md) | [中文](README.zh-CN.md) | [Deutsch](README.de.md)

Dies ist ein Windows-Desktop-Prototyp zur Temperaturüberwachung eines Labor-Plasmareaktors mit HIKMICRO-Kamera und HikmicroAnalyzer.

Die Software beobachtet das HikmicroAnalyzer-Fenster, nimmt einen Screenshot des ausgewählten Temperaturbereichs auf, liest den Text mit Tesseract OCR, erkennt die höchste sichtbare Temperatur, schreibt Messwerte in eine CSV-Datei und sendet bei `90.0 C` oder höher einen Befehl an ein USB-Seriell-Relais.

Dies ist ein Laborprototyp. Er soll helfen, den G2000 über den bestätigten externen Verriegelungs- oder Not-Aus-Kreis abzuschalten. Es handelt sich nicht um eine zertifizierte industrielle Sicherheitssteuerung.

## Systemübersicht

Das System besteht aus vier Teilen:

- HIKMICRO-Kamera und HikmicroAnalyzer: zeigt Wärmebild und genaue Temperatur-Overlay-Werte.
- Diese WPF-Anwendung: erfasst den Temperaturtext per Screenshot und entscheidet über den Trip.
- USB-Seriell-Relais: erhält HEX-Befehle vom Windows-PC.
- G2000 Verriegelung / Not-Aus: der Relaiskontakt wird als potentialfreier Trockenkontakt in den bestätigten externen Kreis eingebunden.

Die Software verwendet keine HIKMICRO-Kamera-API. Sie liest das sichtbare Overlay im HikmicroAnalyzer per OCR.

## Funktionsweise

1. Das Programm sucht ein sichtbares HikmicroAnalyzer-Fenster über den Text im Feld `Window`.
2. Der Benutzer wählt mit `Select ROI` den Bereich des maximalen Temperaturtexts.
3. Das Programm erstellt periodisch einen Screenshot dieses Bereichs.
4. Tesseract OCR wandelt den Screenshot in Text um.
5. Der Parser erkennt Werte wie `Max 89.9 C`, `90.1°C` oder `最高 90.0 C`.
6. Jede Messung wird in CSV geschrieben.
7. Bei einem gültigen Messwert `>= ThresholdC` wechselt die Software in `Tripped`.
8. In `Tripped` wird der Stop-Befehl einmal gesendet und der Zustand bleibt verriegelt.
9. Manueller Reset ist erst möglich, wenn ein gültiger Temperaturwert unterhalb des Schwellwerts gelesen wurde.
10. Wenn `Auto reset` aktiviert ist, sendet die Software den Relais-Reset automatisch, nachdem die Temperatur für die eingestellte stabile Zeit unter `Recovery C` geblieben ist.

Wenn OCR fehlschlägt, wird `NO READING` protokolliert. Ein einzelner `NO READING`-Wert löst das Relais nicht aus.

## Repository-Struktur

- `src/ReactorSoftInterlock.sln`: Visual Studio / .NET Solution.
- `src/ReactorSoftInterlock.Wpf`: WPF-Desktopanwendung.
- `src/ReactorSoftInterlock.Domain`: Schwellwert, Messwert, Relaisaktion und verriegelte Trip-Logik.
- `src/ReactorSoftInterlock.Application`: Überwachungsschleife, OCR-Textparser und Schnittstellen.
- `src/ReactorSoftInterlock.Infrastructure`: Screenshot, Tesseract, serielles Relais, CSV, Einstellungen.
- `tests/ReactorSoftInterlock.Tests`: Tests für Kernlogik.
- `docs/g2000-soft-interlock.md`: zusätzliche Hardware-Hinweise.
- `Doc`: G2000-Handbuch und Projekt-PDFs.
- `HikmicroAnalyzer`: HIKMICRO-Beispielbilder.

## Voraussetzungen

Entwicklungs-PC:

- Windows 10 oder neuer.
- Visual Studio 2022 mit `.NET desktop development`, oder .NET 8 SDK.
- Internetzugang für den ersten NuGet-Restore.

Labor-PC:

- Windows 10 oder neuer.
- HikmicroAnalyzer läuft und zeigt den maximalen Temperaturwert sichtbar an.
- Tesseract OCR für Windows.
- USB-Seriell-Relais und passender Windows-Treiber.
- Bestätigter G2000 external interlock / Not-Aus Anschluss.
- Multimeter zur Prüfung der Relaiskontakte vor Anschluss an den G2000.

## Erforderliche Komponenten installieren

Für den Labor-PC möglichst das self-contained Release-Paket verwenden. Dieses Paket enthält die .NET Runtime, die die App benötigt. Deshalb muss auf dem Labor-PC normalerweise keine separate .NET Runtime installiert werden. Der Labor-PC benötigt trotzdem HikmicroAnalyzer, Tesseract OCR, den USB-Relais-Treiber und die korrekte Relais/G2000-Verdrahtung.

Installation auf dem Labor-PC:

1. HikmicroAnalyzer installieren und prüfen, dass Kamerabild und Maximaltemperatur-Overlay sichtbar sind.
2. Tesseract OCR für Windows installieren. Danach entweder den Tesseract-Installationsordner zu `PATH` hinzufügen oder den vollständigen Pfad zu `tesseract.exe` in das App-Feld `Tesseract` eintragen.
3. Den USB-Seriell-Relais-Treiber installieren. Häufige Relaisboards verwenden CH340, CH341, CP210x oder FTDI; den zum Board passenden Treiber verwenden.
4. Windows Geräte-Manager öffnen, `Ports (COM & LPT)` erweitern, das Relais einstecken und den angezeigten `COMx`-Wert notieren.
5. `Dry run relay` aktiviert lassen, bis OCR funktioniert und das Relais ohne G2000 getestet wurde.
6. Mit G2000-Handbuch und Laborverdrahtung bestätigen, ob der external interlock / Not-Aus Kreis geöffnet oder geschlossen werden muss, um Hochspannung zu sperren.

Installation auf einem Entwicklungs-PC:

1. Visual Studio 2022 installieren.
2. Im Visual Studio Installer den Workload `.NET desktop development` auswählen.
3. Sicherstellen, dass `.NET 8.0 SDK` in den Einzelkomponenten installiert ist, oder das .NET 8 SDK separat installieren.
4. Wenn Visual Studio die Solution als nicht unterstützt öffnet, `.NET desktop development` installieren, Visual Studio schließen und `src/ReactorSoftInterlock.sln` erneut öffnen.
5. Wenn Visual Studio nichtfunktionale Solution-Änderungen anbietet, kann das akzeptiert werden; Debug- und Release-Konfigurationen müssen aber erhalten bleiben.

## Aus dem Quellcode bauen

PowerShell im Repository-Wurzelverzeichnis öffnen:

```powershell
cd "L:\Documents\files\Yu Zhang TU Clausthal\ProjectShukang\raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor"
```

Abhängigkeiten wiederherstellen:

```powershell
dotnet restore .\src\ReactorSoftInterlock.sln
```

Tests ausführen:

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Release bauen:

```powershell
dotnet build .\src\ReactorSoftInterlock.sln -c Release
```

Die ausführbaren Dateien liegen danach hier:

```text
src\ReactorSoftInterlock.Wpf\bin\Release\net8.0-windows\
```

Das self-contained Paket für den Labor-PC erzeugen:

```powershell
dotnet publish .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\ReactorSoftInterlock-win-x64
```

Der veröffentlichte Ordner kann gezippt und auf den Labor-PC kopiert werden. Er enthält `ReactorSoftInterlock.Wpf.exe` und die von der App benötigten .NET Runtime-Dateien.

## Starten

Aus dem Quellcode starten:

```powershell
dotnet run --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

Release-Modus:

```powershell
dotnet run -c Release --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

Alternativ `src/ReactorSoftInterlock.sln` in Visual Studio 2022 öffnen, `ReactorSoftInterlock.Wpf` als Startprojekt auswählen und `F5` drücken.

Labor-Release-Paket starten:

1. `ReactorSoftInterlock-win-x64-latest.zip` entpacken.
2. `ReactorSoftInterlock.Wpf.exe` starten.
3. Falls Windows SmartScreen erscheint, zuerst bestätigen, dass die Datei aus diesem Projekt stammt.
4. `appsettings.json` neben der EXE belassen; die App aktualisiert diese Datei beim Speichern der Einstellungen.
5. Wenn die App meldet, dass Tesseract, ROI, HikmicroAnalyzer, COM-Port oder HEX-Befehle fehlen, diesen Punkt zuerst beheben und erst danach die Überwachung starten.

## Erste Software-Einrichtung

1. HikmicroAnalyzer starten.
2. Sicherstellen, dass der maximale Temperaturtext sichtbar ist.
3. HikmicroAnalyzer sichtbar lassen, am besten maximiert.
4. Diese Anwendung starten.
5. In `Window` einen stabilen Teil des Fenstertitels eintragen, zum Beispiel `Hikmicro`.
6. In `Tesseract` `tesseract.exe` oder den vollständigen Pfad eintragen.
7. `Dry run relay` aktiviert lassen.
8. `Save Settings` klicken.
9. `Select ROI` klicken.
10. Nur den maximalen Temperaturtext markieren.
11. `Start` klicken.
12. `Temperature`, `Raw OCR text` und die Historientabelle prüfen.

Die ROI hängt von Fensterposition, Fenstergröße und Windows-Skalierung ab. Nach Verschieben, Skalieren, Minimieren, Überdecken oder Änderung der Anzeige-Skalierung muss die ROI neu gewählt werden.

## Anschluss an den Plasmagenerator

Das Relais muss als passiver Kontakt verwendet werden. Weder PC, USB-Port, serielles Modul noch Software dürfen Spannung in G2000-Verriegelungsklemmen einspeisen.

Anschlussprinzip:

- Die USB-Seite des Relais wird mit dem Windows-PC verbunden.
- Die Kontaktseite des Relais wird nur als potentialfreier Trockenkontakt verwendet.
- Der Kontakt wird in den im Labor bestätigten G2000 external interlock / Not-Aus Kreis eingebunden.
- Im Normalzustand muss der Kontaktzustand den Hochspannungsbetrieb des G2000 erlauben.
- Im Trip-Zustand muss der Kontaktzustand den Hochspannungsbetrieb verhindern oder abschalten.

Diese README nennt keine festen G2000-Klemmennummern. Die Klemmen müssen anhand des G2000-Handbuchs und der realen Laborverdrahtung bestätigt werden.

Empfohlene Inbetriebnahme:

1. `Dry run relay` aktiviert lassen und nur OCR prüfen.
2. USB-Relais an den PC anschließen, aber noch nicht an den G2000.
3. COM-Port im Windows Device Manager finden.
4. `COM Port`, `Baud`, `Stop HEX` und `Reset HEX` eintragen.
5. Relaiskontakt weiter vom G2000 getrennt lassen.
6. `Dry run relay` deaktivieren.
7. `Test Relay Stop` klicken.
8. Mit dem Multimeter `COM-NO` und `COM-NC` prüfen.
9. Entscheiden, ob der Alarm den Kreis öffnen oder schließen muss.
10. Relaiskontakt in den bestätigten G2000-Verriegelungs- oder Not-Aus-Kreis einbinden.
11. G2000-Reaktion ohne Plasma oder in einem risikoarmen Zustand prüfen.
12. Erst danach für echte Übertemperatur-Trips verwenden.

Kontaktwahl:

- Wenn die G2000-Verriegelung geschlossen sein muss, um Betrieb zu erlauben, ist meist `COM` + `NC` passend, damit ein Relais-Trip den Kreis öffnet.
- Wenn Laborverdrahtung oder Relaislogik anders sind, `COM` + `NO` verwenden oder die Relaiszustände für `Stop HEX` und `Reset HEX` tauschen.
- Immer mit dem Multimeter prüfen. Nicht nur auf Relais-LEDs vertrauen.

## COM-Port einrichten

COM-Port finden:

1. Windows Device Manager öffnen.
2. `Ports (COM & LPT)` aufklappen.
3. USB-Relais abziehen.
4. USB-Relais wieder einstecken.
5. Beobachten, welcher `COMx` erscheint, zum Beispiel `COM3` oder `COM4`.
6. Genau diesen Wert in `COM Port` eintragen.

Baudrate:

- Zuerst das Handbuch des Relaisboards prüfen.
- Häufige Werte sind `9600` und `115200`.
- Der Wert `Baud` in der App muss zum Relaisboard passen.
- Wenn `Test Relay Stop` nichts bewirkt, zuerst die Baudrate prüfen, danach HEX-Befehle.

HEX-Befehle:

- `Stop HEX` wird beim Übertemperatur-Trip gesendet.
- `Reset HEX` wird beim manuellen Reset oder erfolgreichem Auto-Reset gesendet.
- Diese Befehle gehören zum externen USB-Relaisboard, nicht zum G2000 selbst.
- Die App lässt beide Befehle standardmäßig leer, weil die richtigen Bytes vom tatsächlichen externen Relaismodell abhängen.
- Nur als Beispiel: Übliche LCUS-1 / LCUS-2 / LC Technology USB-Seriell-Relaisboards mit CH340 USB-Seriell-Chip verwenden oft 9600 Baud, `A0 01 01 A2` für Kanal 1 ON und `A0 01 00 A1` für Kanal 1 OFF.
- Akzeptierte Formate:

```text
A0 01 01 A2
A0-01-01-A2
A0:01:01:A2
```

Wenn noch kein externes Relaisboard ausgewählt wurde, `Dry run relay` aktiviert lassen und den App-Ausgang nicht mit dem G2000 verbinden.

Optionale LCUS/CH340-Kanal-1-Verdrahtung, nur wenn genau dieses externe Relaisboard verwendet wird:

- Wenn der G2000-Interlock geöffnet werden muss, um Hochspannung zu stoppen, und `COM` + `NC` verwendet wird, zieht `Stop HEX = A0 01 01 A2` das Relais an und öffnet den NC-Kontakt.
- `Reset HEX = A0 01 00 A1` lässt das Relais abfallen und schließt `COM` + `NC` wieder.
- Wenn das Labor `COM` + `NO` verwendet, unbedingt mit dem Multimeter prüfen; Stop/Reset müssen eventuell getauscht werden.

G2000-Computersteuerung:

- Das G2000-Handbuch nennt industrielle Schnittstellen wie CAN und RS485. Diese sind G2000-native Schnittstellen und getrennt vom externen USB-Relaispfad.
- Dieser Prototyp implementiert noch keine G2000-CAN/RS485-Telegramme. Für direkte PC-Steuerung des G2000 müssen zuerst die Protokollseiten aus dem Handbuch ausgewertet werden: Steckerbelegung, Bustyp, Baudrate, Node-Adresse, Nachrichten-/Registermap, Enable-Befehl, Stop-Befehl, Statuswort und mögliche Watchdog- oder zyklische Telegrammpflichten.
- Bis dieses Protokoll implementiert und getestet ist, bleibt der empfohlene Stop-Pfad die externe Interlock-/Not-Aus-Schleife, weil sie mit dem Multimeter überprüfbar ist und nicht vom G2000-Softwaremodus abhängt.

Dry Run:

- Aktiviert: die App simuliert Relaisaktionen und öffnet keinen COM-Port.
- Deaktiviert: die App öffnet den COM-Port und sendet die konfigurierten HEX-Bytes.

## Checkliste vor dem Experiment

- HikmicroAnalyzer ist geöffnet.
- Maximaler Temperaturtext ist sichtbar.
- HikmicroAnalyzer ist nicht minimiert.
- Kein anderes Fenster verdeckt den Temperaturtext.
- ROI wurde für die aktuelle Fensterposition neu geprüft.
- `Temperature` zeigt einen plausiblen Wert.
- CSV-Protokollierung funktioniert.
- `Dry run relay` passt zur aktuellen Testphase.
- `Test Relay Stop` wurde mit Multimeter geprüft.
- G2000-Verriegelungsreaktion wurde in einem risikoarmen Zustand geprüft.

## Während des Experiments

- HikmicroAnalyzer nicht verschieben oder skalieren.
- HikmicroAnalyzer nicht minimieren.
- Temperaturtext nicht verdecken.
- Bei unplausibler Temperatur `Raw OCR text` prüfen.
- Bei wiederholtem `NO READING` zuerst ROI/OCR korrigieren, bevor die Software weiter als Schutz verwendet wird.

## Nach einem Trip

1. Prüfen, dass Plasma gestoppt wurde oder der gewünschte sichere Zustand erreicht ist.
2. CSV-Protokoll sichern.
3. Warten, bis die App einen gültigen Wert unterhalb des Schwellwerts liest.
4. Wenn `Auto reset` deaktiviert ist, `Reset` klicken.
5. Wenn `Auto reset` aktiviert ist, prüfen, dass der Relais-Reset erst nach stabiler Temperatur unter `Recovery C` erfolgt.
6. OCR und Relaiszustand erneut prüfen, bevor fortgefahren wird.

## Status Bedeutung

- `Monitoring`: gültige Temperatur unterhalb des Schwellwerts.
- `NoReading`: OCR hat keine gültige Temperatur geliefert. Der Messwert wird protokolliert, löst aber keinen Relais-Trip aus.
- `Tripped`: Schwellwert erreicht oder überschritten. Stop-Befehl wurde einmal gesendet und der Zustand ist verriegelt.
- `RelayTestFailed`: Relaistest fehlgeschlagen oder Ausnahme aufgetreten.

## CSV-Protokollierung

Standardpfad:

```text
src\ReactorSoftInterlock.Wpf\bin\<Configuration>\net8.0-windows\data\temperature-history.csv
```

Spalten:

```text
timestamp,temperature_c,raw_ocr_text,status,alarm_reason,relay_action,screenshot_roi
```

Mit `Export CSV` kann die aktuelle CSV-Datei an einen gewählten Ort kopiert werden.

## Fehlerbehebung

### HikmicroAnalyzer-Fenster wird nicht gefunden

- Prüfen, ob HikmicroAnalyzer geöffnet ist.
- Prüfen, ob `Window` einen Teil des Fenstertitels enthält.
- HikmicroAnalyzer nicht minimieren.
- Einen kürzeren Filter wie `Hikmicro` versuchen.

### OCR zeigt `NO READING`

- `Select ROI` erneut ausführen.
- Nur den maximalen Temperaturtext wählen, nicht das ganze Bild.
- HikmicroAnalyzer maximieren.
- Fenster entfernen, die den Temperaturtext verdecken.
- Pfad zu `Tesseract` prüfen.
- Nach Änderung der Windows-Anzeigeskalierung ROI neu wählen.

### Temperatur wird falsch erkannt

- ROI kleiner wählen.
- Temperatur-Overlay scharf und kontrastreich halten.
- Keine Labels, Uhrzeiten oder andere Zahlen in die ROI aufnehmen.
- `Raw OCR text` notieren, damit der Parser später verbessert werden kann.

### `Test Relay Stop` bewirkt nichts

- Prüfen, ob `Dry run relay` noch aktiviert ist.
- COM-Port im Device Manager bestätigen.
- USB-Relais-Treiber prüfen.
- Programme schließen, die den seriellen Port belegen.
- `Baud` mit dem Relaisboard abgleichen.
- Prüfen, ob `Stop HEX` nicht leer ist und zum Relaisprotokoll passt.

### COM port access denied

- Arduino Serial Monitor, PuTTY, serielle Assistenten oder Hersteller-Tools schließen.
- USB-Relais abziehen und wieder einstecken.
- Prüfen, ob sich die COM-Nummer geändert hat.
- Anwendung bei Bedarf neu starten.

### COM port not found

- USB-Kabel und USB-Port prüfen.
- Im Device Manager nach unbekannten Geräten suchen.
- Relais-Treiber installieren oder aktualisieren.
- `COM Port` neu eintragen.

### Relais schaltet, aber G2000 stoppt nicht

- Kontakt mit Multimeter prüfen.
- Prüfen, ob `COM-NO` oder `COM-NC` verwendet wird.
- Prüfen, ob der Kontakt wirklich im G2000 external interlock / Not-Aus Kreis liegt.
- Prüfen, ob der Stop-Befehl den Relaiskontakt in den richtigen Zustand bringt.
- Keine weiteren Plasma-Tests durchführen, bis die Verriegelung ohne Plasma verifiziert ist.

### G2000 erlaubt keinen Start

- Kontaktlogik kann umgekehrt sein.
- Anfangszustand des Relais kann falsch sein.
- Nach Prüfung des Handbuchs `COM-NO` oder `COM-NC` wechseln.
- Prüfen, ob `Stop HEX` und `Reset HEX` für dieses Relaisboard vertauscht werden müssen.
- Mit Multimeter prüfen, ob Reset den Verriegelungskreis in den erlaubten Zustand zurücksetzt.

### Trip kann nicht zurückgesetzt werden

- Wenn die aktuelle Temperatur noch über dem Schwellwert liegt, ist das erwartetes Verhalten.
- Reset erfordert einen gültigen Temperaturwert unterhalb des Schwellwerts.
- Wenn dauerhaft `NO READING` angezeigt wird, zuerst OCR/ROI korrigieren.

## Konfigurationsdatei

Die App erstellt und aktualisiert `appsettings.json` neben der ausführbaren Datei.

Wichtige Felder:

- `ThresholdC`: Standard `90.0`.
- `PollIntervalMs`: Standard `1000`.
- `WindowTitleContains`: findet HikmicroAnalyzer über Fenstertitel.
- `Roi`: gespeichert nach `Select ROI`.
- `Ocr.TesseractExePath`: `tesseract.exe` oder voller Pfad.
- `Ocr.Language`: Standard `eng`.
- `Relay.DryRun`: Standard `true`.
- `Relay.PortName`: Standard `COM3`.
- `Relay.BaudRate`: Standard `9600`.
- `Relay.StopCommandHex`: standardmäßig leer; aus dem Handbuch des externen Relais eintragen.
- `Relay.ResetCommandHex`: standardmäßig leer; aus dem Handbuch des externen Relais eintragen.
- `DataDirectory`: Standard `data`.
- `Language`: `en`, `zh-CN` oder `de`.
- `AutoResetEnabled`: Standard `true`.
- `RecoveryThresholdC`: Standard `85.0`.
- `RecoveryStableSeconds`: Standard `30`.

## Entwicklertest

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Aktuell erwartet:

```text
Passed: 20, Failed: 0, Skipped: 0
```
