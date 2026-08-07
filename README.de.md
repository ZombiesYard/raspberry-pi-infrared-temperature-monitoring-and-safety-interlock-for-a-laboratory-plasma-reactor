# G2000 Temperatur-Soft-Interlock

Sprache: [English](README.md) | [中文](README.zh-CN.md) | [Deutsch](README.de.md)

Dies ist ein Windows-Desktop-Prototyp zur Temperaturüberwachung eines Labor-Plasmareaktors mit HIKMICRO-Kamera und HikmicroAnalyzer.

Die Software beobachtet das HikmicroAnalyzer-Fenster, nimmt einen Screenshot des ausgewählten Temperaturbereichs auf, liest den Text mit Tesseract OCR, erkennt die höchste sichtbare Temperatur, schreibt Messwerte in eine CSV-Datei und sendet bei `90.0 C` oder höher einen Befehl an ein USB-Seriell-Relais.

Dies ist ein Laborprototyp. Er soll helfen, den G2000 über den bestätigten externen Verriegelungs- oder Not-Aus-Kreis abzuschalten. Es handelt sich nicht um eine zertifizierte industrielle Sicherheitssteuerung.

## Systemübersicht

Das System besteht aus vier Teilen:

- HIKMICRO-Kamera und HikmicroAnalyzer: zeigt Wärmebild und genaue Temperatur-Overlay-Werte.
- Diese WPF-Anwendung: erfasst den Temperaturtext per Screenshot und entscheidet über den Trip.
- USB-Seriell-Relais: erhält Relaisbefehle vom Windows-PC. Im aktuellen Branch `relay-interlock-test` wurde das Relais `DSD TECH SH-UR04A` mit ASCII-AT-Befehlen verifiziert.
- G2000 Verriegelung / Not-Aus: der Relaiskontakt wird als potentialfreier Trockenkontakt in den bestätigten externen Kreis eingebunden.

Die Software verwendet keine HIKMICRO-Kamera-API. Sie liest das sichtbare Overlay im HikmicroAnalyzer per OCR.

## Aktuell validierter Branch

Der aktuelle Branch `relay-interlock-test` ist bereits auf das im Labor getestete Relais abgestimmt:

- Relaisboard: `DSD TECH SH-UR04A 4CH`
- USB-Chip / Treiber: `Silicon Labs CP210x`
- Validierter COM-Port: `COM3`
- Protokoll: ASCII-AT-Befehle über `9600 8N1`
- Verifiziertes Verhalten von Kanal 1:
  - `AT+CH1=1` -> CH1 EIN -> `COM1-NO1` geschlossen
  - `AT+CH1=0` -> CH1 AUS -> `COM1-NC1` geschlossen
- Verifizierte G2000-Interlock-Verdrahtung:
  - `G2000 I1 -> COM1`
  - `G2000 I2 -> NO1`

Deshalb verwendet dieser Branch jetzt standardmaessig eine Engineering-Relaisbank mit vier Kanaelen:

- CH1 -> Interlock A negativ -> `I1-I2`
- CH2 -> Interlock A positiv -> `I5-I6`
- CH3 -> Interlock B negativ -> `I3-I4`
- CH4 -> Interlock B positiv -> `I7-I8`
- Trip-Aktion: alle aktivierten Kanaele oeffnen
- Restore-Aktion: alle aktivierten Kanaele schliessen

Dieser Branch wird jetzt auch fuer den `AMC2100` Gas-Massendurchflussregler erweitert:

- Schnittstelle: `RS485 Modbus RTU`
- Relevante DB9-Pins:
  - Pin 3 = `RS485B`
  - Pin 4 = `RS485A`
  - Pin 5 = `+24V`
  - Pin 6 = `GND`
- Standard-Slave-Adresse: `1`
- Standard-Baudrate: `19200`
- Wichtige Register:
  - `0-1` = Ist-Durchfluss, `32-bit float`
  - `2-3` = Soll-Durchfluss, `32-bit float`
  - `11` = Steuermodus, `1` = digitale Steuerung, `2` = analoge Spannungsteuerung

Die aktuelle AMC2100-Logik in der Software ist:

