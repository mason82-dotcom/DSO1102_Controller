param(
    [ValidateSet('probe','info','exports')]
    [string]$Command = 'probe',
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DSO1102.Controller.Bridge\DSO1102.Controller.Bridge.csproj'

if (-not (Test-Path $project)) {
    throw "Bridge project not found: $project"
}

if (-not (Test-Path $DllPath)) {
    throw "Vendor DLL not found: $DllPath"
}

dotnet run --project $project -c Release -r win-x86 -- $Command --dll $DllPath

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
