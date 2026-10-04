param(
    [ValidateSet('new','legacy')]
    [string]$Mode = 'new',
    [int]$MaxCalls = 8,
    [int]$IdleTimeoutMs = 30000,
    [int]$TotalTimeoutMs = 180000,
    [int]$DistinctPointerArg = 3,
    [string]$ExePath = 'C:\Program Files (x86)\DSO-1102 USB\DSO-1102 USB.exe',
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$traceScript = Join-Path $PSScriptRoot 'Trace-VendorCall.ps1'

if ($Mode -eq 'new') {
    $export = 'dsoSetTriggerAndSampleRateNew'
    $argCount = 8
} else {
    $export = 'dsoSetTriggerAndSampleRate'
    $argCount = 3
}

& $traceScript `
  -Export $export `
  -ArgumentCount $argCount `
  -MaxCalls $MaxCalls `
  -IdleTimeoutMs $IdleTimeoutMs `
  -TotalTimeoutMs $TotalTimeoutMs `
  -DistinctPointerArg $DistinctPointerArg `
  -ExePath $ExePath `
  -DllPath $DllPath

exit $LASTEXITCODE