- Die AMC2100-Gasflusssteuerung bleibt von der Temperatur-Verriegelung unabhaengig.
- Vor einem Hardware-Durchflussbefehl muss AMC2100 aktiviert sein; bei deaktivierter Steuerung wird der Befehl eindeutig blockiert.
- Wenn AMC2100 aktiviert ist, schreibt `Einstellungen speichern` den angezeigten Ziel-Durchfluss in das Geraet.
- Die Tasten `-` / `+` aendern den Sollwert um 10 mL/min und schreiben ihn sofort, ohne zusaetzliches Speichern.
- `Gasfluss stoppen` schreibt `0`; `Gas-Sollwert anwenden` schreibt den konfigurierten Zielwert.
- Periodisches Lesen des Ist-Durchflusses und manuelle Schreibbefehle verwenden einen serialisierten Befehlspfad und konkurrieren nicht um denselben COM-Port.
- Im Hauptfenster wird der Live-Wert `Gasfluss` aus den AMC2100-Istwert-Registern ueber RS485 angezeigt.

Dies ist kein separater Hard-Reset-Registerzugriff. Die UI meldet Erfolg erst, wenn die Modbus-Schreibantwort die Register `2-3` bestaetigt und ein sofortiges Ruecklesen dem angeforderten Sollwert entspricht. Das verifiziert das Sollwertregister, nicht den physischen Gasfluss; dafuer bleiben die Live-Anzeige `Gasfluss` und die Beobachtung vor Ort erforderlich.

Beim Banktest kann das lokale AMC2100-Display weiterhin `0` zeigen oder den Sollwert nicht sofort aktualisieren. Schreib-Rueckmeldung und Live-`Gasfluss` gemeinsam pruefen; aus einem erfolgreichen Befehl allein darf kein physischer Durchfluss abgeleitet werden.

Fuer einen praktischen softwaregesteuerten Validierungsablauf direkt auf der Engineering-Seite diese Checkliste verwenden:

- [docs/Engineering-Checklist.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.md)
- [docs/Engineering-Checklist.zh-CN.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.zh-CN.md)
- [docs/Engineering-Checklist.de.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.de.md)

## Funktionsweise

1. Das Programm sucht ein sichtbares HikmicroAnalyzer-Fenster über den Text im Feld `Window`.
2. Der Benutzer wählt mit `Select ROI` den Bereich des maximalen Temperaturtexts.
3. Das Programm erstellt periodisch einen Screenshot dieses Bereichs.
4. Tesseract OCR wandelt den Screenshot in Text um.
5. Der Parser erkennt Werte wie `Max 89.9 C`, `90.1°C` oder `最高 90.0 C`.
6. Jede Messung wird in CSV geschrieben.
7. Bei einem gueltigen Messwert `>= ThresholdC` wechselt die Software in `Tripped`.
8. In `Tripped` oeffnet die Software einmal alle aktivierten Interlock-Kanaele und der Zustand bleibt verriegelt.
9. Manueller Reset ist erst moeglich, wenn ein gueltiger Temperaturwert unterhalb des Schwellwerts gelesen wurde.
10. Wenn `Auto reset` aktiviert ist, schliesst die Software die Relaisbank automatisch wieder, nachdem die Temperatur fuer die eingestellte stabile Zeit unter `Recovery C` geblieben ist.

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
2. Tesseract OCR für Windows installieren. Für diesen Laborprototyp ist ein Community-Windows-Installer wie der Build der UB Mannheim in Ordnung. Installieren Sie ihn in einen normalen Windows-Ordner wie `C:\Program Files\Tesseract-OCR`. Installieren Sie ihn nicht direkt in den `artifacts\...`-Release-Ordner.
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

Der veroeffentlichte Ordner kann gezippt und auf den Labor-PC kopiert werden. Er enthaelt `ReactorSoftInterlock.Wpf.exe` und die von der App benoetigten .NET Runtime-Dateien.

Fuer diesen Branch sind dies die bevorzugten Namen:

```text
artifacts\ReactorSoftInterlock-win-x64-dsd-offline\
artifacts\ReactorSoftInterlock-win-x64-dsd-offline.zip
```

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

