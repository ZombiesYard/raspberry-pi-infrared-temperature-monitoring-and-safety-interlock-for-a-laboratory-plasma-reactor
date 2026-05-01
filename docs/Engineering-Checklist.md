# Engineering Checklist

Language: [English](Engineering-Checklist.md) | [中文](Engineering-Checklist.zh-CN.md) | [Deutsch](Engineering-Checklist.de.md)

This checklist is for the `Engineering` page in the `relay-interlock-test` branch.

Use it when the DSD TECH relay already works and you want to validate the real G2000 interlock path with software control instead of repeatedly removing jumpers by hand.

## Purpose

- Confirm that software-controlled relay opening really disables G2000 HV / plasma.
- If AMC2100 is enabled, confirm that trip also writes the gas setpoint to `0`.
- If AMC2100 is enabled, use the top `Gas Flow` card in the app as the primary live indicator during RS485 tests.
- Confirm what happens after software-controlled relay closing.
- Confirm whether one branch is enough, one interlock group is enough, or all four branches are required.

## Before You Start

- Multimeter available.
- Screwdriver available.
- G2000 in a low-risk setup.
- Another person nearby if possible.
- `Dry run relay` is OFF.
- Correct COM port is selected.
- Relay channels and G2000 wiring are written down.
- HikmicroAnalyzer and OCR are not required for the static interlock tests.

## Record Current Wiring

Fill this in before testing:

```text
CH1 -> ______
CH2 -> ______
CH3 -> ______
CH4 -> ______
```

Recommended mapping in this branch:

```text
CH1 -> I1-I2
CH2 -> I5-I6
CH3 -> I3-I4
CH4 -> I7-I8
```

## Stage 1 - Static Software Interlock Tests

Do not run plasma yet. Use the `Engineering` page only.

### 1. Baseline

- Click `Connect All Interlocks`.
- Power on G2000.
- Confirm no interlock fault is shown.
- Confirm manual start is allowed.

Record:

```text
Baseline normal: yes / no
```

### 2. Single-Channel Tests

Keep all other channels connected. Open only one channel at a time.

- `Disconnect CH1`
- `Disconnect CH2`
- `Disconnect CH3`
- `Disconnect CH4`

For each test, record:

```text
CH1 only open: fault __ / HV allowed __
CH2 only open: fault __ / HV allowed __
CH3 only open: fault __ / HV allowed __
CH4 only open: fault __ / HV allowed __
```

### 3. Group Tests

Open the channels that belong to one interlock group together.

- Interlock A open: open CH1 and CH2 together
- Interlock B open: open CH3 and CH4 together

Record:

```text
Interlock A open: fault __ / HV allowed __
Interlock B open: fault __ / HV allowed __
```

### 4. Full Open Test

- Click `Disconnect All Interlocks`

Record:

```text
All open: fault __ / HV allowed __
```

## Stage 2 - Decide The Required Control Scope

Use the static results above:

- If opening one branch already disables HV, one branch may be enough.
- If one full group is required, control that group.
- If A and B both matter, keep the four-channel design.

For this branch, the monitoring logic is intentionally conservative:

```text
Trip = open all enabled channels
Restore = close all enabled channels
```

## Stage 3 - Dynamic Hot-Open Test

Only do this after the static tests are understood and the lab agrees.

- Click `Connect All Interlocks`.
- Bring G2000 into the lowest acceptable running condition.
- Trigger the action you plan to use in real monitoring:
  - preferred final test: `Disconnect All Interlocks`
  - optional earlier test: one channel or one group only
  - if AMC2100 is enabled, also confirm that the gas setpoint is written to `0`

Observe immediately:

- Does HV stop?
- Does plasma stop?
- What error text appears?

Record:

```text
Dynamic action: ______
HV stop: yes / no
Plasma stop: yes / no
Error text: ______
```

## Stage 4 - Recovery Test

After the hot-open test:

- Click `Connect All Interlocks`
- Observe recovery behavior

Record:

```text
Fault clears automatically: yes / no
Returns to ready: yes / no
HV resumes automatically: yes / no
Manual ON required: yes / no
Manual reset required: yes / no
Power-cycle required: yes / no
```

## Final Summary Template

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
AMC2100 gas restored: __
```
