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


## Square-wave timing analysis

The bridge now derives timing directly from the decoded ADC samples:

1. split the waveform into low/high plateaus,
2. place the transition threshold halfway between the plateau means,
3. require four stable samples before accepting a state transition,
4. collect rising and falling edge indices,
5. calculate same-polarity edge-to-edge periods in samples.

For captures of the DSO-1102 internal CAL/probe-compensation output, the software additionally reports a conditional sample-rate estimate under the documented nominal 1 kHz reference:

```text
estimatedSampleRateHz = meanPeriodSamples * 1000
sampleIntervalNs      = 1e9 / estimatedSampleRateHz
recordDurationMs      = sampleCount / estimatedSampleRateHz * 1000
```

The 1 kHz value is explicitly treated as a nominal external reference, not as a property inferred from the USB protocol.


## Time-base profile inferred from internal CAL signal

For the vendor-traced CH1 profile, the decoded buffer contains 10,240 samples and the internal CAL signal yields:

```text
rising-edge periods: 999..1000 samples, mean 999.7778
falling-edge periods: 999..1001 samples, mean 999.8000
combined mean period: 999.7895 samples
```

Using the nominal 1 kHz internal CAL signal as the external reference gives:

```text
estimated sample rate   = 999,789.47 samples/s
estimated sample period = 1.00021 us
estimated record length = 10.24116 ms
```

This is consistent with the discrete 1 MS/s mode expected for the currently initialized 1 ms/div profile. If the hardware sample clock is taken as exactly 1,000,000 samples/s, the observed CAL waveform frequency is:

```text
1,000,000 / 999.7895 = 1000.21 Hz
```

Therefore the current profile is treated as:

```text
time-base code         = 12
configured time base   = 1 ms/div
sample count           = 10,240
sample rate candidate  = 1 MS/s
record duration        = 10.24 ms
```

The sample-rate value remains marked as a profile-level inference until the original vendor time-base/sample-rate lookup table is statically recovered.


## Time-base setter tracing workflow

To reconstruct the complete DSO-1102 time-base/sample-rate table without guessing USB commands, use the original application as the source of truth and trace its vendor-DLL setter calls.

The helper:

```powershell
powershell -ExecutionPolicy Bypass \
  -File .\tools\Trace-Timebase.ps1 \
  -Mode new
```

captures the first real call to:

```text
dsoSetTriggerAndSampleRateNew   (8 arguments)
```

The legacy path can be inspected with:

```powershell
powershell -ExecutionPolicy Bypass \
  -File .\tools\Trace-Timebase.ps1 \
  -Mode legacy
```

which targets:

```text
dsoSetTriggerAndSampleRate      (3 arguments)
```

Recommended reconstruction procedure:

1. Start the vendor application through the multi-call tracer.
2. Switch the vendor UI through the real adjacent DSO-1102 time bases: 400 us/div -> 1 ms/div -> 2 ms/div -> 4 ms/div.
3. Let each change trigger its normal configuration call.
4. Record the captured setter arguments and pointed-to UInt16 structure words for each call.
5. Correlate the changing structure field with the measured sample period from `capture-raw`.

Do not use 500 us/div or 5 ms/div for this model; the observed UI provides 400 us/div and 4 ms/div instead.

For the already characterized profile:

```text
UI time base           = 1 ms/div
configuration word[2]  = 12
sample count           = 10,240
sample-rate candidate  = 1 MS/s
record duration        = 10.24 ms
CAL signal period      = ~999.79 samples
```

The goal is to recover the actual vendor lookup table rather than infer every mode from the nominal front-panel setting.


## Multi-call trace result for dsoSetTriggerAndSampleRateNew

A real eight-call trace of `dsoSetTriggerAndSampleRateNew` completed successfully, but all captured calls carried the same argument-3 configuration prefix:

```text
word[0] = 0
word[1] = 0
word[2] = 15
word[3] = 50
word[4] = 0
word[5] = 0
```

The calls came from two original-application return sites:

```text
0x004536B1
0x004539C0
```

Arguments 4..7 were zero and argument 8 was 2 in every captured call. The first eight calls were therefore dominated by initialization/refresh activity and did not expose the intended sequence of UI time-base changes.

This result also means that `word[2]` must not yet be labeled as the time-base code solely from the New-setter trace. The next step is to trace the three-argument legacy export `dsoSetTriggerAndSampleRate`, where static DLL analysis already showed direct construction of the sample-rate/trigger command.


## Legacy setter runtime result

A runtime trace of `dsoSetTriggerAndSampleRate` during normal vendor-application startup, capture activity, and time-base UI changes captured zero calls. The legacy export exists in the DLL but is not used by this application path.

Time-base reconstruction therefore remains focused on `dsoSetTriggerAndSampleRateNew`.

The call tracer now supports duplicate suppression based on a selected pointer argument. For time-base work, argument 3 is used as the state key: the first configuration is recorded, subsequent byte-identical argument-3 states are suppressed, and only actual configuration changes are retained. This prevents recurring acquisition/refresh calls from consuming the trace budget before UI changes occur.


## Verified time-base code mapping from filtered New-setter trace

A filtered runtime trace of `dsoSetTriggerAndSampleRateNew` observed 16 calls, suppressed 8 byte-identical argument-3 states, and retained 8 distinct states.

The final ordered UI sequence was:

```text
400 us/div -> 1 ms/div -> 2 ms/div -> 4 ms/div
```

For the four user-selected time bases, argument 3 had the following stable prefix:

```text
                 word[0] word[1] word[2] word[3] word[4] word[5]
400 us/div          0       0      15      50       5       0
1 ms/div            0       0      16      50       5       0
2 ms/div            0       0      17      50       5       0
4 ms/div            0       0      18      50       5       0
```

Therefore, for this normal acquisition mode:

```text
timeBaseCode 15 = 400 us/div
timeBaseCode 16 = 1 ms/div
timeBaseCode 17 = 2 ms/div
timeBaseCode 18 = 4 ms/div
```

Earlier calls with `word[4] = 6` are initialization/alternate internal states and must not be used as the normal UI mapping.

If the waveform record remains 10,240 samples and spans approximately 10.24 horizontal divisions, the expected sample-rate table is:

```text
400 us/div -> 2.5 MS/s   (4.096 ms record)
1 ms/div   -> 1.0 MS/s   (10.24 ms record)  [already measured]
2 ms/div   -> 500 kS/s   (20.48 ms record)
4 ms/div   -> 250 kS/s   (40.96 ms record)
```

Only the 1 ms/div -> ~1 MS/s point is currently measured from the internal ~1 kHz CAL waveform. The other three rates are predictions to be verified by capture timing before being promoted to hardware facts.


## Waveform-read profile tracing across time bases

Setter codes and waveform-read configuration must not be assumed to be identical. The filtered New-setter trace maps UI time bases to setter word[2] values 15..18, while an earlier real `dsoGetChannelData` trace showed a different read-state prefix for the previously characterized profile.

To recover the exact read-state structure used for each UI time base, trace `dsoGetChannelData` directly and suppress duplicate calls by argument 4:

```powershell
powershell -ExecutionPolicy Bypass \
  -File .\tools\Trace-CaptureProfiles.ps1 \
  -MaxCalls 8 \
  -IdleTimeoutMs 30000 \
  -TotalTimeoutMs 120000
```