1. `ReactorSoftInterlock-win-x64-dsd-offline.zip` entpacken.
2. `ReactorSoftInterlock.Wpf.exe` starten.
3. Falls Windows SmartScreen erscheint, zuerst bestätigen, dass die Datei aus diesem Projekt stammt.
4. `appsettings.json` neben der EXE belassen; die App aktualisiert diese Datei beim Speichern der Einstellungen.
5. Wenn die App meldet, dass Tesseract, ROI, HikmicroAnalyzer, COM-Port oder Relaisbefehle fehlen, diesen Punkt zuerst beheben und erst danach die Überwachung starten.

## Erste Software-Einrichtung

1. HikmicroAnalyzer starten.
2. Sicherstellen, dass der maximale Temperaturtext sichtbar ist.
3. HikmicroAnalyzer sichtbar lassen, am besten maximiert.
4. Diese Anwendung starten.
5. In `Window` einen stabilen Teil des Fenstertitels eintragen, zum Beispiel `Hikmicro`.
6. Bei `Tesseract` entweder den Standard `offline-deps\tesseract\tesseract.exe` belassen, wenn ein portabler Tesseract-Ordner mitgeliefert wird, oder den vollständigen Installationspfad eintragen, zum Beispiel `C:\Program Files\Tesseract-OCR\tesseract.exe`.
7. `Dry run relay` aktiviert lassen.
8. `Save Settings` klicken.
9. `Select ROI` klicken.
10. Nur den maximalen Temperaturtext markieren.
11. `Start` klicken.
12. `Temperature`, `Raw OCR text`, Diagramm und Historientabelle pruefen.
13. Die Seite `Engineering` nur fuer Relais- und Interlock-Validierung verwenden.

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
4. `COM Port`, `Baud`, `Stop-Befehl` und `Reset-Befehl` eintragen.
5. Relaiskontakt weiter vom G2000 getrennt lassen.
6. `Dry run relay` deaktivieren.
7. `Test Relay Stop` klicken.
8. Mit dem Multimeter `COM-NO` und `COM-NC` prüfen.
9. Entscheiden, ob der Alarm den Kreis öffnen oder schließen muss.
10. Relaiskontakt in den bestätigten G2000-Verriegelungs- oder Not-Aus-Kreis einbinden.
11. G2000-Reaktion ohne Plasma oder in einem risikoarmen Zustand prüfen.
12. Erst danach für echte Übertemperatur-Trips verwenden.

Empfohlene Engineering-Zuordnung fuer diesen Branch:

- CH1 -> Interlock A negativ -> `I1-I2`
- CH2 -> Interlock A positiv -> `I5-I6`
- CH3 -> Interlock B negativ -> `I3-I4`
- CH4 -> Interlock B positiv -> `I7-I8`
- Pro Kanal bevorzugt `COMx` + `NOx`, damit der Zweig bei Kanal EIN geschlossen und bei Kanal AUS geoeffnet ist.
- Der Monitoring-Modus in diesem Branch ist dafuer ausgelegt, bei Trip alle aktivierten Kanaele zu oeffnen und beim Restore wieder zu schliessen.
- Immer mit dem Multimeter pruefen. Nicht nur auf Relais-LEDs vertrauen.

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
- Wenn `Test Relay Stop` nichts bewirkt, zuerst die Baudrate prüfen, danach die Relaisbefehle.

Relaisverhalten in diesem Branch:

- Im Hauptfenster muessen normale Laboranwender keine AT-Befehle direkt bearbeiten.
- Die Seite `Engineering` bietet gruppierte Aktionen:
  - `Alle Interlocks oeffnen`
  - `Alle Interlocks schliessen`
  - einzelne `Oeffnen` / `Schliessen` Buttons fuer CH1-CH4
- `Erweiterte Einstellungen` behalten den vollen Engineering-Zugriff:
  - Baudrate
  - Oeffnen- / Schliessen-Befehle fuer CH1-CH4
  - Kanal-Aktivierung
  - DSD-Standardwerte wiederherstellen
- Diese Befehle gehoeren zum externen USB-Relaisboard, nicht zum G2000 selbst.
- Im aktuellen `relay-interlock-test`-Branch wurde das Relais `DSD TECH SH-UR04A` getestet.
- Standard-Serieneinstellungen:
  - `COM Port = COM3` waehrend der Validierung
  - `Baud = 9600`
  - `Data bits = 8`
  - `Parity = None`
  - `Stop bits = 1`
- Standardbefehle pro Kanal:

