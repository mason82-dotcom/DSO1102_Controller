# OpenHantek reference for DSO-1102 reverse engineering

This document records protocol and architecture facts observed in the current
OpenHantek DSO-2250 source tree and compares them with independently recovered
DSO-1102 behavior.

OpenHantek is used as an external semantic reference only. No OpenHantek source
code is copied into DSO1102_Controller.

## Licensing note

The OpenHantek repository includes a GPL-3.0 COPYING file, while relevant source
files carry SPDX headers such as GPL-2.0+. Treat the code as copyleft material.
Use protocol facts, independently verified behavior, and clean-room
reimplementations rather than copying implementation code.

Reference tree:
https://github.com/OpenHantek/openhantek/tree/master/openhantek/src

## Highest-value files

### hantekdso/models/modelDSO2250.cpp

Directly useful model constants:

- DSO-2250 uses separate commands for channels, trigger, record length,
  samplerate and pretrigger/buffer state.
- Normal-mode limits:
  - base 100 MS/s
  - max 100 MS/s
  - record lengths: roll, 10,240, 524,288
- Fast one-channel limits:
  - base 200 MS/s
  - max 250 MS/s
  - record lengths: roll, 20,480, 1,048,576
- 8-bit sample size.
- Nine gain/range steps.
- EXT is represented as a special trigger source.

This strongly matches the DSO1102USB.dll lineage and the DSO-1102 runtime
observation of 524,288 decoded samples/channel.

Source:
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekdso/models/modelDSO2250.cpp

## definitions.h

### UsedChannels

Generic family values:

- 0 = CH1
- 1 = CH2
- 2 = CH1+CH2
- 3 = none

DSO-2250 aliases remap the meanings to:

- 0 = CH1
- 1 = none
- 2 = CH1+CH2
- 3 = CH2

This is the relevant mapping for the DSO-2250-specific BSETCHANNELS command.

### FilterBits

The family SETFILTER command packs three independent one-bit states:

- CH1 filter
- CH2 filter
- trigger filter

This strongly supports the DSO-1102 static reconstruction of dsoSetFilt as
CH1 bandwidth/filter, CH2 bandwidth/filter and trigger HF rejection.

### GainBits

CH1 and CH2 each use a 2-bit 1/2/5 gain subcode.

This matches the DSO-1102 range decomposition:

- range IDs 0,3,6 -> 1x family subcode
- range IDs 1,4,7 -> 2x family subcode
- range IDs 2,5,8 -> 5x family subcode

### CTriggerBits

The DSO-2250 trigger command contains:

- 2-bit trigger source
- 1-bit trigger slope

This is directly useful when interpreting the DSO-1102 _dsoSetTrigIn@32 packet.

### ESamplerateBits

The DSO-2250 samplerate command defines:

- bit 0 = fast rate
- bit 1 = downsampling enabled

The DSO-1102 command is an extended 10-byte variant, but its low flag bits and
16-bit downsampler field show the same structure.

Source:
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekprotocol/definitions.h

## bulkStructs.cpp / bulkcode.h

### Command family

High-value DSO-2250 bulk command IDs:

- 0x00 SETFILTER
- 0x02 FORCETRIGGER
- 0x03 STARTSAMPLING
- 0x04 ENABLETRIGGER
- 0x05 GETDATA
- 0x06 GETCAPTURESTATE
- 0x07 SETGAIN
- 0x0B BSETCHANNELS
- 0x0C CSETTRIGGERORSAMPLERATE
- 0x0D DSETBUFFER
- 0x0E ESETTRIGGERORSAMPLERATE
- 0x0F FSETBUFFER

These line up closely with the DSO-1102 vendor DLL's lower-level helpers.

### BSETCHANNELS

The DSO-2250 command writes the channel-mode value directly into command byte 2.