Then switch the vendor UI in this order:

```text
400 us/div -> 1 ms/div -> 2 ms/div -> 4 ms/div
```

Argument 4 is the trigger/sample configuration pointer used by the actual waveform read. These captured states are the authoritative source for reconstructing independent bridge captures at each time base.


## Direct dsoGetChannelData profile trace

A filtered runtime trace of `dsoGetChannelData` observed 48 calls, suppressed 40 byte-identical argument-4 states, and retained 8 distinct read states.

The normal acquisition states used:

```text
word[0] = 0
word[1] = 0
word[2] = time-base code
word[3] = 50
word[4] = 6
word[5] = 0
word[6] = 127
word[7] = 192
word[8] = 124
word[9] = 192
word[10] = 128
...
word[13] = 256
...
word[22] = 16368
word[23...] = live channel-level calibration bytes
```

The read-path time-base codes matched the New-setter trace:

```text
15 = 400 us/div
16 = 1 ms/div
17 = 2 ms/div
18 = 4 ms/div
```

The trace then walked back through 18 -> 17 -> 16 -> 15, confirming the changing field is the UI time-base state rather than a one-off initialization value.

A transient first state used `word[4] = 5`; subsequent normal acquisition states consistently used `word[4] = 6`. The bridge therefore uses word[4] = 6 for the new profile-specific read commands.

New read-only diagnostic commands:

```text
capture-400us
capture-1ms
capture-2ms
capture-4ms
```

These commands do not call any configuration setter. The vendor application must first initialize the matching time base, then be closed. The bridge only performs capture/read using the runtime-traced decoding structure.


## 400 us/div measured sample rate

The first profile-specific `capture-400us` run exposed a stale/misaligned decoder state even though the vendor function returned success: CH1 contained only 1,825 byte-range ADC values and 8,415 values outside 0..255, while CH2 contained only 33 byte-range values. This is not a valid decoded waveform and is now rejected by the bridge.

A repeated `capture-400us` run produced a clean record:

```text
CH1 valid ADC samples = 10,239 / 10,240
CH2 valid ADC samples = 10,239 / 10,240
CH1 low plateau       = 127.0683 counts
CH1 high plateau      = 132.9030 counts
plateau separation    = 5.8347 counts
high duty fraction    = 0.4883
rising edges          = 2185, 7185
falling edges         = 4685, 9685
period                = exactly 5000 samples
```

Using the nominal 1 kHz internal CAL waveform:

```text
sample rate     = 5,000,000 samples/s
sample interval = 200 ns
record duration = 10,240 / 5,000,000 = 2.048 ms
```

Therefore the previously predicted 2.5 MS/s value for time-base code 15 was wrong. The measured profile is:

```text
timeBaseCode 15 = 400 us/div
sample rate     = 5 MS/s
sample count    = 10,240
record duration = 2.048 ms
```

The short record contains only about two periods of the 1 kHz reference signal, so the timing detector now accepts one matching period from each edge polarity when plateau occupancy is valid and the two period estimates agree.


## 1 ms/div capture exposes larger-record hypothesis

A profile-specific `capture-1ms` run with time-base code 16 was valid but the first 10,240 decoded samples still contained approximately 5,000 samples per 1 kHz CAL period:

```text
rising period  = 4999 samples
falling period = 5001 samples
combined mean  = 5000 samples
=> local decoded sample spacing ~= 200 ns
=> local decoded rate ~= 5 MS/s
```

This means the previous assumption that 10,240 samples are the complete acquisition record is not valid for all time-base modes.

The vendor DLL was already known to contain larger-memory paths. A plausible interpretation is that the 1 ms/div mode keeps a 5 MS/s decoded sample stream but returns a longer record, while earlier bridge diagnostics inspected only the first 0x2800 samples.

The bridge now scans the entire 524,288-sample guard allocation after each `dsoGetChannelData` call and reports:

```text
bufferWriteExtentA
bufferWriteExtentB
  lastNonZeroIndex
  populatedPrefixEstimate
  totalNonZeroValues
  nonZeroChunks
```

This is a diagnostic estimate based on the zero-initialized guard buffers. It is intended to recover the actual mode-dependent record length before changing the fixed 0x2800 analysis window.


## 1 ms/div writes the full 512K channel buffers

After adding whole-buffer write-extent diagnostics, a real `capture-1ms` run showed:

```text
guardBufferSamplesPerChannel = 524,288

buffer A:
  lastNonZeroIndex        = 524,287
  populatedPrefixEstimate = 524,288
  totalNonZeroValues      = 524,288

buffer B:
  lastNonZeroIndex        = 524,287
  populatedPrefixEstimate = 524,288
  totalNonZeroValues      = 524,288
```

Thus the earlier fixed 10,240-sample analysis window was only a small prefix of the vendor-decoded record for this mode.

At the locally measured ~5 MS/s sample spacing, 524,288 samples correspond to approximately 104.8576 ms of raw decoded acquisition per channel. This duration is a derived value, not yet a claim about the visible screen width; the vendor application may display only a viewport of the deeper acquisition memory.

The bridge now also analyzes the complete 524,288-sample buffers and reports full-buffer ADC validity, tail samples, plateau statistics, and square-wave timing. This will verify whether the decoded 1 kHz CAL waveform remains coherent across the entire deep-memory record.


## 1 ms/div full-buffer verification

A full-buffer `capture-1ms` run verifies the deep-memory interpretation.

Per channel:

```text
buffer length        = 524,288 samples
valid ADC8 samples   = 524,287
excluded sentinel    = 1
valid fraction       = 0.9999980926513672
```

CH1 remains a clean two-plateau square wave across the whole buffer:

```text
rising edges         = 105
falling edges        = 104
rising periods       = 104
falling periods      = 103
mean period          = 4999.299516908212 samples
period stddev        = 1.9798867284376596 samples
period CV            = 0.00039603282854757
edge agreement       = true
```

Using the nominal 1 kHz internal CAL source:

```text
estimated sample rate     = 4,999,299.5169 samples/s
estimated sample interval = 200.028023 ns
estimated record duration = 104.872092 ms
```

CH2 remains a quiet channel over the same full record and is rejected by the plateau/timing detector as a secondary outlier cluster rather than a periodic waveform.

Conclusion:

```text
timeBaseCode 16 = 1 ms/div
decoded sample stream ~= 5 MS/s
decoded record depth  = 524,288 samples/channel
decoded record span   ~= 104.87 ms/channel
```

The visible 1 ms/div screen is therefore a viewport into a much deeper decoded acquisition record; it is not equivalent to the full capture duration.


## 2 ms/div full-buffer verification

A full-buffer `capture-2ms` run verifies that time-base code 17 uses the same decoded ~5 MS/s deep-memory stream as the 1 ms/div profile.

Per channel:

```text
buffer length        = 524,288 samples
valid ADC8 samples   = 524,287
excluded sentinel    = 1
valid fraction       = 0.9999980926513672
```

CH1 remains a clean two-plateau square wave across the complete record:

```text
rising edges         = 105
falling edges        = 104
rising periods       = 104
falling periods      = 103
mean period          = 4998.391304347826 samples
period stddev        = 1.3429082792591764 samples
period CV            = 0.0002686680968917048
edge agreement       = true
```

