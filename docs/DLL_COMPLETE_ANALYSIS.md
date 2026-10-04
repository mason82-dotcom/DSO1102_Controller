# Complete analysis of DSO1102USB.dll

This document is the primary static-analysis reference for the original Voltcraft/ODM `DSO1102USB.dll` used by DSO1102_Controller.

Primary source of truth: the original binary supplied with the vendor application. OpenHantek/libsigrok material is used only as external family-level corroboration where explicitly stated.

## Binary identity

```text
SHA-256       93CA581F4DE649C54957CBD84B25A067ABAA89A078F765B4C7B6958F895C9CC1
format        PE32 / i386
image base    0x10000000
timestamp     2013-07-24 02:14:07
FileVersion   1.0.0.1
Description   DSO2250USB DLL
ProductName   DSO2250USB Dynamic Link Library
OriginalName  DSO2250USB.DLL
signature     none
```

Useful embedded strings include:

```text
D1102
\\.\
-%d
TEST REPORT
DSO-2250 USB
CH1 :
CH2 :
Note  :
```

The runtime device path is constructed as `\\.\D1102-%d`. The DSO2250 metadata and protocol structure are strong evidence of shared Hantek DSO-2250 lineage, but do not mean that the physical DSO-1102 is electrically identical to a DSO-2250.

## Imported operating-system services

The hardware-facing imports are small and informative:

```text
CreateFileA
DeviceIoControl
CreateEventA
WaitForSingleObject
SetEvent
CloseHandle
Sleep
```

The DLL does not use WinUSB, HID or VISA. It opens the vendor driver device and communicates through DeviceIoControl.

## Driver transport layers

The DLL contains three distinct driver-transport paths.

### Bulk/command write

```text
IOCTL 0x222051
```

Used for the oscilloscope bulk command protocol. Typical packets begin with a command byte followed by `0x0F` or `0x00`.

### Bulk/data read

```text
IOCTL 0x22204E
```

Used to retrieve capture state, FPGA response bytes and waveform data.

### Asynchronous vendor-control transfer

```text
IOCTL 0x222059
```

An internal generic helper creates an event, packages a ten-byte driver request descriptor, invokes this IOCTL, waits for completion and closes the event. Higher-level DLL functions use this wrapper for request IDs such as `0xA2`, `0xB2`, `0xB3`, `0xE4`, `0xE5`, `0xF0` and `0xF1`.

DSO1102_Controller must continue to call the vendor DLL rather than reproducing these raw driver transactions unless a future, separately verified transport backend is intentionally developed.

## Complete export inventory

