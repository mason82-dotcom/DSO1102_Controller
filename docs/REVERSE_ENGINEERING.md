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


## Real hardware verification

The x86 bridge has been verified against a connected DSO-1102 on Windows:

- `dsoSearchDevice(0)` returned present.
- indices 1..3 returned not present.
- `dsoGetFPGAVersion(0)` returned raw value `12001`.
- the full path x86 bridge -> `DSO1102USB.dll` -> `DSO1102AMD642.SYS` -> hardware is therefore operational.

## Read-only information calls

Further disassembly confirms these read paths:

- `dsoGetDeviceID(deviceIndex, ushort* value)`: two stack arguments, writes one 16-bit value.
- `dsoGetDeviceAddress(deviceIndex, ushort* value)`: two stack arguments, writes one 16-bit value.
- `dsoGetChannelLevel(deviceIndex, ushort* values, ushort count)`: three stack arguments. The vendor application calls it with count `0x58` (88 words).
- `dsoGetCaptureState(deviceIndex, uint32* value)`: two stack arguments. The response status is returned separately and bytes 2..3 are combined into the output value.

The bridge exposes these through the `info` command without invoking configuration-changing functions.


## Calibration block interpretation

The vendor application's own code calls the channel-level API with a length of `0x58` (88). The DLL reads exactly that many bytes from the device and expands each byte to a 16-bit output element.

The vendor application pairs these 88 byte values as high-byte/low-byte pairs, producing 44 packed 16-bit calibration words. Therefore the bridge reports both representations.

For the verified DSO-1102 unit, the first read produced 44 packed words beginning with:

```text
75, 168, 75, 166, 76, 166, 77, 166, ...
```

and ending with:

```text
92, 195, 92, 195, 10578, 10322, 15, 70
```

These are calibration/configuration values, not waveform samples.

## Capture-state interpretation

The capture-state function returns a state byte plus a separate trigger-related value. A state code of zero is a defined protocol state and must not be treated as a generic API failure.

Known family state codes are tracked neutrally as:

- 0: VALUE0
- 1: VALUE1
- 2: SUCCESS
- 7: VALUE7
- 127: TIMEOUT

The exact semantic meaning of VALUE0, VALUE1 and VALUE7 for the DSO-1102 remains to be verified empirically.


## Verified DSO-1102 capture-ready state

After initializing a simple CH1/DC/1x/1 V-div/1 ms-div configuration with the original application and closing it, the bridge sequence

```text
dsoCaptureStart
sleep 3 ms
dsoTriggerEnabled
sleep 3 ms
dsoForceTrigger
sleep 3 ms
dsoGetCaptureState
```

reliably produced:

- state code: `3`
- stable raw trigger value: `4968`

Therefore state code `3` is treated as the DSO-1102 capture-ready/readable state.

For the small-memory path, disassembly of `dsoGetChannelData` shows an output size of `0x2800` samples per decoded buffer (10,240 samples). The large-memory path expands to 524,288 samples.

The experimental `capture-gnd` bridge command reads the already configured capture without invoking any vendor `Set*`, calibration, flash, or device-ID write function. It emits neutral buffer A/B statistics so CH1 can be identified empirically by tying CH1 to GND.
