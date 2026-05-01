# Engineering-Checkliste

Sprache: [English](Engineering-Checklist.md) | [中文](Engineering-Checklist.zh-CN.md) | [Deutsch](Engineering-Checklist.de.md)

Diese Checkliste gehoert zur Seite `Engineering` im Branch `relay-interlock-test`.

Sie ist fuer den Fall gedacht, dass das Relais bereits per Software geschaltet werden kann und die reale G2000-Interlock-Funktion jetzt ueber die Software geprueft wird, statt Jumper immer wieder von Hand zu entfernen.

## Ziel

- Bestaetigen, dass das softwaregesteuerte Oeffnen des Relais den G2000 wirklich sperrt.
- Bestaetigen, was nach dem softwaregesteuerten Schliessen des Relais passiert.
- Bestaetigen, ob ein Zweig, eine Gruppe oder alle vier Zweige benoetigt werden.

## Voraussetzungen

- Multimeter vorhanden.
- Schraubendreher vorhanden.
- G2000 in risikoarmem Aufbau.
- Moeglichst eine zweite Person daneben.
- `Dry run relay` ist AUS.
- Richtiger COM-Port ist ausgewaehlt.
- Verdrahtung zwischen Relaiskanaelen und G2000 ist notiert.
- Fuer die statischen Interlock-Tests ist OCR nicht zwingend erforderlich.

## Aktuelle Verdrahtung notieren

Vor dem Test ausfuellen:

```text
CH1 -> ______
CH2 -> ______
CH3 -> ______
CH4 -> ______
```

Empfohlene Zuordnung in diesem Branch:

```text
CH1 -> I1-I2
CH2 -> I5-I6
CH3 -> I3-I4
CH4 -> I7-I8
```

## Phase 1 - Statische Software-Interlock-Tests

In dieser Phase noch kein Plasma fahren. Nur die Seite `Engineering` verwenden.

### 1. Basiszustand

- `Connect All Interlocks` klicken
- G2000 einschalten
- Pruefen, dass kein Interlock-Fehler angezeigt wird
- Pruefen, dass manueller Start erlaubt ist

Notieren:

```text
Basiszustand normal: ja / nein
```

### 2. Einzelkanal-Tests

Alle anderen Kanaele geschlossen lassen. Pro Test nur einen Kanal oeffnen:

- `Disconnect CH1`
- `Disconnect CH2`
- `Disconnect CH3`
- `Disconnect CH4`

Pro Test notieren:

```text
CH1 only open: fault __ / HV allowed __
CH2 only open: fault __ / HV allowed __
CH3 only open: fault __ / HV allowed __
CH4 only open: fault __ / HV allowed __
```

### 3. Gruppentests

Die Kanaele einer Interlock-Gruppe gemeinsam oeffnen:

- Interlock A open: CH1 und CH2 gemeinsam oeffnen
- Interlock B open: CH3 und CH4 gemeinsam oeffnen

Notieren:

```text
Interlock A open: fault __ / HV allowed __
Interlock B open: fault __ / HV allowed __
```

### 4. Alles-offen-Test

- `Disconnect All Interlocks` klicken

Notieren:

```text
All open: fault __ / HV allowed __
```

## Phase 2 - Erforderlichen Steuerumfang festlegen

Die statischen Ergebnisse so auswerten:

- Wenn bereits ein einzelner Zweig HV sperrt, kann ein einzelner Zweig genuegen.
- Wenn eine ganze Gruppe erforderlich ist, sollte mindestens diese Gruppe gesteuert werden.
- Wenn A und B beide relevant sind, die Vierkanal-Loesung beibehalten.

Die Monitoring-Logik in diesem Branch ist absichtlich konservativ:

```text
Trip = open all enabled channels
Restore = close all enabled channels
```

## Phase 3 - Dynamischer Heiss-Test

Nur ausfuehren, wenn die statischen Tests verstanden sind und das Labor zustimmt.

- `Connect All Interlocks` klicken
- G2000 in den niedrigsten zulaessigen Betriebszustand bringen
- Die Aktion ausloesen, die spaeter real verwendet werden soll:
  - bevorzugter Endtest: `Disconnect All Interlocks`
  - optional vorher: Einzelkanal oder einzelne Gruppe

Sofort beobachten:

- Stoppt HV?
- Stoppt das Plasma?
- Welcher Fehlertext erscheint?

Notieren:

```text
Dynamic action: ______
HV stop: yes / no
Plasma stop: yes / no
Error text: ______
```

## Phase 4 - Wiederherstellungstest

Nach dem dynamischen Oeffnen:

- `Connect All Interlocks` klicken
- Wiederherstellungsverhalten beobachten

Notieren:

```text
Fault clears automatically: yes / no
Returns to ready: yes / no
HV resumes automatically: yes / no
Manual ON required: yes / no
Manual reset required: yes / no
Power-cycle required: yes / no
```

## Abschlussvorlage

```text
Current wiring:
CH1 -> __
CH2 -> __
CH3 -> __
CH4 -> __

Static tests:
CH1 only open: fault __ / HV allowed __
CH2 only open: fault __ / HV allowed __
CH3 only open: fault __ / HV allowed __
CH4 only open: fault __ / HV allowed __
Interlock A open: fault __ / HV allowed __
Interlock B open: fault __ / HV allowed __
All open: fault __ / HV allowed __

Dynamic test:
Action: __
HV stop: __
Plasma stop: __
Error: __

Recovery test:
Fault clears automatically: __
Ready: __
HV resumes automatically: __
Manual ON required: __
Manual reset required: __
Power-cycle required: __
```