| Ord | Export | RVA | ABI / stack cleanup | Classification | Project value |
|---:|---|---:|---|---|---|
| 1 | `dsoForceTrigger` | `0x5990` | 1 arg / `ret 4` | hardware | useful |
| 2 | `dsoGetCaptureState` | `0x5A90` | 2 args / `ret 8` | hardware read | essential |
| 3 | `dsoSetTriggerAndSampleRate` | `0x5430` | 3 args / `ret 0x0C` | legacy hardware setter | deprecated/unused by observed UI |
| 4 | `dsoSetVoltageAndCoupling` | `0x63F0` | 6 args / `ret 0x18` | hardware setter | essential |
| 5 | `dsoSetOffset` | `0x3180` | 6 args / `ret 0x18` | hardware setter | essential |
| 6 | `dsoCaptureStart` | `0x46D0` | 1 arg / `ret 4` | hardware | essential |
| 7 | `dsoTriggerEnabled` | `0x45F0` | 1 arg / `ret 4` | hardware | essential |
| 8 | `dsoSearchDevice` | `0x6430` | 1 arg / `ret 4` | device discovery | essential |
| 9 | `dsoGetChannelLevel` | `0x23F0` | 3 args / `ret 0x0C` | calibration read | useful/read-only |
| 10 | `dsoSetChannelLevel` | `0x2670` | 3 args / `ret 0x0C` | persistent calibration write | locked out |
| 11 | `dsoSetVoltageAndCouplingFirst` | `0x5C20` | 5 args / `ret 0x14` | gain helper | reference/internal |
| 12 | `dsoSetVoltageAndCouplingSecond` | `0x5ED0` | 6 args / `ret 0x18` | relay helper | reference/internal |
| 13 | `dsoSetFilt` | `0x6180` | 4 args / `ret 0x10` | filter setter | useful |
| 14 | `dsoGetCalData` | `0x24D0` | 3 args / `ret 0x0C` | calibration read | useful/read-only |
| 15 | `dsoSetCalData` | `0x25A0` | 3 args / `ret 0x0C` | persistent calibration write | locked out |
| 16 | `dsoGetChannelData` | `0x3820` | 8 args / `ret 0x20` | waveform read | essential |
| 17 | `dsoFFT` | `0x6910` | 4 args / `ret 0x10` | software DSP | optional/ignore |
| 18 | `dsoFFTGetSamples` | `0x6DB0` | 3 args / `ret 0x0C` | software resampler | optional/ignore |
| 19 | `dsoFFTBuffer` | `0x2750` | 4 args / `ret 0x10` | software buffer/window helper | optional/ignore |
| 20 | `dsoGetDeviceID` | `0x6EC0` | 2 args / `ret 8` | identity read | diagnostic |
| 21 | `dsoSetDeviceID` | `0x6F80` | 2 args / `ret 8` | persistent identity write | locked out |
| 22 | `dsoSetTriggerAndSampleRateNew` | `0x53B0` | 8 args / `ret 0x20` | orchestration setter | essential |
| 23 | `InitLevelRange` | `0x5DB0` | 1 arg / `ret 4` | local relay-cache reset | useful internally |
| 24 | `dsoGetCalTrigState` | `0x2E60` | 1 arg / `ret 4` | hardware read | diagnostic |
| 25 | `dsoGetLogicData` | `0x5980` | 1 arg / `ret 4` | stub | ignore |
| 26 | `dsoGetDeviceAddress` | `0x2270` | 2 args / `ret 8` | identity/address read | diagnostic |
| 27 | `dsoGetFPGAVersion` | `0x2FE0` | 1 arg / `ret 4` | hardware read/cache | essential diagnostic |
| 28 | `dsoSetFiltAndVoltageData` | `0x6280` | 5 args / `ret 0x14` | combined hardware setter | useful/reference |
| 29 | `_dsoReadFlash@8` | `0x70F0` | 2 args / `ret 8` | large persistent-memory read | keep disabled unless explicitly needed |
| 30 | `_dsoSetChIn@8` | `0x5120` | 2 args / `ret 8` | low-level acquisition channel mode | internal/fast-rate relevant |
| 31 | `_dsoSetDeviceAddress@8` | `0x2330` | 2 args / `ret 8` | persistent address write | locked out |
| 32 | `_dsoSetLogicData@8` | `0x64F0` | 2 args / `ret 8` | low-level legacy logic command | low priority |
| 33 | `_dsoSetRamLength@8` | `0x5030` | 2 args / `ret 8` | low-level record mode | important internal |
| 34 | `_dsoSetSampleRate@8` | `0x47B0` | 2 args / `ret 8` | low-level samplerate | essential internal |
| 35 | `_dsoSetTrigIn@32` | `0x5220` | 8 args / `ret 0x20` | low-level trigger | essential internal |
| 36 | `_dsoSetTriggerLength@16` | `0x4E60` | 4 args / `ret 0x10` | pre/post-trigger buffer | essential internal |
| 37 | `_dsoWriteFlash@8` | `0x7040` | 2 args / `ret 8` | persistent flash write | permanently locked out |
| 100 | `PageGDICalls` | `0x27B0` | 3 args / `ret 0x0C` | MFC/GDI report renderer | ignore for hardware control |

## Bulk command IDs recovered directly from the DLL

