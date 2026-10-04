# DSO-1102 reverse-engineering notes

These notes document observations from the user's installed Voltcraft/Hantek DSO-1102 package. They are used to implement an interoperable controller without replacing the working vendor driver.

## Verified binaries

### User-mode DLL

- File: `DSO1102USB.dll`
- SHA-256: `93CA581F4DE649C54957CBD84B25A067ABAA89A078F765B4C7B6958F895C9CC1`
- PE architecture: x86 / I386
- File/Product version: 1.0.0.1
- Internal description: `DSO2250USB DLL`
- Original filename: `DSO2250USB.DLL`
- Unsigned

The old DSO2250 metadata appears to be retained from the code base used by the DSO-1102 package; the binary itself exports DSO-1102 functions and contains the string `D1102`.

### Vendor application

- File: `DSO-1102 USB.exe`
- SHA-256: `A70B2D36EDA3C6A97BF50506D556B66EFC5A34E4AC349C3C4099E8060AF2E19D`
- PE architecture: x86 / I386
- File/Product version: 1.0.0.1
- Product: DigitalScope Application
- Unsigned

The application dynamically loads `DSO1102USB.dll` with `LoadLibraryW` and resolves its API with `GetProcAddress`.

### Kernel driver

- File: `DSO1102AMD642.SYS`
- x64 / AMD64
- File/Product version: 1.0.0.1
- Product: Hantek DSO
- Service: `DSO11022`
- VID/PID: `04B5:1102`

## Device naming

The DLL contains:

- `D1102`
- `\\.\`
- format string `-%d`

The DLL's search routine constructs the Win32 device name from these strings. The resulting observed pattern is:

```text
\\.\D1102-%d
```

where the original application probes indices 0 through 3.

## Driver communication

The DLL imports:

- `CreateFileA`
- `DeviceIoControl`
- `CloseHandle`
- event/wait APIs

Observed IOCTLs include:

- `0x222051`
- `0x22204E`

The implementation should prefer the vendor DLL API instead of calling these IOCTLs directly.

## Exported API

Named exports include:

- `dsoSearchDevice`
- `dsoGetFPGAVersion`
- `dsoGetDeviceID`
- `dsoGetDeviceAddress`
- `dsoGetChannelLevel`
- `dsoGetCalData`
- `dsoSetCalData`
- `dsoSetChannelLevel`
- `dsoGetCalTrigState`
- `dsoSetOffset`
- `dsoGetChannelData`
- `dsoTriggerEnabled`
- `dsoCaptureStart`
- `dsoSetTriggerAndSampleRate`
- `dsoSetTriggerAndSampleRateNew`
- `dsoGetLogicData`
- `dsoForceTrigger`
- `dsoGetCaptureState`
- `dsoSetVoltageAndCouplingFirst`
- `dsoSetVoltageAndCouplingSecond`
- `dsoSetFilt`
- `dsoSetFiltAndVoltageData`
- `dsoSetVoltageAndCoupling`
- `dsoFFT`
- `dsoFFTGetSamples`
- `dsoFFTBuffer`
- `dsoSetDeviceID`
- `InitLevelRange`

Several lower-level exports use decorated stdcall names, for example:

- `_dsoSetSampleRate@8`
- `_dsoSetTrigIn@32`
- `_dsoSetTriggerLength@16`
- `_dsoSetRamLength@8`
- `_dsoSetChIn@8`
- `_dsoSetLogicData@8`
- `_dsoReadFlash@8`
- `_dsoWriteFlash@8`

## Verified calling convention observations

Disassembly shows stack-cleanup returns consistent with stdcall.

- `dsoSearchDevice`: one 32-bit stack slot; original application passes indices 0..3 and tests AX for zero/nonzero.
- `dsoGetFPGAVersion`: one 32-bit stack slot; original application passes the selected device index and stores EAX.
- `dsoGetChannelData`: eight 32-bit stack slots (`ret 0x20`).
- `dsoSetTriggerAndSampleRateNew`: eight slots (`ret 0x20`).
- `dsoSetVoltageAndCoupling`: six slots (`ret 0x18`).
- `dsoSetOffset`: six slots (`ret 0x18`).
- `dsoGetCaptureState`: two slots (`ret 0x08`).
- `dsoCaptureStart`, `dsoTriggerEnabled`, `dsoForceTrigger`: one slot (`ret 0x04`).

The x64 desktop app cannot load this x86 DLL in-process. The repository therefore uses a separate x86 bridge process for the vendor API.
