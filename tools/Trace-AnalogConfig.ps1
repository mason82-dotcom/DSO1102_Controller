param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('voltage','offset')]
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