Using the nominal 1 kHz internal CAL source:

```text
estimated sample rate     = 4,998,391.3043 samples/s
estimated sample interval = 200.064369 ns
estimated record duration = 104.891148 ms
```

Conclusion:

```text
timeBaseCode 17 = 2 ms/div
decoded sample stream ~= 5 MS/s
decoded record depth  = 524,288 samples/channel
decoded record span   ~= 104.89 ms/channel
```

Therefore, changing from 1 ms/div (code 16) to 2 ms/div (code 17) does not change the decoded sample clock or deep-memory record length. The time-base change is implemented at a later display/viewport stage in the vendor application for these two settings.


## 4 ms/div full-buffer verification

A full-buffer `capture-4ms` run verifies that time-base code 18 also uses the same decoded ~5 MS/s deep-memory stream.

Per channel:

```text
buffer length        = 524,288 samples
valid ADC8 samples   = 524,287
excluded sentinel    = 1
valid fraction       = 0.9999980926513672
```

CH1 remains a stable square wave across the whole record:

```text
rising edges         = 105
falling edges        = 104
mean period          = 4999.31884057971 samples
period stddev        = 1.1650226962586319 samples
period CV            = 0.00023303628622405257
edge agreement       = true
```

Using the nominal 1 kHz internal CAL source:

```text
estimated sample rate     = 4,999,318.8406 samples/s
estimated sample interval = 200.027250 ns
estimated record duration = 104.871687 ms
```

Conclusion:

```text
timeBaseCode 18 = 4 ms/div
decoded sample stream ~= 5 MS/s
decoded record depth  = 524,288 samples/channel
decoded record span   ~= 104.87 ms/channel
```

Together with the verified 1 ms/div and 2 ms/div profiles, codes 16, 17 and 18 all use the same ~5 MS/s 512 KiSample deep-memory acquisition. The UI time-base difference for these settings is therefore a display/viewport operation rather than an ADC sample-clock or record-depth change.


## Verified time-base summary

Current runtime-verified mapping:

| UI time base | Code | Decoded sample rate | Decoded depth/channel | Status |
| --- | ---: | ---: | ---: | --- |
| 400 us/div | 15 | ~5 MS/s | 524,288 samples | full-buffer verified |
| 1 ms/div | 16 | ~5 MS/s | 524,288 samples | full-buffer verified |
| 2 ms/div | 17 | ~5 MS/s | 524,288 samples | full-buffer verified |
| 4 ms/div | 18 | ~5 MS/s | 524,288 samples | full-buffer verified |

For codes 16..18, changing Time/div does not change the decoded ADC sample clock or decoded record depth. The visible horizontal scale is therefore implemented downstream as viewport/rendering behavior for those profiles.

All four profiles in this block are now full-buffer verified.


## 400 us/div full-buffer verification

A fresh full-buffer `capture-400us` run verifies that time-base code 15 also uses the same decoded ~5 MS/s 512 KiSample deep-memory stream.

Per channel:

```text
buffer length        = 524,288 samples
valid ADC8 samples   = 524,287
excluded sentinel    = 1
valid fraction       = 0.9999980926513672
```

CH1 timing over the complete record:

```text
rising edges         = 105
falling edges        = 104
mean period          = 4999.004830917875 samples
period stddev        = 1.5180001608430254 samples
period CV            = 0.00030366047087101994
edge agreement       = true
```

Using the nominal 1 kHz internal CAL source:

```text
estimated sample rate     = 4,999,004.8309 samples/s
estimated sample interval = 200.039815 ns
estimated record duration = 104.878274 ms
```

Conclusion:

```text
timeBaseCode 15 = 400 us/div
decoded sample stream ~= 5 MS/s
decoded record depth  = 524,288 samples/channel
decoded record span   ~= 104.88 ms/channel
```

Codes 15, 16, 17 and 18 are therefore all verified to use the same approximately 5 MS/s, 512 KiSample/channel decoded acquisition stream. In this range, Time/div changes are downstream viewport/rendering behavior rather than changes in decoded sample clock or decoded record depth.


## Guarded time-base self-initialization probe

After full-buffer verification of time-base codes 15..18, the bridge adds one deliberately narrow transient configuration test:

```text
self-init-400us
```

This command calls only the runtime-traced `dsoSetTriggerAndSampleRateNew` export. It does not call voltage/coupling, offset, filter, calibration, flash, device-ID or device-address setters.

The verified setter ABI used by the test is:

```text
arg1 = device index (0)
arg2 = 0
arg3 = pointer to vendor state
arg4 = 0
arg5 = 0
arg6 = 0
arg7 = 0
arg8 = 2
```

For 400 us/div, the runtime-traced state prefix is:

```text
0, 0, 15, 50, 5, 0,
127, 192, 124, 192, 128, 0, 0, 256,
0, 0, 0, 0, 0, 0, 0, 0, 16368
```

The live 88-byte channel-level calibration table is appended at word 23 exactly as observed in the vendor process.

The command sequence is:

```text
dsoCaptureStart
dsoSetTriggerAndSampleRateNew
dsoTriggerEnabled
dsoForceTrigger
poll dsoGetCaptureState until state 3
dsoGetChannelData
```

The JSON explicitly reports:

```text
selfInitializedTimeBase
timeBaseSetterResult
transientTimeBaseConfigurationChanged
configurationSettersCalled
configurationSetter
```

This is not yet a complete independent device initialization. Analog input range/coupling, offset and filtering remain whatever state is already active in the device. The purpose of this probe is only to prove that the traced time-base setter can be invoked safely and produce a readable acquisition without using the vendor UI for that specific time-base operation.


## Analog configuration tracing

The remaining transient analog setup must be recovered from the vendor application before the bridge writes those settings itself.

The helper:

```text
tools/Trace-AnalogConfig.ps1
```

supports the two exports whose six-slot stdcall ABI is already established statically:

```text
-Mode voltage -> dsoSetVoltageAndCoupling
-Mode offset  -> dsoSetOffset
```

Example:

```powershell
powershell -ExecutionPolicy Bypass \
  -File .\tools\Trace-AnalogConfig.ps1 \
  -Mode voltage \
  -MaxCalls 16 \
  -TotalTimeoutMs 120000
```

The tracer only observes calls made by the original application. It does not invoke these setters itself.


## Self-initialized 400 us/div verification

The first guarded self-initialization run succeeded without using the original vendor UI for the time-base operation:

```text
command                         = self-init-400us
requiresOriginalApplicationInitialization = false
selfInitializedTimeBase         = true
timeBaseSetterResult            = 1
capture state                   = 3 (CAPTURE_READY)
vendorReadResult                = 1
```

The full decoded record remained valid:

```text
buffer depth/channel       = 524,288 samples
valid ADC8 samples         = 524,287
rising edges               = 105
falling edges              = 104
mean CAL period            = 4998.574879227053 samples
estimated sample rate      = 4,998,574.8792 samples/s
estimated sample interval  = 200.057021 ns
estimated record duration  = 104.887295 ms
```

Safety reporting for the successful run:

```text
persistentConfigurationChanged       = false
transientTimeBaseConfigurationChanged = true
calibrationWritten                    = false
flashWritten                          = false
deviceIdWritten                       = false
configurationSettersCalled            = true
configurationSetter                   = dsoSetTriggerAndSampleRateNew
waveformRead                          = true
```

