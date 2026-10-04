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