| Command | Meaning in this DLL | Packet observations |
|---:|---|---|
| `0x00` | filter | `00 0F <bits> ...` |
| `0x01` | legacy combined trigger/samplerate | 12-byte legacy packet |
| `0x02` | force trigger | `02 00` |
| `0x03` | capture start | `03 00` |
| `0x04` | trigger enable | `04 00` |
| `0x05` | request waveform data | `05 00`, followed by reads |
| `0x06` | get capture state | `06 00`, followed by read |
| `0x07` | analog gain | packed CH1/CH2 1/2/5 gain subcodes |
| `0x08` | low-level logic-data setter | `08 0F <byte> ...` |
| `0x0B` | acquisition channel mode | `_dsoSetChIn` |
| `0x0C` | trigger/control | `_dsoSetTrigIn`, 10-byte DSO-1102 packet |
| `0x0D` | RAM/record mode | `_dsoSetRamLength` |
| `0x0E` | samplerate/timing | `_dsoSetSampleRate`, 10-byte DSO-1102 packet |
| `0x0F` | trigger/pre-post buffer position | `_dsoSetTriggerLength` |
| `0x12` | calibrated CH1/CH2/trigger offsets | `12 0F` + six bytes |
| `0x13` | calibration-trigger state read | followed by data read |
| `0x14` | FPGA version read | followed by data read |

The command numbering strongly matches historical Hantek DSO-2250 protocol lineage. The DSO-1102 variants of commands `0x0C`, `0x0E` and `0x0F` contain extensions and must not be assumed byte-for-byte identical to older public source.

## Vendor-control requests recovered directly from the DLL

| Request | Use | Safety |
|---:|---|---|
| `0xA2`, value `0x08` | channel-level/calibration byte block | read useful; write locked out |
| `0xA2`, value `0x0A` | device address / device-ID byte in this DLL revision | read diagnostic; write locked out |
| `0xA2`, value `0x60` | two calibration bytes | read useful; write locked out |
| `0xB2` | speed/status handshake | internal |
| `0xB3` | begin-command/setup handshake | internal |
| `0xE4` | individual analog-relay state update | internal; use high-level setter |
| `0xE5` | combined filter/gain state byte | internal; use high-level setter |
| `0xF0` | flash write | dangerous / locked out |
| `0xF1` | flash read | large persistent-memory access; disabled by default |

### Flash geometry

`_dsoWriteFlash@8` and `_dsoReadFlash@8` loop `0x2000` times with `0x40` bytes per transfer:

```text
8192 * 64 = 524,288 bytes
```

The write path uses request `0xF0`; the read path uses `0xF1`. The control value/index is `0x1E00`. The bridge must never call the flash-write export during normal operation.

## Device discovery

`dsoSearchDevice(index)` builds the device path:

```text
\\.\D1102-<index>
```

and attempts to open it. Runtime probing has confirmed index 0 as the connected DSO-1102 and indices 1..3 as absent.

## FPGA version

`dsoGetFPGAVersion(device)` sends command `14 00`, reads the response and computes:

```text
version = response[0] + 1000 * response[1]
```

The result is cached per device at a DLL-global array beginning at VA `0x1000B088`.

The real scope returns:

```text
12001 = 0x2EE1
```

This value is functionally important. `dsoGetChannelData` explicitly compares the cached FPGA version against `0x2EE1` and uses version-specific waveform-decode paths. Therefore FPGA version 12001 is part of the acquisition compatibility profile, not merely display metadata.

## Capture-state protocol

`dsoGetCaptureState(device, &value)` sends `06 00`, then reads via `0x22204E`.

```text
return value = response[0]
*value       = response[2] | response[3] << 8
```

Runtime testing established:

```text
state 3 = CAPTURE_READY on this DSO-1102
```

This independently matches the DSO-2250 family ready-state value.

`dsoGetCalTrigState(device)` similarly sends `13 00` and returns the first response byte.

## Trigger/sample-rate orchestration

`dsoSetTriggerAndSampleRateNew` is the important modern setter used by the vendor application. Its verified eight-slot ABI is:

```text
arg1 = device index
arg2 = auxiliary scalar
arg3 = pointer to trigger/sample configuration
arg4 = auxiliary scalar
arg5 = auxiliary scalar
arg6 = auxiliary scalar
arg7 = auxiliary scalar
arg8 = 32-bit command value
```

The observed normal vendor calls use device 0, auxiliary args mostly zero and `arg8=2`.

