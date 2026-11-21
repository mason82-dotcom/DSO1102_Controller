# DSO1102 Controller

Windows-x64 controller and signal-analysis application for the Voltcraft DSO-1102-USB two-channel oscilloscope.

## Verified hardware identity

- Device name: DSO1102 USB DRIVER 2
- USB VID: 04B5
- USB PID: 1102
- PnP identity observed: USB\VID_04B5&PID_1102
- Reported provider/manufacturer field: ODM

The project uses the original vendor DLL through an x86 bridge for the previously verified 1 ms/div capture profile. Other settings remain experimental. Native bridge calls require the exact DLL fingerprint documented in `docs/DLL_COMPLETE_ANALYSIS.md`.

## Features

- .NET 8 / WPF x64 application with a separate x86 bridge process for the 32-bit vendor DLL
- CH1 + CH2 acquisition: full 524,288-sample record per channel at the CAL-referenced ~5 MS/s
- **Time base as a window of the record:** 5 µs/div … 10 ms/div (1-2-5). The instrument stays in
  the runtime-verified 1 ms/div profile; the decoded stream rate is the same for all verified
  hardware time bases (see `docs/REVERSE_ENGINEERING.md`, "Verified time-base summary").
- **Software trigger** on the captured record: source CH1/CH2, rising/falling, level with
  hysteresis, trigger position 0–100 %, modes Auto (record centre without edge) and Normal
  (frames without edge never replace the display). Single in Normal mode captures until an edge
  is found. The device itself is still force-triggered; the UI labels this explicitly.
- Per-channel display scale and centre line; nominal default 32 ADC/div around code 128
- Measurements of the visible window: frequency/period/duty cycle (mid-level crossings with
  hysteresis and an ADC noise floor), peak-to-peak, min, max, mean, RMS, AC-RMS
- Spectrum (FFT over up to 2^19 samples, selectable window, span tied to the time base)
- Two time cursors with Δt, 1/Δt and channel values
- Export of the visible window or the full record as CSV (invariant culture, metadata header)
  and of the scope area as PNG
- View settings are stored per sample domain in `%APPDATA%\DSO1102_Controller\settings.json`
- Simulator backend (1 kHz CAL-like square on CH1, 2.5 kHz sine on CH2, random phase, noise)
- Dark instrument UI (`src/DSO1102.Controller.App/Themes/Theme.xaml`): design tokens (L0 canvas /
  L1 surfaces / one blue accent, 1 px borders, no drop shadows), Segoe UI with three weights and
  12/14/16 px sizes, custom templates for buttons, 44 px icon buttons with tooltip + shortcut,
  segmented controls, switches, combo boxes, sliders and a slim scrollbar; measurement bar
  under the scope; status indicators for connection and acquisition state
- **Experimental analog front-end (opt-in, off by default):** V/div 500 mV / 1 V / 2 V, DC/AC
  coupling, software GND, probe 1×/10×. Uses the guarded transient analog sequence
  (`frame-1ms-analog <r1> <r2> <c1> <c2>`, only the runtime-traced range codes 5..7 and coupling
  0/1 are accepted by bridge and client) once per change, then the plain frame command. Values are
  shown as **nominal volts** (32 codes/div around code 128), never as calibrated volts.
- Regression tests without hardware (`tests/DSO1102.Controller.Tests`)

