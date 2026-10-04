param(
    [string]$Export = 'dsoGetChannelData',
    [int]$ArgumentCount = 8,
    [int]$MaxCalls = 1,
    [int]$IdleTimeoutMs = 30000,
    [int]$TotalTimeoutMs = 120000,
    [int]$DistinctPointerArg = 0,
    [string]$ExePath = 'C:\Program Files (x86)\DSO-1102 USB\DSO-1102 USB.exe',
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DSO1102.Controller.CallTrace\DSO1102.Controller.CallTrace.csproj'

dotnet run --project $project -c Release -r win-x86 -- `
  --exe $ExePath `
  --dll $DllPath `
  --export $Export `
  --args $ArgumentCount `
  --max-calls $MaxCalls `
  --idle-timeout-ms $IdleTimeoutMs `
  --total-timeout-ms $TotalTimeoutMs `
  --distinct-pointer-arg $DistinctPointerArg

exit $LASTEXITCODE
