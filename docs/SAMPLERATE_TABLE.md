# DSO-1102 samplerate programming table

This table is reconstructed directly from the real `DSO1102USB.dll` export `_dsoSetSampleRate@8`.
It documents the 38 Time/DIV switch entries and both branches selected by `config.word[4] == 0` versus non-zero.

The 16-bit primary values strongly match the historical Hantek DSO-2250 encoding `samplerateWord = 0x10001 - divider` when downsampling is active. The second 16-bit field is specific to this DSO-1102/vendor-DLL command revision; OpenHantek's public 8-byte DSO-2250 `0x0E` packet has no corresponding second word.

Do not treat the mechanically decoded divider as a measured physical ADC clock. The CAL-referenced effective rate in the transferred stream and the ADC core/update rate are separate evidence domains.

## Command-byte-2 packing

The DLL constructs command `0x0E` byte 2 as:

```text
bit 0 = table-controlled state; OpenHantek DSO-2250 correlates this bit with fast-rate
bit 1 = table-controlled state; OpenHantek DSO-2250 correlates this bit with downsampling
bit 2 = DSO-1102 table-controlled extra bit
bit 3 = config.word[6] bit 0
```

For the normal traced profiles `config.word[6]` is odd, so bit 3 is set.

## Deep-record branch (`config.word[4] != 0`)

| Code | Time/DIV | Byte2 low bits 0..2 | Primary word | Primary encoded divider | Secondary word | Secondary mechanical value | Runtime status |
|---:|---|---:|---:|---:|---:|---:|---|
| 0 | 4 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 1 | 10 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 2 | 20 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 3 | 40 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 4 | 100 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 5 | 200 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 6 | 400 ns/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 7 | 1 us/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 8 | 2 us/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 9 | 4 us/div | `0x05` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 10 | 10 us/div | `0x07` | `0x0000` | — | `0x0000` | — | static DLL decode |
| 11 | 20 us/div | `0x06` | `0xFFFF` | 2 | `0x0001` | 65,536 | static DLL decode |
| 12 | 40 us/div | `0x07` | `0xFFFF` | 2 | `0xFFFF` | 2 | static DLL decode |
| 13 | 100 us/div | `0x02` | `0xFFFD` | 4 | `0xFFFF` | 2 | static DLL decode |
| 14 | 200 us/div | `0x06` | `0xFFF7` | 10 | `0xFFFC` | 5 | static DLL decode |
| 15 | 400 us/div | `0x02` | `0xFFED` | 20 | `0xFFF7` | 10 | DSO-1102 waveform verified |
| 16 | 1 ms/div | `0x02` | `0xFFD9` | 40 | `0xFFED` | 20 | DSO-1102 waveform verified |
| 17 | 2 ms/div | `0x02` | `0xFF9D` | 100 | `0xFFCF` | 50 | DSO-1102 waveform verified |
| 18 | 4 ms/div | `0x02` | `0xFF39` | 200 | `0xFF9D` | 100 | DSO-1102 waveform verified |
| 19 | 10 ms/div | `0x02` | `0xFE71` | 400 | `0xFF39` | 200 | static DLL decode |
| 20 | 20 ms/div | `0x02` | `0xFC1A` | 999 | `0xFE0D` | 500 | static DLL decode |
| 21 | 40 ms/div | `0x02` | `0xF83E` | 1,987 | `0xFC1A` | 999 | static DLL decode |
| 22 | 100 ms/div | `0x02` | `0xF06E` | 3,987 | `0xF83E` | 1,987 | static DLL decode |
| 23 | 200 ms/div | `0x02` | `0xD8CC` | 10,037 | `0xD8CC` | 10,037 | static DLL decode |
| 24 | 400 ms/div | `0x02` | `0xB1BC` | 20,037 | `0xD8CC` | 10,037 | static DLL decode |
| 25 | 1 s/div | `0x02` | `0xAFC8` | 20,537 | `0xD8CC` | 10,037 | static DLL decode |
| 26 | 2 s/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 27 | 4 s/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 28 | 10 s/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 29 | 20 s/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 30 | 40 s/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 31 | 1 min/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 32 | 2 min/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 33 | 4 min/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 34 | 10 min/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 35 | 20 min/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 36 | 40 min/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |
| 37 | 1 h/div | `0x02` | `0xFFED` | 20 | `0xD8CC` | 10,037 | static DLL decode |

With the currently observed `config.word[6].bit0 = 1`, add `0x08` to the low-bit value to obtain the actual command byte 2.

## Small/zero-record-state branch (`config.word[4] == 0`)

Only codes 12..24 differ from the deep-record branch. The remaining codes use the same programming.