This verifies that the traced `dsoSetTriggerAndSampleRateNew` ABI and 400 us/div state can be driven directly by the x86 bridge.

Because runtime traces showed the same setter structure for codes 15..18 with only `word[2]` changing, the bridge now exposes:

```text
self-init-400us -> code 15
self-init-1ms   -> code 16
self-init-2ms   -> code 17
self-init-4ms   -> code 18
```

These commands still do not constitute complete cold device initialization: analog voltage range/coupling, offset and filter state are not yet set independently by the bridge.


## Voltage/div and coupling runtime mapping

A controlled runtime trace of `dsoSetVoltageAndCoupling` used this CH1 UI sequence while leaving CH2 unchanged:

```text
CH1 DC, 1 V/div
-> CH1 DC, 500 mV/div
-> CH1 DC, 2 V/div
-> CH1 AC, 2 V/div
-> CH1 DC, 2 V/div
-> CH1 DC, 1 V/div
```

Eight calls were observed. The first three were identical initialization/refresh calls:

```text
arg2 = 6
arg3 = 6
arg4 = 0
arg5 = 1
```

The subsequent five calls exactly tracked the requested UI transitions:

```text
500 mV/div, DC -> arg2=5, arg3=6, arg4=0, arg5=1
2 V/div,   DC -> arg2=7, arg3=6, arg4=0, arg5=1
2 V/div,   AC -> arg2=7, arg3=6, arg4=1, arg5=1
2 V/div,   DC -> arg2=7, arg3=6, arg4=0, arg5=1
1 V/div,   DC -> arg2=6, arg3=6, arg4=0, arg5=1
```

Therefore, for CH1:

```text
arg2 = CH1 vertical range code
  5 = 500 mV/div
  6 = 1 V/div
  7 = 2 V/div

arg4 = CH1 coupling
  0 = DC
  1 = AC
```

Because CH2 was deliberately not changed in this run:

```text
arg3 = CH2 vertical range state (observed constant 6)
arg5 = CH2 coupling state (observed constant 1)
```

The exact UI meaning of CH2's constant values must be confirmed by a dedicated CH2 trace rather than inferred.

Arguments 1 and 6 carried varying non-zero upper 16 bits while their low 16 bits remained zero. As with other vendor calls, those upper bits are treated as caller register/stack residue until a runtime trace proves otherwise.

All calls returned a low-word success value of 1 (`ReturnEax ...0001`).


## CH2 voltage/div and coupling confirmation

A second controlled `dsoSetVoltageAndCoupling` trace changed only CH2.

Important operator note: the first observed call was caused by explicitly enabling CH2 in the vendor UI. Therefore that first call must not be classified as generic startup noise.

Observed initial state after CH2 enable:

```text
arg2 = 6
arg3 = 6
arg4 = 0
arg5 = 1
```

The next two calls repeated the same scalar state. They are treated as refresh/re-application calls.

The explicit CH2 test sequence then produced:

```text
CH2 DC, 1 V/div     -> arg2=6, arg3=6, arg4=0, arg5=0
CH2 DC, 500 mV/div  -> arg2=6, arg3=5, arg4=0, arg5=0
CH2 DC, 2 V/div     -> arg2=6, arg3=7, arg4=0, arg5=0
CH2 AC, 2 V/div     -> arg2=6, arg3=7, arg4=0, arg5=1
CH2 DC, 2 V/div     -> arg2=6, arg3=7, arg4=0, arg5=0
CH2 DC, 1 V/div     -> arg2=6, arg3=6, arg4=0, arg5=0
```

Therefore the complete tested runtime mapping is:

```text
arg1 low16 = device index = 0
arg2       = CH1 vertical range code
arg3       = CH2 vertical range code
arg4       = CH1 coupling: 0=DC, 1=AC
arg5       = CH2 coupling: 0=DC, 1=AC
arg6 low16 = 0
```

Verified vertical range codes on both channels:

```text
5 = 500 mV/div
6 = 1 V/div
7 = 2 V/div
```

All traced calls returned a low-word success value of 1.

### CH2 enable/disable remains unresolved

Because enabling CH2 caused a `dsoSetVoltageAndCoupling` call, the vendor application clearly reapplies the channel's analog state when the channel is enabled. However, no dedicated enable bit is visible in the six low-16 scalar arguments of this call: the first enable-triggered call used the same range/coupling-shaped values later seen during ordinary analog updates.

Therefore, CH2 enabled/disabled state must not be inferred from `dsoSetVoltageAndCoupling` alone. The actual channel-enable control is still to be located in another setter/state path before the bridge exposes channel enable/disable.



## Channel enable remains a separate control path

Operator clarification for the CH2 analog trace: the first observed `dsoSetVoltageAndCoupling` call was triggered by enabling CH2.

That call carried the same range/coupling-shaped scalar fields used by ordinary analog updates. Therefore the voltage/coupling export is re-applied when a channel is enabled, but no independent enable bit has yet been identified in its six scalar arguments.

A read-only runtime trace mode is available for the candidate internal export:

```text
_dsoSetChIn@8
```

Use:

```powershell
powershell -ExecutionPolicy Bypass \
  -File .\tools\Trace-AnalogConfig.ps1 \
  -Mode channel \
  -MaxCalls 16 \
  -IdleTimeoutMs 30000 \
  -TotalTimeoutMs 120000
```

Recommended sequence:

```text
start: CH1 enabled, CH2 disabled
enable CH2
disable CH2
enable CH2
disable CH1
enable CH1
```

Keep V/div, coupling, timebase, trigger and probe settings unchanged. The candidate export is traced only; it is not invoked by the bridge until its semantics are proven.


## _dsoSetChIn@8 is an internal helper, not yet a channel-enable ABI

A runtime trace of `_dsoSetChIn@8` captured 16 completed calls, but every call returned to `0x100053FD`.

The vendor DLL base is `0x10000000`, so the return site is RVA `0x53FD`. This lies inside the already identified `dsoSetTriggerAndSampleRateNew` routine at RVA `0x53B0`.

Observed low-16 argument pairs were:

```text
call 1:  arg1=0, arg2=0
calls 2..16: arg1=0, arg2=2
```

Therefore `_dsoSetChIn@8` is being called internally by the New trigger/sample-rate setter and cannot currently be treated as the application's CH1/CH2 UI enable/disable setter.

The 16-call trace budget was consumed by repeated internal calls before the requested UI toggle sequence could provide distinct evidence.

Do not expose `_dsoSetChIn@8` as a bridge control until its second argument semantics are independently proven.

The actual channel enable/disable state remains unresolved and must be located at the original application's state/call-site level rather than inferred from this internal helper.


## External corroboration from NI / LabVIEW community

The following NI Community findings are not treated as protocol truth for the DSO-1102 by themselves, but they materially corroborate the reverse-engineered architecture and Hantek-family behavior observed in this project.

### Voltcraft / Hantek OEM and direct-DLL architecture

An NI Community thread for the Voltcraft DSO-2150 identifies it as a Hantek OEM product. An NI employee inspected the manufacturer's LabVIEW VI and stated that it calls the vendor `dso_2150usb.dll` directly rather than using VISA.

This strongly supports the DSO1102_Controller architecture of preserving the manufacturer's USB driver and calling the vendor DLL instead of replacing the device stack with VISA/WinUSB.