The function orchestrates the lower-level helpers in this family:

```text
_dsoSetTrigIn
_dsoSetChIn
_dsoSetRamLength
_dsoSetSampleRate
_dsoSetTriggerLength
```

### Verified configuration words

```text
word[0]  = Trigger Source: 0 CH1, 1 CH2, 2 ALT, 3 EXT, 4 EXT/10
word[1]  = fast-timebase hardware channel mode state
word[2]  = Time/DIV code 0..37
word[3]  = trigger position percent
word[4]  = record/RAM state class; low-level helpers distinguish zero/non-zero
word[5]  = unresolved
word[6]  = contains a bit used by samplerate command byte 2
word[10] = decoded record-depth selector
word[13] = observed 256 in normal profile; exact high-level label still open
word[22] = observed 16368 in normal profile; exact high-level label still open
word[23..] = live channel-level/calibration state in the read profile
```

Runtime profiles use `word[3]=50`, and static analysis proves this is a 50% trigger position.

### Hardware channel mode

The New setter computes:

```text
if TimeDIV code >= 10:
    _dsoSetChIn mode = 2
else:
    _dsoSetChIn mode = config.word[1]
```

Thus `_dsoSetChIn` is an acquisition-topology helper, not a direct UI visibility switch. Fast Time/DIV codes 0..9 are the correct place to investigate CH1-only / CH2-only fast interleaving.

## Complete Time/DIV table

The DLL contains a 38-entry samplerate switch. The vendor EXE's ordered UI resources align as:

```text
 0  4 ns/div      10  10 us/div      20  20 ms/div      30  40 s/div
 1 10 ns/div      11  20 us/div      21  40 ms/div      31   1 min/div
 2 20 ns/div      12  40 us/div      22 100 ms/div      32   2 min/div
 3 40 ns/div      13 100 us/div      23 200 ms/div      33   4 min/div
 4 100 ns/div     14 200 us/div      24 400 ms/div      34  10 min/div
 5 200 ns/div     15 400 us/div      25   1 s/div       35  20 min/div
 6 400 ns/div     16   1 ms/div      26   2 s/div       36  40 min/div
 7   1 us/div     17   2 ms/div      27   4 s/div       37   1 h/div
 8   2 us/div     18   4 ms/div      28  10 s/div
 9   4 us/div     19  10 ms/div      29  20 s/div
```

The EXE contains a shared `2 ns/div` resource string, but there is no corresponding DSO-1102 code in the 38-entry DLL switch.

The complete low-level timing table is maintained separately in `docs/SAMPLERATE_TABLE.md`.

## Samplerate command

`_dsoSetSampleRate@8` builds command `0x0E`. In this DSO-1102 DLL the packet is 10 bytes and contains two 16-bit timing fields.

The primary field strongly matches the DSO-2250 one's-complement/downsampler convention:

```text
encoded word = 0x10001 - divider
```

For the runtime-verified deep profiles:

```text
400 us/div  primary 0xFFED -> divider 20
1 ms/div    primary 0xFFD9 -> divider 40
2 ms/div    primary 0xFF9D -> divider 100
4 ms/div    primary 0xFF39 -> divider 200
```

OpenHantek's DSO-2250 model independently uses a 100-MHz normal-mode base. Applying that base produces plausible hardware-rate candidates of 5 MHz, 2.5 MHz, 1 MHz and 0.5 MHz respectively. These are family-correlated candidates, not yet direct DSO-1102 physical-clock measurements.

The second DSO-1102 timing word has no counterpart in the public older 8-byte DSO-2250 samplerate packet and remains a genuine model/DLL-specific field.

## Record length and waveform transfer

`_dsoSetRamLength@8` distinguishes only:

```text
arg2 == 0  -> hardware record mode 1
arg2 != 0  -> hardware record mode 2
```

Therefore higher-level state values 5 and 6 observed at different vendor call stages do not represent different low-level RAM modes.

In `dsoGetChannelData`, `config.word[10]` selects decoded record size in the characterized normal path:

```text
word[10] == 0  -> 10,240 samples/channel
word[10] != 0  -> 524,288 samples/channel
```