```text
CH1 oeffnen    = AT+CH1=0
CH1 schliessen = AT+CH1=1
CH2 oeffnen    = AT+CH2=0
CH2 schliessen = AT+CH2=1
CH3 oeffnen    = AT+CH3=0
CH3 schliessen = AT+CH3=1
CH4 oeffnen    = AT+CH4=0
CH4 schliessen = AT+CH4=1
```

Aktuelle UI-Struktur in diesem Branch:

- Seite `Monitor`:
  - Alltagseinstellungen
  - OCR-Ueberwachung
  - Diagramm und Verlauf
  - gruppierte Relaisaktionen
- Seite `Engineering`:
  - explizite Interlock-Test-Checkliste
  - Einzelkanael-Buttons fuer CH1-CH4
  - gruppierte Oeffnen- / Schliessen-Aktionen
- `Tools -> Erweiterte Einstellungen`:
  - vollstaendige Relaisbefehl-Bearbeitung nur fuer Engineering

- Die Konfigurationsschlüssel in `appsettings.json` heißen aus Kompatibilitätsgründen weiterhin `StopCommandHex` und `ResetCommandHex`, speichern in diesem Branch aber ASCII-AT-Befehle statt HEX-Bytes.

Wenn noch kein externes Relaisboard ausgewählt wurde, `Dry run relay` aktiviert lassen und den App-Ausgang nicht mit dem G2000 verbinden.

G2000-Computersteuerung:

- Das G2000-Handbuch nennt industrielle Schnittstellen wie CAN und RS485. Diese sind G2000-native Schnittstellen und getrennt vom externen USB-Relaispfad.
- Dieser Prototyp implementiert noch keine G2000-CAN/RS485-Telegramme. Für direkte PC-Steuerung des G2000 müssen zuerst die Protokollseiten aus dem Handbuch ausgewertet werden: Steckerbelegung, Bustyp, Baudrate, Node-Adresse, Nachrichten-/Registermap, Enable-Befehl, Stop-Befehl, Statuswort und mögliche Watchdog- oder zyklische Telegrammpflichten.
- Bis dieses Protokoll implementiert und getestet ist, bleibt der empfohlene Stop-Pfad die externe Interlock-/Not-Aus-Schleife, weil sie mit dem Multimeter überprüfbar ist und nicht vom G2000-Softwaremodus abhängt.

Dry Run:

- Aktiviert: die App simuliert Relaisaktionen und öffnet keinen COM-Port.
- Deaktiviert: die App öffnet den COM-Port und sendet die konfigurierten Relaisbefehle.

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
- Die automatische HIKMICRO-Bildkalibrierung kann im Overlay kurz exakt `0 C` anzeigen. Exakt null wird als `NO READING` behandelt, setzt die Stabilzeit fuer die automatische Wiederfreigabe zurueck und kann einen ausgeloesten Interlock nicht selbst freigeben.
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
- Prüfen, ob `Stop-Befehl` nicht leer ist und zum Relaisprotokoll passt, zum Beispiel `AT+CH1=0` für das getestete DSD-Relais.

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
- Prüfen, ob `Stop-Befehl` und `Reset-Befehl` für dieses Relaisboard vertauscht werden müssen.
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
- `Ocr.TesseractExePath`: `offline-deps\tesseract\tesseract.exe`, `tesseract.exe` oder voller Pfad. Die App prüft zusätzlich übliche Windows-Installationsorte wie `C:\Program Files\Tesseract-OCR\tesseract.exe`.
- `Ocr.Language`: Standard `eng`.
- `Relay.DryRun`: Standard `true`.
- `Relay.PortName`: Standard `COM3`.
- `Relay.BaudRate`: Standard `9600`.
- `Relay.StopCommandHex`: speichert in diesem Branch den Stop-Befehl als Text. Standard: `AT+CH1=0`.
- `Relay.ResetCommandHex`: speichert in diesem Branch den Reset-Befehl als Text. Standard: `AT+CH1=1`.
- `DataDirectory`: Standard `data`.
- `Language`: `en`, `zh-CN` oder `de`.
- `AutoResetEnabled`: Standard `true`.
- `RecoveryThresholdC`: Standard `85.0`.
- `RecoveryStableSeconds`: Standard `30`.

## Entwicklertest

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

Erwartetes Ergebnis: alle Tests laufen erfolgreich durch.