| Code | Time/DIV | Byte2 low bits 0..2 | Primary word | Primary encoded divider | Secondary word | Secondary mechanical value |
|---:|---|---:|---:|---:|---:|---:|
| 12 | 40 us/div | `0x02` | `0xFFFD` | 4 | `0xFFFF` | 2 |
| 13 | 100 us/div | `0x02` | `0xFFF7` | 10 | `0xFFFC` | 5 |
| 14 | 200 us/div | `0x06` | `0xFFED` | 20 | `0xFFF7` | 10 |
| 15 | 400 us/div | `0x02` | `0xFFD9` | 40 | `0xFFED` | 20 |
| 16 | 1 ms/div | `0x02` | `0xFF9D` | 100 | `0xFFCF` | 50 |
| 17 | 2 ms/div | `0x02` | `0xFF39` | 200 | `0xFF9D` | 100 |
| 18 | 4 ms/div | `0x02` | `0xFE71` | 400 | `0xFF39` | 200 |
| 19 | 10 ms/div | `0x02` | `0xFC19` | 1,000 | `0xFE0D` | 500 |
| 20 | 20 ms/div | `0x02` | `0xF83E` | 1,987 | `0xFC19` | 1,000 |
| 21 | 40 ms/div | `0x02` | `0xF06E` | 3,987 | `0xF83E` | 1,987 |
| 22 | 100 ms/div | `0x02` | `0xD8CC` | 10,037 | `0xEC7A` | 4,999 |
| 23 | 200 ms/div | `0x02` | `0xAFC8` | 20,537 | `0xD8CC` | 10,037 |
| 24 | 400 ms/div | `0x02` | `0x639C` | 40,037 | `0xD8CC` | 10,037 |

## OpenHantek DSO-2250 correlation

OpenHantek's DSO-2250 implementation independently defines:

- command `0x0E` for samplerate programming;
- byte-2 bit 0 as fast-rate;
- byte-2 bit 1 as downsampling;
- one 16-bit samplerate word encoded as `0x10001 - divider`;
- normal-mode base clock `100 MHz`;
- fast/interleaved base `200 MHz`, with a maximum of `250 MHz`.

For the DSO-1102 deep-record branch, applying the DSO-2250 100-MHz normal-mode model to the **primary** word is especially coherent for codes 13..24:

| Code | Time/DIV | Primary divider | 100 MHz / divider candidate | Samples per division candidate |
|---:|---|---:|---:|---:|
| 13 | 100 us/div | 4 | 25,000,000.000 S/s | 2,500.0 |
| 14 | 200 us/div | 10 | 10,000,000.000 S/s | 2,000.0 |
| 15 | 400 us/div | 20 | 5,000,000.000 S/s | 2,000.0 |
| 16 | 1 ms/div | 40 | 2,500,000.000 S/s | 2,500.0 |
| 17 | 2 ms/div | 100 | 1,000,000.000 S/s | 2,000.0 |
| 18 | 4 ms/div | 200 | 500,000.000 S/s | 2,000.0 |
| 19 | 10 ms/div | 400 | 250,000.000 S/s | 2,500.0 |
| 20 | 20 ms/div | 999 | 100,100.100 S/s | 2,002.0 |
| 21 | 40 ms/div | 1,987 | 50,327.126 S/s | 2,013.1 |
| 22 | 100 ms/div | 3,987 | 25,081.515 S/s | 2,508.2 |
| 23 | 200 ms/div | 10,037 | 9,963.136 S/s | 1,992.6 |
| 24 | 400 ms/div | 20,037 | 4,990.767 S/s | 1,996.3 |

This pattern is physically plausible (roughly 2,000–2,500 samples/div over much of the range), but it remains **family-correlated**, not a direct DSO-1102 sample-clock measurement.

For codes 15..18 the transferred/deinterleaved stream has independently measured about 5 MSamples/s from the nominal 1-kHz CAL waveform. Because `dsoGetChannelData` does not interpolate in software, a difference between this stream rate and the primary-word candidate would have to arise before the DLL decode stage (for example FPGA/RAM sample holding, replication or another device-side timing layer).

## Slow range

From code 25 onward the DLL enters additional slow-timebase handling after sending the `0x0E` command. Codes 26..37 reuse the same primary/secondary words despite widely different Time/DIV values, so the simple downsampler interpretation is insufficient there. Those profiles require analysis of the slow/roll scheduling path before assigning a physical rate.

## Safety / implementation rule

The table is diagnostic evidence, not permission to send untested profiles. `DSO1102_Controller` should continue to expose only Time/DIV settings whose complete initialization/capture behavior has been validated on the real DSO-1102, unless a dedicated guarded test mode is used.