### Shared Hantek DSO-2000 family SDK lineage

An older NI Community post advertises second-development libraries/examples for Hantek DSO-2090 / DSO-2150 / DSO-2250 USB across LabVIEW, VB, VC, Delphi and C++Builder.

This is consistent with the DSO-1102 vendor DLL carrying DSO2250USB metadata and sharing export names / behavior with older Hantek DSO-2000-family SDKs. The metadata should therefore be treated as evidence of shared code lineage, not as proof that the physical DSO-1102 is a DSO-2250.

### dsoGetChannelData output buffers must be preallocated

NI Community analysis of the manufacturer's DSO-2090 LabVIEW VI specifically identifies `dsoGetChannelData()` as receiving two output arrays. The inspected VI had hidden arrays preallocated to 30,000 elements each.

The same discussion notes that external C code cannot resize LabVIEW arrays and the caller must allocate sufficient output space before the DLL call.

This corroborates the bridge design that pins caller-owned waveform buffers before invoking `dsoGetChannelData`.

### Calling convention and buffer errors

A Hantek DSO-2090 thread around LabVIEW error 1097 highlights two relevant failure classes:
- mismatch between stdcall and C calling convention;
- insufficiently allocated output buffers.

The DSO1102_Controller runtime/static work has independently confirmed stdcall-style stack cleanup for the relevant vendor exports, so the NI discussion provides external corroboration rather than the primary ABI source.

### 32-bit vendor DLL requires a 32-bit caller

NI Community guidance is explicit that a 32-bit process cannot directly call a 64-bit DLL and vice versa. A recommended workaround is a matching-bitness helper process communicating with the main application.

This directly matches the project's x86 bridge + x64 controller architecture.

### Hantek waveform data is exposed as 8-bit ADC-like values

A later Hantek LabVIEW thread reports waveform levels in the 0..255 range and explicitly associates this with an 8-bit oscilloscope. The same post reports audible relay switching as vertical-range codes change.

This is consistent with DSO1102_Controller observations that normal decoded waveform samples are byte-range values expanded to UInt16 and that vertical range selection drives analog relay/range state.

### Caution: arbitrary waveform buffer sizes are unsafe

A Hantek DSO3064 LabVIEW user reported memory corruption / hangs when simply increasing the waveform buffer beyond the size expected by that SDK path.

This supports the project's conservative strategy:
- use runtime-observed buffer behavior;
- allocate guarded buffers;
- validate decoded sample ranges;
- do not infer safe record lengths merely from available memory.

### Project impact

External NI evidence therefore reinforces the following current design decisions:

```text
1. Keep the original Hantek/Voltcraft driver stack.
2. Use the vendor DLL rather than VISA for the legacy USB DSO family.
3. Keep the vendor DLL inside a 32-bit bridge process.
4. Treat dsoGetChannelData output memory as caller-owned and preallocated.
5. Preserve stdcall ABI handling.
6. Treat normal waveform data as 8-bit ADC samples expanded into larger host types.
7. Do not guess record size or blindly enlarge buffers without runtime verification.
```


## NI/Hantek evidence for 512K deep-memory lineage

Additional external evidence strengthens the interpretation of the vendor DLL's DSO2250 lineage.

A Hantek DSO-2000 family datasheet lists DSO-2250, DSO-2150 and DSO-2090 together and specifies:

```text
vertical resolution: 8 bit
gain range:          10 mV/div .. 5 V/div, 9 steps
time-base range:     4 ns .. 1 h, 38 steps
coupling:            AC / DC / GND
```

Most importantly, the documented acquisition depths are:

```text
DSO-2250: 10K .. 512K samples/channel
DSO-2150: 10K .. 512K samples/channel
DSO-2090: 10K .. 32K samples/channel
```

This independently matches the DSO1102_Controller runtime observation that `dsoGetChannelData` populates exactly 524,288 decoded samples per channel in the characterized deep-memory modes.

The same family datasheet advertises LabVIEW/VB/VC/CVI second-development support. NI Community posts independently show that Hantek distributed SDK manuals, LabVIEW VIs and DLLs for these devices.

This does not prove that the physical DSO-1102 is electrically identical to a DSO-2250. It does, however, materially support the conclusion that the DSO1102 vendor DLL derives from the same 2250/2150 second-development codebase and that the observed 512 KiSample record depth is intentional family behavior rather than an accidental overrun.

### NI evidence on dsoGetChannelData buffers

In an NI Community inspection of the manufacturer's DSO-2090 LabVIEW VI, `dsoGetChannelData()` was found to receive two caller-owned output arrays preallocated to 30,000 elements each.

NI contributors explicitly warned that the external C DLL cannot resize LabVIEW arrays and that insufficient output allocation is a common cause of corruption/error 1097.

This further validates the bridge's design:
- caller-owned pinned buffers;
- guard allocation larger than the expected decoded record;
- post-call ADC-range validation;
- runtime measurement of actual written extent.

### Historical SecondDesign package

An NI Community thread for the Voltcraft/Hantek DSO-2100 includes an attachment named:

```text
SecondDesignDSO-2100USB_Ver5.0.0.1_English.zip
```

An NI contributor who opened the archive reported that the function declarations are contained in `SecondDesignManual.txt`.

This is potentially the closest historical SDK documentation found so far. The attachment itself is not currently retrievable through the indexed forum page, so no declaration from it is treated as evidence until the original archive/manual can be recovered.


## External DSO-2100 control-state structure

An old FreeBASIC implementation for the Voltcraft/Hantek DSO-2100 exposes a reconstructed packed `HARDWARE_CONTROL_DATA` structure used with the older `port_init` API.

Relevant fields:

```text
time_d_va       s32
tri_in_sel      s16   trigger slope/off state
ch1_div         s16
ch1_in_sel      s16   0=DC, 1=AC (when not grounded)
ch2_div         s16
ch2_in_sel      s16   0=DC, 1=AC (when not grounded)
ram_rw_mode     s16
ram_copy_mode   s16   time-base mode
ram_copy_delay  s16
ho_mode         s16   0=Auto, 1=Normal, 2=Single
ho_mode1        s16
CH              s16   0=CH1+CH2, 1=CH1, 2=CH2
TRI_12E         s16   1=CH1, 2=CH2, 3=EXT, 4=ALT
Ch1_To_Gnd      s16
Ch2_To_Gnd      s16
```

This older API is not ABI-compatible with the DSO-1102 DLL and must not be copied directly. However, it provides strong family-level evidence for three concepts relevant to the current unresolved state:

1. channel visibility/selection is a separate state from AC/DC coupling;
2. GND is a separate state from AC/DC;
3. trigger source is a separate channel selector.

This supports keeping DSO-1102 channel-enable and GND semantics separate from the already verified `dsoSetVoltageAndCoupling` ABI.

### Vertical range lineage

The DSO-2100 wrapper maps its seven range modes as:

```text
0 = 50 mV/div
1 = 100 mV/div
2 = 200 mV/div
3 = 500 mV/div
4 = 1 V/div
5 = 2 V/div
6 = 5 V/div
```

The later DSO-2000 family datasheet documents nine gain steps from 10 mV/div through 5 V/div.

The DSO-1102 runtime trace has independently verified:

