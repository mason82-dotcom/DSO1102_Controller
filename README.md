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