Useful DSO-1102 correlation:
_dsoSetChIn@8 is the corresponding low-level helper, but DSO1102USB.dll only
passes UI-dependent channel mode directly at fast Time/DIV codes 0..9. At
slower codes it forces mode 2.

### DSETBUFFER

The record-length ID is written directly to command byte 2.

Useful DSO-1102 correlation:
_dsoSetRamLength@8 distinguishes hardware RAM mode 1 versus 2. The higher-level
vendor state values 5 and 6 observed in DSO1102 collapse to the same non-zero
deep-memory class.

### ESETTRIGGERORSAMPLERATE

For the DSO-2250:

- bit 0 = fast-rate mode
- bit 1 = downsampling mode
- bytes 4..5 = 16-bit samplerate/downsampler word

OpenHantek calculates the downsampler word as:

    samplerateWord = 0x10001 - downsampler

when downsampling is enabled.

This formula exactly matches the primary 16-bit timing words independently
recovered from DSO1102USB.dll _dsoSetSampleRate@8.

For the verified DSO-1102 profiles:

- 400 us/div: 0xFFED -> divider 20
- 1 ms/div:   0xFFD9 -> divider 40
- 2 ms/div:   0xFF9D -> divider 100
- 4 ms/div:   0xFF39 -> divider 200

The DSO-1102 command additionally contains a second timing word and extra flag
bits; do not assume the older 8-byte DSO-2250 packet is byte-for-byte identical.

### FSETBUFFER

The DSO-2250 uses separate 24-bit pre- and post-trigger positions.

OpenHantek computes them around 0x7FFFF and record length. This strongly
corroborates the DSO-1102 _dsoSetTriggerLength@16 reconstruction in which
config.word[3] is trigger position percent.

Sources:
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekprotocol/bulkStructs.cpp
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekprotocol/bulkcode.h

## hantekdsocontrol.cpp

This file is the most valuable behavioral reference because it shows how the
individual protocol commands are combined.

### setChannelUsed

The DSO-2250-specific CH2-only code is explicitly selected rather than the
generic family CH2 code.

Current DSO-1102 status:
- mode 2 is directly observed in the slow-timebase path;
- the DSO1102 DLL proves slow Time/DIV codes force mode 2;
- CH1-only/CH2-only hardware modes should only be tested in the fast
  Time/DIV range where the DLL passes the channel-mode state through.

### Fast-rate selection

OpenHantek enables the high-rate/interleaved mode only when no more than one
channel is active and the requested samplerate exceeds normal-mode capability.

For DSO-2250:
- normal mode uses the 100 MS/s limit set;
- fast one-channel mode uses the 200/250 MS/s limit set.

This resolves the naming ambiguity in ControlSpecification: the "multi" limit
set is the fast one-channel/interleaved hardware mode.

### Sample counts

OpenHantek expects:
- normal mode: record length per channel multiplied by the channel count;
- fast one-channel mode: one record of the selected fast-mode length.

For the DSO-2250 deep setting this means the raw family path can reach
1,048,576 samples in fast one-channel mode.

This supports keeping the DSO1102 bridge's 1,048,576-element guard allocation,
while still requiring direct DSO-1102 runtime verification before declaring a
1M record mode supported.

### Samplerate

For DSO-2250 ESETTRIGGERORSAMPLERATE:
- downsampling is active for divider > 1;
- the programmed word is 0x10001 - divider;
- fast-rate is a separate flag.

This gives a strong semantic interpretation for the first timing word emitted
by DSO1102USB.dll. Exact DSO-1102 physical sample rates still require
correlation of the DSO-1102 base clock and its additional second timing field.

### Gain and relays

Gain selection uses:
- a bulk 1/2/5 gain code;
- two range-dependent relay thresholds;
- a separate coupling relay.

The DSO-1102 DLL independently shows the same three V/div bands:
- IDs 0..2
- IDs 3..5
- IDs 6..8

### Coupling

OpenHantek's hardware relay distinguishes AC from non-AC. The DSO-1102 DLL
independently behaves the same way.