```text
5 = 500 mV/div
6 = 1 V/div
7 = 2 V/div
```

These observations are exactly consistent with a nine-step sequence:

```text
0 = 10 mV/div   [inferred]
1 = 20 mV/div   [inferred]
2 = 50 mV/div   [inferred]
3 = 100 mV/div  [inferred]
4 = 200 mV/div  [inferred]
5 = 500 mV/div  [runtime verified]
6 = 1 V/div     [runtime verified]
7 = 2 V/div     [runtime verified]
8 = 5 V/div     [inferred]
```

Only codes 5, 6 and 7 are currently promoted as DSO-1102 protocol facts. The remaining entries are a strong family-based hypothesis requiring direct runtime tracing.

### DSO-2100 time-base lineage

The older wrapper exposes 35 time-base modes and a separate `ram_copy_mode`. Its mapping includes:

```text
mode 15 = 0.2 ms
mode 16 = 0.5 ms
mode 17 = 1 ms
mode 18 = 2 ms
mode 19 = 5 ms
```

The DSO-1102 uses different verified codes (15=400 us, 16=1 ms, 17=2 ms, 18=4 ms), so exact numeric time-base codes are model/API-generation specific and must not be transferred across families.

The useful family-level concept is that horizontal scale is represented by a discrete mode field, while capture/record behavior can be controlled by additional memory-related fields.


## libsigrok / OpenHantek family corroboration

Independent Hantek-family reverse engineering in libsigrok provides several strong structural matches to the DSO-1102 observations. This material is used only as behavioral/reference evidence; no GPL source code is copied into this project.

### 512K record mode

The libsigrok Hantek DSO driver defines two record sizes for its DSO-2250 profile:

```text
10,240 samples
524,288 samples
```

This exactly matches the short and deep-memory sizes independently observed through the DSO1102 vendor DLL.

### Vertical ranges

The Hantek-family V/div table is:

```text
0 = 10 mV/div
1 = 20 mV/div
2 = 50 mV/div
3 = 100 mV/div
4 = 200 mV/div
5 = 500 mV/div
6 = 1 V/div
7 = 2 V/div
8 = 5 V/div
```

The DSO-1102 runtime trace independently verified codes 5, 6 and 7 as 500 mV/div, 1 V/div and 2 V/div, respectively.

The remaining codes are therefore strongly corroborated family mappings, but remain marked inferred for the DSO-1102 until runtime-tested.

### Time-base family

The Hantek-family table includes:

```text
10 us, 20 us, 40 us,
100 us, 200 us, 400 us,
1 ms, 2 ms, 4 ms,
10 ms, 20 ms, 40 ms,
100 ms, 200 ms, 400 ms
```

The DSO-1102's verified 400 us / 1 ms / 2 ms / 4 ms profiles are therefore native members of the same discrete family progression.

### Capture state 3

The family driver distinguishes a DSO-2250-specific capture-ready state with numeric value 3.

This independently corroborates the DSO-1102 runtime finding that state code 3 is the readable/ready state for the vendor-DLL acquisition path.

### Separate 2250 control operations

The 2250-family protocol has distinct operations for:

```text
set channels
set trigger source
set record length
set sample rate
set trigger position / buffer state
```

This aligns closely with the internal DSO1102 vendor-DLL exports already identified:

```text
_dsoSetChIn@8
_dsoSetTrigIn@32
_dsoSetRamLength@8
_dsoSetSampleRate@8
_dsoSetTriggerLength@16
```

This is strong evidence that the DSO1102 DLL's `dsoSetTriggerAndSampleRateNew` routine is an orchestration wrapper around the same family of lower-level state operations.

### Channel mode hypothesis

The generic DSO-2090/2150 family uses:

```text
0 = CH1 only
1 = CH2 only
2 = both channels
```

However, the DSO-2250 has a dedicated channel command and a different enum:

```text
0 = CH1 only
1 = no channels
2 = CH1 + CH2
3 = CH2 only
```

The DSO-1102 trace of `_dsoSetChIn@8` observed:

```text
arg2 = 0 before CH2 was enabled
arg2 = 2 after CH2 was enabled while CH1 remained enabled
```

This exactly matches the DSO-2250-specific CH1-only -> both transition.

The remaining value to verify directly on the DSO-1102 is `3 = CH2 only`. Value `1 = no channels` should not be inferred as a usable UI state until explicitly observed.

### Raw waveform scaling

The family driver describes normal waveform samples as unsigned 8-bit values 0..255 and maps a channel's full ADC span across eight vertical divisions.

This agrees with the DSO1102_Controller observation that normal decoded samples are byte-range ADC values expanded into UInt16 containers.

Physical-voltage conversion in DSO1102_Controller should therefore eventually use the verified V/div setting plus per-range calibration/offset information, not a single global volts-per-count constant.


## Channel-level calibration block: statically verified layout

Direct disassembly of `DSO1102USB.dll` export `dsoSetOffset` (RVA `0x3180`) resolves the 44 packed calibration words.

The exact layout is:

```text
words  0..17  = CH1, 9 V/div ranges x {start,end}
words 18..35  = CH2, 9 V/div ranges x {start,end}
words 36..43  = trigger/additional calibration pairs
```

For CH1, range code `n` selects:

```text
start = calibration[2*n]
end   = calibration[2*n + 1]
```

For CH2:

```text
start = calibration[18 + 2*n]
end   = calibration[18 + 2*n + 1]
```

The DLL contains a nine-way switch for each channel, proving that the V/div code range is 0..8.

The offset interpolation uses the exact constant `1/255`:

```text
channelRaw =
    calibrationStart
    + (255 - positionByte)
      * (calibrationEnd - calibrationStart)
      / 255
```

The remaining words are consumed by the third offset/trigger path as:

```text
selector 0 -> words 36,37
selector 1 -> words 38,39
words 40,41 -> not referenced by dsoSetOffset
selector other -> words 42,43
```

This confirms that the first 36 packed words are not merely family-level evidence; their channel/range ordering is directly verified in the DSO-1102 vendor DLL.

The bridge's read-only `info` command now emits this decoded structure as CH1/CH2 range calibration tables plus the remaining trigger calibration pairs.


## Correct DSO-2250 channel-mode encoding

Historical OpenHantek source contains a DSO-2250-specific enum:

```text
BUSED_CH1    = 0
BUSED_NONE   = 1
BUSED_CH1CH2 = 2
BUSED_CH2    = 3
```

This differs from the generic DSO-2090/2150 channel enum, where CH2-only uses value 1.

A historical commit titled `DSO-2250 channel fix` explicitly notes that the DSO-2250 uses a different value for channel 2 and fixes the code path to use the DSO-2250-specific channel command.

This resolves the ambiguity seen in newer libsigrok source comments.

The DSO-1102 runtime trace already observed:

```text
_dsoSetChIn arg2 = 0 while CH1 only was enabled
_dsoSetChIn arg2 = 2 after CH2 was enabled while CH1 remained enabled
```

These values exactly match the DSO-2250-specific mapping.

Therefore the strongest current hypothesis for DSO-1102 is:

```text
0 = CH1 only          [runtime observed]
1 = no channels       [family-confirmed, DSO-1102 not yet observed]
2 = CH1 + CH2         [runtime observed]
3 = CH2 only          [family-confirmed, DSO-1102 not yet observed]
```