The deep normal two-channel path reads `0x100000` raw bytes and deinterleaves them directly:

```text
1,048,576 raw bytes
-> 524,288 CH1 byte samples
-> 524,288 CH2 byte samples
```

Each raw byte is zero-extended into a UInt16 output element. The DLL subsequently rotates/reorders the record around the trigger point; it does not create a larger time grid by software interpolation in this path.

For fast Time/DIV paths below code 10 the DLL has separate branches influenced by channel mode, trigger state and record state. Historical DSO-2250 code strongly suggests a one-channel fast record up to 1,048,576 samples; the bridge allocates guard space for this, but the DSO-1102 mode still requires direct runtime verification.

### Waveform ABI

Runtime tracing established:

```c
ushort __stdcall dsoGetChannelData(
    ushort deviceIndex,
    ushort* bufferA,              // CH1
    ushort* bufferB,              // CH2
    void* triggerSampleConfig,
    void* offsetState,            // config pointer + 12 bytes
    uint32_t captureTriggerValue,
    ushort calibrationA,
    ushort calibrationB);
```

Normal decoded ADC values are 0..255 expanded to UInt16. The first output element can contain a signed/sentinel-like `0xFFxx` value and is excluded from normal ADC statistics.

The DLL also applies calibration-related adjustments derived from `calibrationA` and `calibrationB`; values greater than 10 are internally shifted by 10 in the early read path.

## Vertical ranges, gain and coupling

`dsoSetVoltageAndCoupling` decomposes into a bulk gain command plus individual relay updates.

### V/div range codes

```text
0 = 10 mV/div   family-correlated
1 = 20 mV/div   family-correlated
2 = 50 mV/div   family-correlated
3 = 100 mV/div  family-correlated
4 = 200 mV/div  family-correlated
5 = 500 mV/div  DSO-1102 runtime verified
6 = 1 V/div     DSO-1102 runtime verified
7 = 2 V/div     DSO-1102 runtime verified
8 = 5 V/div     family-correlated
```

The DLL itself proves nine range IDs and the repeating 1/2/5 gain grouping:

```text
0,3,6 -> gain subcode 0
1,4,7 -> gain subcode 1
2,5,8 -> gain subcode 2
```

### Coupling

Runtime tracing verified:

```text
0 = DC
1 = AC
```

The vendor UI also has `2 = GND`, but the relay code only distinguishes `coupling == 1` from `coupling != 1`. The original application implements GND by suppressing the displayed waveform and drawing a flat reference level; it is not a separate physical relay position in this software stack.

### Relay groups

The second voltage/coupling helper maintains eight cached relay states per device. `InitLevelRange` only fills this cache with `-1`; it does not itself issue a hardware transaction.

The relay grouping is:

```text
CH1 range 0..2 : two low-range relay groups active
CH1 range 3..5 : intermediate group active
CH1 range 6..8 : low-range groups inactive
CH2            : equivalent independent groups
coupling == 1  : AC state
coupling != 1  : non-AC state
trigger source == 3 : external-trigger relay
```

Changes are sent individually through control request `0xE4`, with 50-ms settling delays.

## Filter path

`dsoSetFilt(device,ch1,ch2,trigger)` packs three one-bit controls into command `00 0F`:

```text
bit 0 = CH1 bandwidth/filter
bit 1 = CH2 bandwidth/filter
bit 2 = trigger HF rejection
```

`dsoSetFiltAndVoltageData` is a combined five-argument path:

```text
arg1 = device index
arg2 = CH1 bandwidth/filter flag
arg3 = CH2 bandwidth/filter flag
arg4 = CH1 V/div range code
arg5 = CH2 V/div range code
```

It maps both range codes to the same 1/2/5 gain subcodes and packs:

```text
bits 0..1 = CH1 gain
bits 2..3 = CH2 gain
bit 4     = CH2 filter
bit 5     = CH1 filter
bits 6..7 = 0
```

The byte is sent through request `0xE5`, then the DLL sleeps 50 ms. The original application wrapper at `0x454590` supplies the filter flags from channel-object offset `+0x0C` and range codes from `+0x18`.

