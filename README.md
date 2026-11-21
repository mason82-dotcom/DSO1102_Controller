# DSO1102 Controller

Windows x64 oscilloscope software for the **Voltcraft DSO-1102-USB** (order no. 122470,
USB `VID_04B5&PID_1102`). It drives the instrument through the manufacturer's own driver and
DLL, never through guessed USB commands, and adds a modern scope UI with software trigger,
measurements, spectrum, cursors and export.

```text
WPF app (x64)  ──stdin/stdout JSON──▶  bridge (x86)  ──▶  DSO1102USB.dll  ──▶  DSO1102AMD642.SYS  ──▶  DSO-1102
```

The vendor DLL is 32-bit only (no 64-bit SDK exists, also not in Hantek's 2023 SDKs), so a
small x86 helper process hosts it. The kernel driver is already x64. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Quick start

1. Install the original Voltcraft software and driver for the DSO-1102-USB
   (`DSO1102USB.dll` under `C:\Program Files (x86)\DSO-1102 USB\`). Do not replace the driver
   with WinUSB/Zadig.
2. Download the `DSO1102_Controller-win-x64` artifact from the latest successful
   [Build and test](../../actions) run and start `DSO1102_Controller.exe`.
   The x86 bridge is included under `bridge\`.
3. Click **DSO-1102 verbinden**, or **Simulator verbinden** to try the UI without hardware.
4. Press **F5** for continuous acquisition. Put the CAL output on CH1, choose 200 µs/div and
   click **Pegel auf Signalmitte setzen**: a stable 1 kHz square wave should appear.

Only the vendor DLL with the documented SHA-256 is loaded; any other file is refused before
`LoadLibrary` (`tools\Run-HardwareProbe.ps1 -Command sdk-info` checks a file without loading it).

## What you get

| Area | Behaviour |
|---|---|
| Acquisition | Full record per frame: 524,288 samples/channel at the CAL-referenced ~5 MS/s, from the runtime-verified 1 ms/div profile |
| Time base | 5 µs/div … 10 ms/div (1-2-5) as a window of that record; the instrument profile does not change |
| Trigger | Software trigger on the record: CH1/CH2, rising/falling, level with hysteresis, position 0–100 %, Auto/Normal; *Single* in Normal waits for an edge. The instrument itself is force-triggered |
| Vertical | Per-channel scale and centre line. Without analog control amplitudes are **uncalibrated ADC codes** (nominal 32 codes/div around 128) |
| Analog front-end | **Opt-in, experimental:** V/div 500 mV / 1 V / 2 V, DC/AC, software GND, probe 1×/10×; shown as **nominal volts**. Only vendor codes seen in runtime traces are sent |
| Measurements | Frequency, period, duty cycle (hysteresis + ADC noise floor), peak-to-peak, min, max, mean, RMS, AC-RMS of the visible window |
| Spectrum | FFT up to 2¹⁹ points, Hann/Hamming/Blackman/rectangular, span tied to the time base |
| Cursors | Two time cursors with Δt, 1/Δt and channel values |
| Export | Visible window or full record as CSV (invariant culture, metadata header); scope area as PNG |
| Settings | Stored per sample domain in `%APPDATA%\DSO1102_Controller\settings.json` |

### Keyboard

| Key | Action |
|---|---|
| F5 / F6 / Esc | Continuous / single acquisition / stop (Esc also removes cursors when idle) |
| Ctrl+S | Save visible window as CSV |
| Ctrl+1 / Ctrl+2 | Place cursor A / B at the trigger point |
| Ctrl+← / → | Move cursor B by 0.1 div (with Shift: cursor A) |

Mouse: left click sets cursor A, right click sets cursor B. All controls have access keys
(Alt + underlined letter), visible focus rings and screen-reader names.

## Safety model

- No persistent writes: calibration, flash, device-ID and device-address exports are never called.
- The normal frame command only re-applies the verified time-base profile; the analog front-end
  stays as the device has it unless analog control is switched on.
- Analog control parametrises the reconstructed transient sequence of `self-init-1ms-analog`
  and accepts only traced range codes 5–7 and coupling 0/1 (bridge and client use the same
  parser). It is **not yet accepted on the device**; follow the acceptance steps in
  [docs/AUDIT_2026-11-02.md](docs/AUDIT_2026-11-02.md) before relying on the voltages.
- Bridge commands time out after 15 s; a failed or cancelled command restarts the helper process.

## Build and test

Requires the .NET 8 SDK (pinned feature band in `global.json`).

```powershell
dotnet run --project .\tests\DSO1102.Controller.Tests -c Release       # 170+ checks, no hardware
.\tools\Run-Controller.ps1                                            # build bridge + start app against the installed DLL
dotnet publish .\src\DSO1102.Controller.App -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Tests and a compile of all Windows projects also run on Linux
(`dotnet build src/DSO1102.Controller.App -p:EnableWindowsTargeting=true`). CI runs a Linux test
job and a Windows build/publish job in parallel. Analyzers run at `8.0-recommended` with
warnings as errors.

Development builds can point at a specific bridge/DLL:

```powershell
$env:DSO1102_BRIDGE_EXE = "D:\path\to\DSO1102_Bridge_x86.exe"
$env:DSO1102_SDK_DLL    = "C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll"
```

## Documentation

| Document | Content |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, bridge protocol, data flow, design decisions |
| [docs/AUDIT_2026-11-02.md](docs/AUDIT_2026-11-02.md) | Manufacturer/package audit, open work, hardware acceptance steps |
| [docs/HARDWARE_BRINGUP.md](docs/HARDWARE_BRINGUP.md) | Driver binding and diagnostics on the target PC |
| [docs/DLL_COMPLETE_ANALYSIS.md](docs/DLL_COMPLETE_ANALYSIS.md) | Static analysis of every `DSO1102USB.dll` export |
| [docs/SAMPLERATE_TABLE.md](docs/SAMPLERATE_TABLE.md) | All 38 Time/DIV codes and their sample-rate programming |
| [docs/REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) | Chronological runtime evidence (traces, captures) |
| [docs/OPENHANTEK_REFERENCE.md](docs/OPENHANTEK_REFERENCE.md) | Hantek family correlations (evidence only, no GPL code copied) |

No vendor binaries are distributed in this repository.