The remaining direct verification is to capture value 3 by starting with both channels active and then disabling CH1 while keeping CH2 active.

## Offset calibration formula from historical Hantek source

Historical OpenHantek code calculates vertical channel offset from the two range-specific calibration endpoints:

```text
minimum = big_endian_u16(OFFSET_START)
maximum = big_endian_u16(OFFSET_END)

offsetValue =
    normalizedPosition * (maximum - minimum)
    + minimum
    + 0.5
```

where `normalizedPosition` is in the range 0.0 .. 1.0.

A historical bugfix in `Control::setOffset` specifically corrected the high byte of `maximum` to come from `OFFSET_END`, confirming that START and END are separate big-endian 16-bit calibration values.

This is strong family-level evidence for interpreting the DSO-1102 channel-level block as two endpoint values per channel/per V-div range.

For the DSO-1102, the exact six-argument `dsoSetOffset` wrapper ABI is still to be recovered by runtime trace before the bridge invokes it.


## Static dsoSetOffset call-site reconstruction

Disassembly of the original vendor application identifies the wrapper at `0x4537B0`, which calls the function pointer at object offset `+0x230` using six stack arguments.

The DLL call is assembled as:

```text
arg1 = device index (low16 from object +0x28)
arg2 = pointer to object +0x36
arg3 = wrapper argument B
arg4 = wrapper argument C
arg5 = wrapper argument D
arg6 = pointer to object +0xD4
```

Immediately before the call, the wrapper copies 44 UInt16 calibration/configuration words into the object block beginning at `+0xD4`, and conditionally adjusts the first 18 pairs based on another wrapper input.

This strongly suggests that `arg6` is the packed channel/range calibration table used by the offset calculation.

Multiple callers feed `arg3` and `arg4` from two parallel UI/state getters and `arg5` from a 16-bit control value. Combined with the historical Hantek offset model, the leading semantic hypothesis is:

```text
arg1 = device index
arg2 = offset/range state pointer
arg3 = CH1 vertical-position value
arg4 = CH2 vertical-position value
arg5 = trigger-level/trigger-position value
arg6 = packed per-range calibration table
```

This mapping is not yet promoted to a verified ABI. The controlled `dsoSetOffset` runtime trace should test it by moving only CH1 vertically while leaving CH2, trigger level, V/div and coupling unchanged.


## dsoSetOffset DLL-level argument roles

Direct analysis of the `dsoSetOffset` export establishes these six argument roles:

```text
arg1 = device index
arg2 = pointer to live offset/trigger-position state
arg3 = CH1 V/div range code (0..8)
arg4 = CH2 V/div range code (0..8)
arg5 = three-way selector for the third/trigger calibration path
arg6 = pointer to the 44-word packed calibration table
```

Within the state pointed to by arg2:

```text
word 0 -> CH1 position input
word 1 -> CH2 position input
word 2 -> third-path position when arg5 == 0
word 3 -> third-path position when arg5 == 1
word 4 -> third-path position otherwise
```

The export computes three calibrated 16-bit values and passes the resulting six bytes to an internal helper that sends an eight-byte driver command beginning with:

```text
0x12 0x0F
```

followed by the six calculated bytes.

The exact semantic label for arg5 remains to be confirmed at runtime. Based on the original application's surrounding trigger logic and Hantek-family behavior, it is likely related to trigger-source selection, but that label is not yet promoted to a protocol fact.


## dsoSetVoltageAndCoupling DLL-level decomposition

Direct disassembly of `dsoSetVoltageAndCoupling` (RVA `0x63F0`) shows that the six-argument wrapper delegates to the exported helpers `dsoSetVoltageAndCouplingFirst` and `dsoSetVoltageAndCouplingSecond`.

The complete argument roles are now:

```text
arg1 = device index
arg2 = CH1 V/div range code
arg3 = CH2 V/div range code
arg4 = CH1 coupling code
arg5 = CH2 coupling code
arg6 = trigger/relay selector
```

The runtime trace had already verified:

```text
arg2: 5=500 mV/div, 6=1 V/div, 7=2 V/div
arg3: same mapping for CH2
arg4: 0=DC, 1=AC
arg5: 0=DC, 1=AC
```

### Gain command

`dsoSetVoltageAndCouplingFirst` maps each 0..8 range code into a repeating 1/2/5 gain triplet:

```text
range codes 0,3,6 -> gain subcode 0
range codes 1,4,7 -> gain subcode 1
range codes 2,5,8 -> gain subcode 2
```

It combines CH1 and CH2 gain subcodes into the vendor gain command (command byte 0x07).

This is exactly consistent with the nine-step V/div sequence:

```text
10mV, 20mV, 50mV,
100mV, 200mV, 500mV,
1V, 2V, 5V
```

Only the values already runtime-tested on the DSO-1102 are treated as direct UI-code verification; the full labels are additionally corroborated by the Hantek-family SDK/source material.

### Relay thresholds and coupling

`dsoSetVoltageAndCouplingSecond` separately controls the analog relay state.

For CH1:

```text
range < 3  -> both low-range relay groups active
range 3..5 -> intermediate-range relay group active
range 6..8 -> neither low-range relay group active
```

CH2 has the equivalent independent relay groups.

The coupling logic is explicit:

```text
coupling == 1 -> AC relay state
coupling != 1 -> non-AC relay state
```

The DSO-1102 runtime trace identifies the tested non-AC state value 0 as DC.

### Sixth argument

The relay helper sets the external-trigger relay only when:

```text
arg6 == 3
```

This matches historical Hantek/Voltcraft control-state documentation in which trigger selector value 3 denotes EXT.

Thus `arg6` is strongly identified as the trigger-source/relay selector, with value 3 selecting the external-trigger relay. A dedicated DSO-1102 trigger-source trace is still desirable before all selector values are promoted as verified UI mappings.


## Trigger-source family mapping and dedicated trace

Historical DSO-2250 protocol sources show a dedicated 8-byte trigger command. Its trigger byte contains a 2-bit source field plus a 1-bit slope field.

For the DSO-2250-specific path, the external family code maps analog channels differently from the older 2090/2150 path. The historical control layer feeds the 2250 trigger command with:

```text
CH1 -> source field 2
CH2 -> source field 3
EXT -> source field 0
```

This is family-level evidence only. The DSO-1102 vendor DLL exposes the eight-slot helper `_dsoSetTrigIn@32`, and its exact scalar mapping must be captured directly before the bridge invokes it.

The call tracer now supports a complete low-16 argument-signature deduplication mode. The helper script exposes:

```text
Trace-AnalogConfig.ps1 -Mode trigger
```

which observes `_dsoSetTrigIn@32` with eight arguments and suppresses consecutive calls whose complete low-16 signature is unchanged.

Recommended first runtime sequence:

```text
start: trigger CH1, rising
CH2
EXT
CH1
```

Keep trigger level and slope fixed for this first run. After source mapping is established, a separate rising/falling trace can isolate the slope field.

## Deep-memory caller-buffer safety

Historical OpenHantek DSO-2250 model data distinguishes two-channel and one-channel record-length limits. In that family implementation the deep record can reach:

```text
two active channels: 524,288 samples/channel
one active channel:  1,048,576 samples
```

The DSO-1102 has directly demonstrated 524,288 written samples per channel, but the one-channel doubling has not yet been observed on this exact unit.

