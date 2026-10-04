param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('voltage','offset','channel','trigger','enable')]
    [string]$Mode,

    [int]$MaxCalls = 16,
    [int]$IdleTimeoutMs = 30000,
    [int]$TotalTimeoutMs = 120000,
    [string]$ExePath = 'C:\Program Files (x86)\DSO-1102 USB\DSO-1102 USB.exe',
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll'
)

$ErrorActionPreference = 'Stop'

$traceScript = Join-Path $PSScriptRoot 'Trace-VendorCall.ps1'

switch ($Mode) {
    'voltage' {
        $export = 'dsoSetVoltageAndCoupling'
        $argumentCount = 6
        $distinctPointerArg = 0
        $distinctScalarArg = 0
        $distinctLow16Signature = 0
    }
    'offset' {
        $export = 'dsoSetOffset'
        $argumentCount = 6
        $distinctPointerArg = 2
        $distinctScalarArg = 0
        $distinctLow16Signature = 0
    }
    'channel' {
        $export = '_dsoSetChIn@8'
        $argumentCount = 2
        $distinctPointerArg = 0
        $distinctScalarArg = 2
        $distinctLow16Signature = 0
    }
    'trigger' {
        $export = '_dsoSetTrigIn@32'
        $argumentCount = 8
        $distinctPointerArg = 0
        $distinctScalarArg = 0
        $distinctLow16Signature = 1
    }
    'enable' {
        $export = 'dsoSetFiltAndVoltageData'
        $argumentCount = 5
        $distinctPointerArg = 0
        $distinctScalarArg = 0
        $distinctLow16Signature = 1
    }
    default {
        throw "Unsupported mode: $Mode"
    }
}

& $traceScript `
  -Export $export `
  -ArgumentCount $argumentCount `
  -MaxCalls $MaxCalls `
  -IdleTimeoutMs $IdleTimeoutMs `
  -TotalTimeoutMs $TotalTimeoutMs `
  -DistinctPointerArg $distinctPointerArg `
  -DistinctScalarArg $distinctScalarArg `
  -DistinctLow16Signature $distinctLow16Signature `
  -ExePath $ExePath `
  -DllPath $DllPath

exit $LASTEXITCODE


<#
Recommended CH2 verification sequence for -Mode voltage:
  CH2 DC, 1 V/div
  -> CH2 DC, 500 mV/div
  -> CH2 DC, 2 V/div
  -> CH2 AC, 2 V/div
  -> CH2 DC, 2 V/div
  -> CH2 DC, 1 V/div

Keep CH1, timebase, trigger and probe settings unchanged.
Expected mapping hypothesis to verify:
  arg3 = CH2 vertical range code
  arg5 = CH2 coupling
Do not promote the hypothesis until runtime trace confirmation.
#>


<#
-Mode channel traces the internal helper _dsoSetChIn@8 for reverse-engineering
only. Runtime tracing proved that its observed calls return inside
dsoSetTriggerAndSampleRateNew (DLL RVA 0x53FD), so it must NOT be treated as a
verified CH1/CH2 UI enable/disable setter.

Use -Mode offset for the next controlled analog-state trace.
#>

<#
Recommended offset verification sequence for -Mode offset:
  Keep CH1 enabled, DC coupled, 1 V/div.
  Keep CH2 unchanged.
  Start with the CH1 vertical position centered.
  -> move CH1 position upward by exactly 1 major division
  -> return CH1 to center
  -> move CH1 position downward by exactly 1 major division
  -> return CH1 to center

Keep V/div, coupling, timebase, trigger, probe and CH2 settings unchanged.
The tracer is read-only; dsoSetOffset is only observed, never invoked.
#>


<#
Important channel-helper behavior:
  dsoSetTriggerAndSampleRateNew forces _dsoSetChIn arg2=2 whenever the
  Time/DIV code is >= 10 (10 us/div and slower).

  Only Time/DIV codes 0..9 (4 ns/div .. 4 us/div) pass the application's
  hardware-channel mode through to _dsoSetChIn.

Therefore do NOT use a 400 us / 1 ms / 2 ms / 4 ms trace to infer UI channel
enable state. A future fast-timebase trace can test the DSO-2250-family
hypothesis:
  0 = CH1 hardware path
  1 = none
  2 = both
  3 = CH2 hardware path
#>


<#
Recommended trigger-source verification sequence for -Mode trigger:
  Keep CH1 and CH2 enabled.
  Keep V/div, coupling, vertical positions, timebase and memory depth unchanged.
  Start with trigger source CH1 and rising edge.
  -> trigger source CH2
  -> trigger source EXT
  -> trigger source CH1

Do not change trigger level or slope during this first run.

The tracer observes _dsoSetTrigIn@32 only. It never invokes the helper.
Calls are deduplicated by the complete low-16 argument signature so recurring
internal refresh calls do not consume the trace budget.
#>


<#
Recommended UI channel-enable verification sequence for -Mode enable:
  Use a normal/slow profile such as 1 ms/div.
  Keep V/div, coupling, vertical position, trigger and timebase unchanged.
  Start with CH1 enabled and CH2 disabled.
  -> enable CH2
  -> disable CH2
  -> enable CH2
  -> disable CH1
  -> enable CH1

This traces dsoSetFiltAndVoltageData, not the fast-sampling helper
_dsoSetChIn@8. The five low-16 arguments are deduplicated as a complete
signature. Static analysis shows args 4 and 5 are CH1/CH2 V/div range codes;
args 2 and 3 are the two per-channel 0/1 state fields that the vendor UI
toggles. Runtime tracing is used to prove polarity/channel ordering.
#>