This supports the DSO-1102 finding that vendor-UI GND is not a separate relay
position in this software stack; GND handling is performed in the vendor
application's data/display path.

### Offset

For the active gain range, OpenHantek:
- reads two calibration endpoints;
- interprets them as big-endian values;
- linearly interpolates the requested normalized vertical position;
- updates trigger level after changing channel offset.

This matches the DSO-1102 DLL's directly reconstructed 2 channels x 9 ranges x
2 calibration-endpoint layout.

### Trigger source

For the DSO-2250-specific trigger command:
- CH1 maps to raw source field 2;
- CH2 maps to raw source field 3;
- EXT maps to raw source field 0.

The DSO-1102 _dsoSetTrigIn@32 helper adds another high-level translation layer,
so these values are useful as expected hardware source bits, not as direct
New-setter input values.

### Trigger level

For 8-bit family models OpenHantek treats trigger-level raw range as 0x00..0xFD
and updates the offset/control command only for the currently selected trigger
channel.

This is useful for interpreting the third calibrated value generated by the
DSO-1102 dsoSetOffset path.

Source:
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekdso/hantekdsocontrol.cpp

## controlStructs.cpp / controlcode.h

High-value control requests:

- CONTROL_VALUE = 0xA2
- CONTROL_GETSPEED = 0xB2
- CONTROL_BEGINCOMMAND = 0xB3
- CONTROL_SETOFFSET = 0xB4
- CONTROL_SETRELAYS = 0xB5

These values match the Hantek-family control requests already correlated with
the DSO-1102 vendor DLL.

### Offset packet

OpenHantek's control object stores three 16-bit values in order:

- CH1 offset
- CH2 offset
- trigger level

This strongly matches the DSO-1102 dsoSetOffset helper, which calculates three
calibrated values before sending its offset command.

### Relay packet

The family relay state has independent controls for:
- two CH1 gain/range relay thresholds;
- CH1 coupling;
- two CH2 gain/range relay thresholds;
- CH2 coupling;
- external-trigger relay.

This matches the DSO-1102 dsoSetVoltageAndCouplingSecond decomposition.

Sources:
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekprotocol/controlStructs.cpp
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekprotocol/controlcode.h

## Capture state

OpenHantek explicitly defines:

- 0 = waiting
- 1 = sampling
- 2 = ready for DSO-2090/2150
- 3 = ready for DSO-2250
- 7 = ready for DSO-5200 family

The DSO-1102 hardware independently returned state 3 for a readable capture,
which is a strong DSO-2250-lineage match.

Source:
https://github.com/OpenHantek/openhantek/blob/master/openhantek/src/hantekdso/states.h

## What is directly useful for DSO1102_Controller

Safe to use as external semantic guidance, with DSO-1102 validation:

1. DSO-2250-specific channel-mode encoding.
2. DSO-2250 capture-ready state 3.
3. 10,240 / 524,288 normal deep-memory architecture.
4. Fast one-channel 20,480 / 1,048,576 architecture as a test hypothesis.
5. 9-step 1/2/5 vertical gain structure.
6. Separate gain relays and AC/DC relay.
7. 2 x 9 x 2 range-specific offset calibration structure.
8. 0x0B/0x0C/0x0D/0x0E/0x0F command-role split.
9. Downsampler word formula 0x10001 - divider.
10. Separate trigger source, slope and pretrigger position semantics.

## What must not be copied blindly

Do not directly transplant:
- libusb transport code;
- OpenHantek command builders;
- Qt control classes;
- packet structs as implementation source;
- samplerate/base-clock constants as DSO-1102 facts;
- DSO-2250 raw USB packet lengths where DSO1102USB.dll demonstrably extends
  the packet;
- GPL implementation code.

The DSO-1102 project should continue to use its own vendor-DLL disassembly,
runtime traces and clean-room C# bridge implementation as the primary source
of truth.