Amplitudes from the real device are **uncalibrated ADC codes**. V/div, coupling and offset are
not set by the controller and remain in the current device state (see "Experimental real-hardware
backend" below).

### Keyboard

| Key | Action |
|---|---|
| F5 | Start continuous acquisition |
| F6 | Single acquisition |
| Esc | Stop acquisition / remove cursors |
| Ctrl+S | Save visible window as CSV |
| Ctrl+1 / Ctrl+2 | Place cursor A / B at the trigger point |
| Ctrl+← / → | Move cursor B by 0.1 div (with Shift: cursor A) |

Mouse: left click sets cursor A, right click sets cursor B.

## Tests

~~~powershell
dotnet run --project .\tests\DSO1102.Controller.Tests -c Release
~~~

The runner checks DSP, trigger/window logic, CSV/settings handling, payload decoding and the
bridge process lifecycle with a fake bridge child process. It never touches the instrument and
also runs on Linux. The WPF projects can be compiled on Linux with
`-p:EnableWindowsTargeting=true`.

## Build

~~~powershell
dotnet restore .\src\DSO1102.Controller.App\DSO1102.Controller.App.csproj
dotnet build .\src\DSO1102.Controller.App\DSO1102.Controller.App.csproj -c Release
~~~

Run:

~~~powershell
dotnet run --project .\src\DSO1102.Controller.App\DSO1102.Controller.App.csproj -c Release
~~~

Publish self-contained x64:

~~~powershell
dotnet publish .\src\DSO1102.Controller.App\DSO1102.Controller.App.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
~~~

## Hardware diagnostics

Connect the oscilloscope and run:

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tools\Probe-UsbDevice.ps1
~~~

See docs/HARDWARE_BRINGUP.md.


## Reverse-engineering references

The hardware backend is based on the original vendor DLL and driver rather than guessed USB commands.

- `docs/DLL_COMPLETE_ANALYSIS.md` — complete static analysis of the original `DSO1102USB.dll`, including every export, transport path, command IDs, calibration layout, trigger/offset/filter behavior and safety classification.
- `docs/SAMPLERATE_TABLE.md` — all 38 Time/DIV codes and the corresponding low-level samplerate/timing programming reconstructed from the DLL.
- `docs/OPENHANTEK_REFERENCE.md` — DSO-2250/OpenHantek family correlations used as external semantic evidence only; no GPL implementation code is copied.
- `docs/REVERSE_ENGINEERING.md` — chronological runtime/static findings and hardware verification notes.

Persistent calibration, flash, device-ID and device-address write functions remain excluded from normal controller operation.


## Experimental real-hardware backend

The repository now contains a guarded first real-hardware path using the original
32-bit vendor DLL through the x86 bridge.

Current scope:

- Windows x64 WPF controller;
- packaged x86 bridge launched out-of-process;
- real device discovery through `dsoSearchDevice`;
- FPGA version reporting;
- runtime-verified `1 ms/div` self-initialized capture profile;
- CH1/CH2 raw frames returned as decoded ADC counts;
- sentinel values are sanitized before signal analysis;
- full records are retained for measurements/FFT; display-only min/max envelopes preserve spikes;
- amplitude is explicitly labelled `ADC`, not volts;
- Single and Run acquisition through one persistent x86 bridge process.

The first real backend keeps one x86 bridge server alive for the whole device session (recreated after cancellation or protocol failure), so repeated captures do not relaunch the helper process. It deliberately does **not** claim to set or know the current
analog V/div/coupling state. The normal `frame-1ms-adc` command self-initializes
the verified timebase path while preserving the existing analog front-end state.

A separate guarded test command exists for the reconstructed transient analog
setup:

```powershell
powershell -ExecutionPolicy Bypass `
  -File .\tools\Run-HardwareProbe.ps1 `
  -Command self-init-1ms-analog
```

That command targets 1 ms/div, 1 V/div, DC, filters off and centered
vertical/trigger positions. It uses only transient high-level vendor setters and
does not call calibration, flash, device-ID or device-address write exports.

The analog self-init sequence is intentionally not promoted into the normal GUI
backend until it has been verified on the physical DSO-1102.

### Packaged bridge

CI places the self-contained x86 bridge under:

```text
bridge\DSO1102_Bridge_x86.exe
```

inside the x64 controller artifact.

For development builds, the bridge can also be selected explicitly:

```powershell
$env:DSO1102_BRIDGE_EXE = "D:\path\to\DSO1102_Bridge_x86.exe"
$env:DSO1102_SDK_DLL = "C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll"
```

## Reliability and manufacturer review (2026-11-02)

See [the repository audit](docs/AUDIT_2026-11-02.md) for findings, source URLs,
current manufacturer-package evidence and remaining hardware acceptance work.

- Commands time out after 15 seconds; cancellation stops the bridge process.
- Connect, Run and Single are exclusive; Stop also cancels Single/Connect.
- ADC data is validated over the complete record; repaired sentinel counts are visible.
- FFT uses coherent-gain correction, includes Nyquist, and the GUI removes DC before windowing.
- Hardware returns the complete record at the bridge's CAL-referenced stream rate.
  `RecordLength` controls simulator length; it does not reprogram hardware memory.
- The plot auto-scales each channel independently. Its grid is not a calibrated V/div or time/div scale.
- The GUI Single button requests one forced capture; it is not a Normal/Single edge-trigger mode.

Inspect a DLL **without loading it or accessing hardware**:

```powershell
.\tools\Run-HardwareProbe.ps1 -Command sdk-info
```

Run deterministic regression checks (no device, driver or vendor DLL needed):

```powershell
dotnet run --project .\tests\DSO1102.Controller.Tests -c Release
```

The Windows build workflow runs these checks before publishing. An unknown DLL
is refused before `LoadLibraryExW`; matching export names alone do not prove ABI
compatibility. No vendor binaries are distributed in this repository.
