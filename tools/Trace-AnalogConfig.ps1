param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('voltage','offset','channel','trigger')]
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
Expected DSO-2250-family scalar sequence for -Mode channel:
  CH1 only       -> arg2 = 0
  both channels  -> arg2 = 2
  CH2 only       -> arg2 = 3
  neither        -> arg2 = 1

DSO-1102 has already directly shown 0 -> 2. Value 3 remains to be verified.
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
