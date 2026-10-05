# DSO1102 Controller

Windows-x64 controller and signal-analysis application for the Voltcraft DSO-1102-USB two-channel oscilloscope.

## Verified hardware identity

- Device name: DSO1102 USB DRIVER 2
- USB VID: 04B5
- USB PID: 1102
- PnP identity observed: USB\VID_04B5&PID_1102
- Reported provider/manufacturer field: ODM

The project does not send guessed USB commands. Real acquisition/control will only be enabled after the installed driver/API and protocol are verified.

## Current baseline

- .NET 8 / WPF x64 application
- CH1 + CH2 acquisition model
- live oscilloscope display
- Vmin, Vmax, Vpp, mean, RMS, frequency and period
- FFT engine
- simulator backend for immediate development/testing
- hardware abstraction for the real DSO-1102-USB
- Windows USB/PnP diagnostic helper
- Windows CI build and self-contained win-x64 artifact

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
- raw frames are decimated to the requested application frame length;
- amplitude is explicitly labelled `ADC`, not volts;
- Single and Run acquisition through one persistent x86 bridge process.

The first real backend keeps one x86 bridge server alive for the whole device session, so repeated captures do not relaunch the helper process. It deliberately does **not** claim to set or know the current
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


## Real hardware backend

The x64 controller now has an initial real DSO-1102 backend through the verified x86 vendor-DLL bridge.

Current production safety boundary:

```text
Time/DIV             1 ms/div
channels             CH1 + CH2
trigger source       CH1
waveform domain      decoded ADC counts
amplitude calibrated no
```

The controller deliberately labels real hardware amplitudes as `ADC` rather than volts until the remaining vendor-application post-correction and count-to-voltage calibration are fully verified.

The Windows CI artifact bundles the self-contained x86 bridge under:

```text
bridge\DSO1102_Bridge_x86.exe
```

next to the x64 controller. For custom layouts, set `DSO1102_BRIDGE_EXE` to the bridge executable path.

The real backend uses the existing persistent x86 bridge server over redirected stdin/stdout. The bridge process stays alive across captures, so live acquisition avoids repeated process startup overhead.
