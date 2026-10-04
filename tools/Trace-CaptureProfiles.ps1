param(
    [int]$MaxCalls = 8,
    [int]$IdleTimeoutMs = 30000,
    [int]$TotalTimeoutMs = 120000,
    [string]$ExePath = 'C:\Program Files (x86)\DSO-1102 USB\DSO-1102 USB.exe',
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll'
)

$ErrorActionPreference = 'Stop'
$traceScript = Join-Path $PSScriptRoot 'Trace-VendorCall.ps1'

& $traceScript `
  -Export 'dsoGetChannelData' `
  -ArgumentCount 8 `
  -MaxCalls $MaxCalls `
  -IdleTimeoutMs $IdleTimeoutMs `
  -TotalTimeoutMs $TotalTimeoutMs `
  -DistinctPointerArg 4 `
  -ExePath $ExePath `
  -DllPath $DllPath

exit $LASTEXITCODE