## Offset and calibration

`dsoGetChannelLevel` reads 88 bytes through `A2/value 08` and expands each byte into a UInt16. The vendor application packs these bytes into 44 big-endian UInt16 values.

Direct analysis of `dsoSetOffset` proves the layout:

```text
words  0..17 = CH1: 9 ranges x {start,end}
words 18..35 = CH2: 9 ranges x {start,end}
words 36..37 = trigger calibration for source 0 / CH1
words 38..39 = trigger calibration for source 1 / CH2
words 40..41 = not referenced by dsoSetOffset
words 42..43 = shared ALT/EXT/EXT10 calibration pair
```

The exact interpolation constant in the DLL is `1/255`:

```text
calibrated = start + (255 - positionByte) * (end - start) / 255
```

The calculated CH1, CH2 and trigger values are then multiplied by exactly `16.0`, split into high/low bytes and sent as:

```text
12 0F CH1_lo CH1_hi CH2_lo CH2_hi TRIG_lo TRIG_hi
```

through IOCTL `0x222051`.

The six-argument wrapper ABI is:

```text
arg1 = device index
arg2 = pointer to live position state
       word0 CH1 position
       word1 CH2 position
       word2 trigger position for CH1 source
       word3 trigger position for CH2 source
       word4 trigger position for other sources
arg3 = CH1 range code 0..8
arg4 = CH2 range code 0..8
arg5 = trigger source code
arg6 = pointer to 44-word calibration table
```

## Trigger source / slope / sweep / mode

Static analysis of the original application resolves the UI state exactly:

```text
Trigger Source: 0 CH1, 1 CH2, 2 ALT, 3 EXT, 4 EXT/10
Trigger Slope : 0 rising(+), 1 falling(-)
Trigger Sweep : 0 Auto, 1 Normal, 2 Single
Trigger Mode  : 0 Edge, 1 Pulse
```

`_dsoSetTrigIn@32` builds a ten-byte `0x0C` command. Byte 2 is packed as:

```text
bits 0..1 = translated source selector
bit 2     = helper arg4 bit0
bit 3     = helper arg3 bit0
bit 4     = helper arg6 bit0
bits 5..6 = helper arg7 bits0..1
bit 7     = helper arg5 bit0
```

Source translation performed by this helper:

```text
input 0 -> source bits 1
input 1 -> source bits 0
input 2,3,4 -> source bits 2
input >4 -> source bits from arg5
```

Bytes 4..7 contain arg8 as a little-endian 32-bit value. Byte 8 is read from DLL global `0x1000B084`; no direct write to this byte exists anywhere in the DLL and it resides in zero-initialized `.data`/BSS, so it is zero under normal DLL initialization unless some external/indirect memory corruption changes it. Byte 9 is `0xFF`.

## Trigger length / pre-post trigger

`_dsoSetTriggerLength@16` receives:

```text
record-state class
trigger position percent
Time/DIV code
```

from the New setter. It computes both the requested percentage and `100 - percentage`, confirming that `config.word[3]` is the trigger-position percentage. It builds the DSO-1102 `0x0F` buffer-position command and includes special slow/roll handling based on Time/DIV.

## Calibration reads

`dsoGetCalData` performs `A2/value 60`, reads two bytes and expands them to two UInt16 results.

The real scope returns:

```text
calibrationA = 1
calibrationB = 12
```

These values are passed back into `dsoGetChannelData` by the vendor application.

## Device address and device ID

Both `dsoGetDeviceAddress` and `dsoGetDeviceID` use `A2/value 0x0A` and read one byte, expanding it into a UInt16 result.

The corresponding setters also use the same value and write one byte. Because these functions alter device identity/configuration and are unnecessary for normal acquisition, the bridge keeps both setters disabled.

## Logic-data exports

`dsoGetLogicData` is a trivial stub in this DLL revision and returns success without a hardware transaction.

`_dsoSetLogicData@8` does send an `08 0F` bulk command with one data byte. No required analog-scope feature currently depends on it, so it is intentionally low priority.

## FFT exports

