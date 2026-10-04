param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('voltage','offset','channel')]
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
    }
    'offset' {
        $export = 'dsoSetOffset'
        $argumentCount = 6
    }
    'channel' {
        $export = '_dsoSetChIn@8'
        $argumentCount = 2
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
  -DistinctPointerArg 0 `
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
Recommended channel-enable verification sequence for -Mode channel:
  Start with CH1 enabled and CH2 disabled.
  -> enable CH2
  -> disable CH2
  -> enable CH2
  -> disable CH1
  -> enable CH1

Keep V/div, coupling, timebase, trigger and probe settings unchanged.

This trace is read-only. The candidate export _dsoSetChIn@8 is selected because
its name strongly suggests channel-input control, but semantics must be proven
from the runtime trace before the bridge invokes it.
#>