To avoid a possible caller-buffer overrun while that behavior is tested, the bridge now allocates 1,048,576 UInt16 elements per waveform buffer. This is a host-side safety margin only; it does not request a larger acquisition or send any additional device command.

Full-buffer analysis estimates the populated prefix from the last non-zero write before calculating ADC statistics, so an untouched zero-filled guard tail is not mistaken for real waveform data.


## Complete DSO-1102 Time/DIV code table

Direct analysis of the real `DSO1102USB.dll` shows that `_dsoSetSampleRate@8` switches on a 16-bit time-base field with valid values `0..37`.

The vendor EXE contains the ordered Time/DIV resource list. Aligning the 38 DLL codes with that list and the already runtime-verified anchors `15=400 us`, `16=1 ms`, `17=2 ms`, `18=4 ms` gives:

```text
 0 = 4 ns/div
 1 = 10 ns/div
 2 = 20 ns/div
 3 = 40 ns/div
 4 = 100 ns/div
 5 = 200 ns/div
 6 = 400 ns/div
 7 = 1 us/div
 8 = 2 us/div
 9 = 4 us/div
10 = 10 us/div
11 = 20 us/div
12 = 40 us/div
13 = 100 us/div
14 = 200 us/div
15 = 400 us/div
16 = 1 ms/div
17 = 2 ms/div
18 = 4 ms/div
19 = 10 ms/div
20 = 20 ms/div
21 = 40 ms/div
22 = 100 ms/div
23 = 200 ms/div
24 = 400 ms/div
25 = 1 s/div
26 = 2 s/div
27 = 4 s/div
28 = 10 s/div
29 = 20 s/div
30 = 40 s/div
31 = 1 min/div
32 = 2 min/div
33 = 4 min/div
34 = 10 min/div
35 = 20 min/div
36 = 40 min/div
37 = 1 h/div
```

The vendor EXE also contains a shared `2 ns/div` UI string, but the DSO-1102 DLL sample-rate switch has only 38 entries and the verified code anchors align exactly when the DSO-1102 table starts at 4 ns/div. Therefore the shared 2 ns/div resource is not assigned a DSO-1102 code.

Only codes already exercised through the real DSO-1102 hardware are currently exposed by the bridge's guarded self-init commands. The complete table is documentation for further tracing, not authorization to invoke untested profiles.

## dsoSetTriggerAndSampleRateNew channel-mode behavior

Direct disassembly of `dsoSetTriggerAndSampleRateNew` shows that the call to `_dsoSetChIn@8` is computed as:

```text
if (config.word[2] >= 10)
    channelMode = 2;
else
    channelMode = config.word[1];
```

Because `config.word[2]` is the Time/DIV code, this means:

```text
codes 0..9   (4 ns .. 4 us) : use config.word[1]
codes 10..37 (10 us .. 1 h) : force hardware channel mode 2
```

Therefore `_dsoSetChIn@8` is a hardware acquisition/samplerate channel-mode helper, not a direct UI visibility toggle.

The previously observed `arg2=2` during 400 us / 1 ms / 2 ms / 4 ms operation is now explained statically by the `>=10` branch and must not be interpreted as proof that both UI channels were enabled.

Historical DSO-2250 sources still provide a useful family hypothesis for fast-mode channel values:

```text
0 = CH1 hardware path
1 = none
2 = both
3 = CH2 hardware path
```

but values 1 and 3 must be tested at a fast Time/DIV code below 10 before being promoted for the DSO-1102.


## Correct vendor-application function-pointer table

A fresh pass over the original EXE's `GetProcAddress` initialization resolved an earlier shifted-slot ambiguity. The verified object slots are:

```text
+0x214 dsoGetChannelData
+0x218 dsoSearchDevice
+0x21C dsoGetDeviceAddress
+0x220 dsoSetTriggerAndSampleRate
+0x224 dsoSetTriggerAndSampleRateNew
+0x228 dsoGetLogicData
+0x22C dsoSetVoltageAndCoupling
+0x230 dsoSetOffset
+0x234 dsoCaptureStart
+0x238 dsoTriggerEnabled
+0x23C dsoGetChannelLevel
+0x240 dsoSetChannelLevel
+0x244 dsoGetCalData
+0x248 dsoGetCaptureState
+0x24C dsoForceTrigger
+0x250 dsoSetFilt
+0x254 dsoSetFiltAndVoltageData
+0x258 dsoFFT
+0x25C dsoFFTGetSamples
+0x260 dsoFFTBuffer
+0x264 dsoGetDeviceID
+0x268 dsoSetDeviceID
+0x26C dsoSetCalData
+0x270 dsoGetCalTrigState
+0x274 InitLevelRange
+0x278 dsoGetFPGAVersion
```

`dsoGetLogicData` at RVA `0x5980` is a trivial stub in this DLL revision: it returns success without a hardware transaction.

## UI channel state versus fast-sampling channel mode

The original application's per-channel state object contains three relevant fields:

```text
+0x0C : 32-bit 0/1 state toggled by the channel UI
+0x14 : 16-bit coupling selection
+0x18 : 16-bit V/div range code
```

The common analog-state wrapper calls:

```text
dsoSetFiltAndVoltageData(
    device,
    channelStateA,
    channelStateB,
    channel1Range,
    channel2Range)

dsoSetVoltageAndCoupling(
    device,
    channel1Range,
    channel2Range,
    channel1Coupling,
    channel2Coupling,
    triggerSelector)
```

The two `+0x0C` fields are changed by UI handlers using an explicit 0/1 toggle and are passed only through `dsoSetFiltAndVoltageData`.

This is a different mechanism from `_dsoSetChIn@8`, which `dsoSetTriggerAndSampleRateNew` forces to hardware mode 2 for Time/DIV codes >=10.

Therefore normal UI channel enable/disable must be reconstructed from `dsoSetFiltAndVoltageData`, while `_dsoSetChIn@8` is reserved for the high-speed acquisition topology.

A dedicated observational trace is available as:

```powershell
.\tools\Trace-AnalogConfig.ps1 -Mode enable
```

Static analysis establishes that arguments 4 and 5 are the CH1/CH2 V/div range codes. Runtime tracing is still required to prove whether argument 2 or 3 corresponds to CH1 and to establish the exact 0/1 polarity.

## GND coupling is implemented in software

The vendor UI defines coupling values:

```text
0 = DC
1 = AC
2 = GND
```

Direct disassembly of the hardware relay path shows only an AC distinction:

```text
coupling == 1 -> AC relay state
coupling != 1 -> non-AC relay state
```

Thus DC and GND intentionally produce the same hardware relay configuration.

The original application's waveform-processing path separately checks:

```text
channel.coupling == 2
```

When true, it does not copy the acquired waveform samples into the display buffer. Instead, it fills the displayed channel with a constant value derived from the channel's current vertical-position state, producing a flat ground-reference trace.

Therefore GND on this DSO-1102 software stack is a display/data-processing mode, not a distinct input-relay command. A replacement controller should reproduce GND by suppressing the displayed/acquired signal and drawing the ground-reference level while leaving the hardware coupling in the non-AC state.

This distinction matters for diagnostics: selecting GND in the replacement UI must not be presented as proof that the BNC input has been physically shorted to ground.
