# DSO-1102-USB hardware bring-up

## Verified device

Windows reported the target unit as:

- Friendly name: DSO1102 USB DRIVER 2
- VID: 04B5
- PID: 1102
- Instance family: USB\VID_04B5&PID_1102
- Provider/manufacturer field: ODM

## Safety rule

Do not replace the existing driver with WinUSB/Zadig yet. The original Voltcraft/Hantek software may depend on that driver.

## Next verification data

Run tools/Probe-UsbDevice.ps1 and capture:

- DriverProvider
- DriverVersion
- DriverInf
- Service
- ClassGuid

Then inspect the installed software/driver package for a documented SDK or x64 DLL API.

## Real-backend acceptance order

1. Match VID 04B5 / PID 1102.
2. Open the verified API/device interface without changing driver binding.
3. Read harmless identity/status information.
4. Perform one single acquisition with a known test signal.
5. Verify CH1 and CH2 sample framing and voltage scaling.
6. Verify timebase/sample-rate controls.
7. Verify trigger source, slope and level.
8. Run continuous capture and check for malformed/lost buffers.

No unverified command is sent to the instrument.
