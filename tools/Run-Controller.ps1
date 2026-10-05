param(
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll',
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $repoRoot 'src\DSO1102.Controller.Bridge\DSO1102.Controller.Bridge.csproj'
$appProject = Join-Path $repoRoot 'src\DSO1102.Controller.App\DSO1102.Controller.App.csproj'

if (-not (Test-Path $DllPath)) {
    throw "Vendor DLL not found: $DllPath"
}

dotnet build $bridgeProject -c $Configuration -r win-x86
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$bridgeExe = Join-Path $repoRoot "src\DSO1102.Controller.Bridge\bin\$Configuration\net8.0-windows\win-x86\DSO1102_Bridge_x86.exe"

if (-not (Test-Path $bridgeExe)) {
    throw "Built bridge executable not found: $bridgeExe"
}

$env:DSO1102_BRIDGE_EXE = $bridgeExe
$env:DSO1102_SDK_DLL = (Resolve-Path $DllPath).Path

Write-Host "DSO1102 bridge: $env:DSO1102_BRIDGE_EXE"
Write-Host "Vendor DLL:      $env:DSO1102_SDK_DLL"
Write-Host "Starting x64 controller..."

dotnet run --project $appProject -c $Configuration
exit $LASTEXITCODE
