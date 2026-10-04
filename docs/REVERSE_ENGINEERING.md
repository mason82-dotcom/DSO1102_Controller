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


## Waveform-read validation result

A real-hardware `capture-gnd` run reached DSO-1102 state `3` and returned a stable trigger value, but both caller-provided waveform buffers remained entirely zero. The vendor function returned nonzero, so that return value alone cannot be used as proof that waveform data was decoded into the supplied buffers.

The bridge now treats an unchanged/all-zero pair of buffers as an invalid waveform read and does not infer channel mapping from it.

An older manufacturer LabVIEW example for the related DSO-2090 family used two preallocated output arrays containing 30,000 elements each for `dsoGetChannelData`. This is useful ABI evidence but does not prove the DSO-1102 sample count. Accordingly, the previous 10,240-sample assumption is no longer treated as verified for the DSO-1102 DLL.

Further hardware reads are gated on reconstructing the remaining auxiliary arguments of the eight-argument DSO-1102 `dsoGetChannelData` export.


## Runtime trace of dsoGetChannelData

A one-shot debugger trace of the original vendor application captured the real call at export RVA `0x3820` with return address `0x00453AC8`.

Observed entry arguments:

```text
arg1 = 0x00030000
arg2 = 0x06C89020
arg3 = 0x06E91020
arg4 = 0x0449ADCA
arg5 = 0x0449ADD6
arg6 = 0x00001860 (6240)
arg7 = 0x00030001
arg8 = 0x0449000C
```

Important observations:

- arg2 and arg3 point to large zero-initialized memory regions at function entry and are strong candidates for the two waveform output buffers.
- arg4 and arg5 are pointers separated by exactly 12 bytes and their memory previews overlap, proving that they point into the same configuration/calibration object.
- arg6 is a scalar value of 6240 and is consistent with a trigger/capture-position field.
- arg1 and arg7 are packed scalar-looking values (`0x00030000`, `0x00030001`), not device indices.
- arg8 is another pointer and remained zero-filled at entry.

The previously guessed bridge signature is therefore not valid. Direct waveform reads are disabled until a return-side trace confirms which pointer arguments are modified by the vendor DLL and what EAX returns.


## Corrected dsoGetChannelData ABI

A return-side trace plus the original application's call site at `0x00453AC2` resolved the eight arguments.

The apparent upper 16 bits seen in some runtime argument values are register residue: the vendor application loads only 16-bit words into the low half of the register before the 32-bit x86 stack push. Only the low 16 bits are semantically meaningful for arguments 1, 7 and 8.

For the traced call:

```text
arg1 low16 = 0     -> device index
arg2       = pointer to waveform buffer A
arg3       = pointer to waveform buffer B
arg4       = pointer to trigger/sample configuration at object +0x2A
arg5       = pointer to offset/calibration state at object +0x36
arg6       = trigger/capture value from object +0x15C
arg7 low16 = 1     -> calibration A from object +0x152
arg8 low16 = 12    -> calibration B from object +0x154
```

The original application populates object +0x152/+0x154 through:

```text
dsoGetCalData(deviceIndex, &calibrationA, &calibrationB)
```

and object +0x15C through:

```text
dsoGetCaptureState(deviceIndex, &triggerValue)
```

The two waveform buffers are conclusively verified: they were all-zero at function entry and contained decoded 16-bit samples at function return. The traced return value was `EAX=1`.

The trigger/sample structure passed as argument 4 begins with the five 16-bit words:

```text
0, 2, 12, 50, 0
```

for the agreed CH1-GND test profile. Argument 5 points exactly six words (12 bytes) later.

For this same profile, the original application takes the `0x2800` branch after the vendor call, confirming 10,240 processed samples per channel for this capture mode.

The bridge command `capture-gnd-v2` reproduces this traced ABI while still avoiding all persistent calibration, flash and device-ID writes.


## First successful bridge waveform read

A real-hardware `capture-gnd-v2` run succeeded with the runtime-verified ABI:

```text
stateCode          = 3 (CAPTURE_READY)
triggerValue       = 5474
vendorReadResult   = 1
samples/channel    = 10240
calibrationA/B     = 1 / 12
```

Both waveform buffers were populated by the vendor DLL.

Observed leading samples:

```text
buffer A: 0xFFFC, 190, 190, 190, ... mostly 189/190
buffer B: 0xFFF4, 62, 63, 62, ... mostly 62/63
```

The first value in each buffer is outside the normal 8-bit ADC range:

- `0xFFFC` = signed `-4`
- `0xFFF4` = signed `-12`

Those values must not be folded into normal ADC noise/Vpp statistics. The bridge now reports raw 16-bit values separately and computes ADC statistics only from values in `0..255`.

Because CH1 was physically tied to GND during this capture, buffer A is currently the leading CH1 candidate: it is markedly flatter than buffer B. Final CH1/CH2 mapping still requires one differential physical test with a known change on one channel.


## Stable CH1-GND baseline

A repeated `capture-gnd-v2` run with CH1 physically tied to oscilloscope GND produced a stable 10,240-sample capture:

```text
stateCode        = 3 (CAPTURE_READY)
triggerValue     = 3781
vendorReadResult = 1
calibrationA/B   = 1 / 12
```

ADC-range statistics after excluding the single first 16-bit boundary value:

```text
buffer A:
  valid samples  = 10239
  range          = 189..192
  mean           = 189.9870
  p-p            = 3 counts
  stddev         = 0.3910 counts

buffer B:
  valid samples  = 10239
  range          = 56..64
  mean           = 62.3339
  p-p            = 8 counts
  stddev         = 0.5172 counts
```

The first element again appeared outside the 8-bit ADC range:

```text
buffer A sample[0] = 0xFFFC (-4 signed)
buffer B sample[0] = 0xFFF4 (-12 signed)
```

Because this out-of-range value is reproducibly confined to the first element in repeated captures, it is treated as a boundary/special decoder value and excluded from ADC statistics.

Buffer A remains the leading CH1 candidate because CH1 was grounded and buffer A is the flatter of the two baselines. A known driven DC level on CH1 is still required for definitive channel mapping and voltage scaling.


## Internal test-signal capture and channel mapping

With CH1 connected to the DSO-1102 internal probe-compensation/test output through a 10:1 probe, the runtime-verified capture produced a clear two-level waveform in buffer A:

```text
CH1 / buffer A:
  low plateau mean   = 131.0333 counts
  high plateau mean  = 136.8468 counts
  separation         = 5.8135 counts
  high duty cycle    = 49.917 %
  low cluster count  = 5128
  high cluster count = 5111
```

Buffer B remained essentially flat around 62.23 counts. Its apparent second cluster contained only 10 of 10,239 valid samples (0.098 %) and is therefore an outlier/glitch cluster, not a waveform plateau.

This definitively maps:

```text
buffer A = CH1
buffer B = CH2
```

Using the nominal 2 Vpp probe-compensation signal and a 10:1 probe, the scope BNC sees approximately 0.2 Vpp. Against the measured 5.8135-count plateau separation, the first-order scale estimate is:

```text
0.2 Vpp / 5.8135 counts = 0.03440 V/count at the BNC
                         = 34.4 mV/count

Referred to the 10:1 probe tip:
                         = 0.344 V/count
```

This is only a first-order calibration estimate because the test output amplitude is nominal and the exact vertical-range calibration path has not yet been reconstructed.