The FFT exports are software-side DSP and do not communicate with the oscilloscope.

`dsoFFT` allocates temporary floating-point/complex buffers, centers input samples around an 8-bit midpoint, applies scaling/window preparation, runs internal FFT routines and returns magnitudes.

`dsoFFTGetSamples` always treats `0x2800 = 10,240` source samples as its base record and either averages groups or resamples to smaller FFT input sizes (including explicit 4096/8192-style branches).

`dsoFFTBuffer` is another software preprocessing helper and only accepts its second argument equal to 4. It selects one of two floating constants (`3.0625` or `3.3125`) based on its state input before entering the internal processing routine.

DSO1102_Controller already has its own clean FFT implementation, so none of these vendor DSP exports are required for hardware support.

## PageGDICalls

`PageGDICalls` is a large MFC/GDI report-rendering routine. Its embedded strings include `TEST REPORT`, `DSO-2250 USB`, `CH1`, `CH2` and `Note`. It is unrelated to device protocol and should not be used by the replacement controller.

## Legacy combined setter

`dsoSetTriggerAndSampleRate` is the older three-argument API. It constructs a 12-byte bulk command beginning:

```text
01 00 ...
```

and contains its own 38-entry Time/DIV logic. Runtime tracing of the actual vendor UI captured no calls to this export while changing the tested timebases; the application uses `dsoSetTriggerAndSampleRateNew` instead. Keep the legacy setter documented but do not build new control logic around it.

## Useful DLL-global state

```text
0x100090A0 + device*32 : 8 x DWORD relay-state cache
0x1000B084             : zero-initialized byte inserted into trigger command byte 8
0x1000B088 + device*4  : cached FPGA version
```

`InitLevelRange(device)` resets the eight relay-cache DWORDs to `0xFFFFFFFF`.

The FPGA cache is important because waveform decode behavior changes when the cached value equals 12001.

## What is safe and directly useful for DSO1102_Controller

High-value functions for the real backend are:

```text
dsoSearchDevice
dsoGetFPGAVersion
dsoGetChannelLevel
dsoGetCalData
dsoGetCaptureState
dsoCaptureStart
dsoTriggerEnabled
dsoForceTrigger
dsoSetTriggerAndSampleRateNew
dsoSetVoltageAndCoupling
dsoSetOffset
dsoSetFilt
dsoGetChannelData
```

Low-level helpers are valuable for reverse engineering/diagnostics but should normally remain behind the high-level vendor setters.

## Functions that must remain locked out

```text
dsoSetChannelLevel
dsoSetCalData
dsoSetDeviceID
_dsoSetDeviceAddress@8
_dsoWriteFlash@8
```

These write calibration, identity or persistent memory. They provide no benefit for ordinary oscilloscope acquisition and introduce unnecessary risk.

## Remaining high-value unknowns

The DLL is now largely understood. The remaining questions are narrow rather than architectural:

1. Exact physical meaning of the DSO-1102-only second 16-bit timing word in command `0x0E`.
2. Direct hardware verification of fast Time/DIV codes 0..9, especially CH1-only/CH2-only fast acquisition and possible 1,048,576-sample record.
3. Direct runtime correlation of physical acquisition rate versus primary samplerate word and the ~5-MS/s transferred stream seen in currently tested profiles.
4. Exact high-level semantic labels for configuration `word[5]`, `word[13]` and `word[22]`.
5. Full slow/roll scheduling semantics for Time/DIV codes 25..37; the samplerate command alone is insufficient because many slow codes reuse timing words.
6. Runtime validation of all nine V/div codes on the physical DSO-1102; codes 5,6,7 are already directly confirmed.

None of these unknowns blocks implementation of the already verified normal acquisition profiles.

## Recommended implementation boundary

The safest architecture remains:

```text
x64 WPF controller
    -> IPC
x86 bridge
    -> verified high-level DSO1102USB.dll exports
    -> original ODM/Hantek driver
    -> DSO-1102
```

Do not replace the original driver, do not emit guessed IOCTL/USB packets, and do not copy GPL implementation code from OpenHantek. Use this DLL analysis plus runtime traces as the primary implementation specification.
