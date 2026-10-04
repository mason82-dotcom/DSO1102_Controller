param(
    [string]$ServiceName = 'DSO11022',
    [string]$PublishedInf = 'oem19.inf'
)

$ErrorActionPreference = 'Stop'

Write-Host 'DSO1102 driver/API probe' -ForegroundColor Cyan
Write-Host ''

Write-Host '=== PnP driver package ===' -ForegroundColor Cyan
try {
    pnputil.exe /enum-drivers /files |
        Select-String -Pattern $PublishedInf -Context 0,30 |
        ForEach-Object { $_.Context.PreContext + $_.Line + $_.Context.PostContext }
}
catch {
    Write-Warning "pnputil /enum-drivers /files failed: $($_.Exception.Message)"
}

Write-Host ''
Write-Host '=== Published INF ===' -ForegroundColor Cyan
$publishedInfPath = Join-Path $env:windir "INF\$PublishedInf"

if (Test-Path $publishedInfPath) {
    Write-Host "Path: $publishedInfPath"
    Get-Content $publishedInfPath |
        Select-String -Pattern 'Provider|DriverVer|CatalogFile|ServiceBinary|AddService|CopyFiles|SourceDisksFiles|Class|ClassGuid|DLL|SYS' |
        ForEach-Object { $_.Line }
}
else {
    Write-Warning "Published INF not found at $publishedInfPath"
}

Write-Host ''
Write-Host '=== Kernel/service registration ===' -ForegroundColor Cyan
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"

if (Test-Path $serviceKey) {
    $service = Get-ItemProperty $serviceKey
    $service | Select-Object DisplayName, ImagePath, Type, Start, ErrorControl | Format-List

    $imagePath = [Environment]::ExpandEnvironmentVariables([string]$service.ImagePath)
    $imagePath = $imagePath -replace '^\\SystemRoot', $env:windir
    $imagePath = $imagePath -replace '^System32', (Join-Path $env:windir 'System32')
    $imagePath = $imagePath.Trim('"')

    if ($imagePath -and (Test-Path $imagePath)) {
        Write-Host "Driver binary: $imagePath"
        Get-Item $imagePath | Select-Object FullName, Length, CreationTimeUtc, LastWriteTimeUtc, VersionInfo | Format-List
        Get-AuthenticodeSignature $imagePath |
            Select-Object Status, StatusMessage, SignerCertificate |
            Format-List
    }
    elseif ($imagePath) {
        Write-Warning "Resolved driver binary was not found: $imagePath"
    }
}
else {
    Write-Warning "Service registry key not found: $serviceKey"
}

Write-Host ''
Write-Host '=== System driver ===' -ForegroundColor Cyan
Get-CimInstance Win32_SystemDriver -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue |
    Select-Object Name, DisplayName, State, Status, StartMode, PathName |
    Format-List

Write-Host ''
Write-Host '=== Candidate user-mode SDK/API files ===' -ForegroundColor Cyan
$roots = @(
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)},
    $env:ProgramData
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$patterns = @('*DSO*.dll', '*Hantek*.dll', '*Voltcraft*.dll', '*scope*.dll')

foreach ($root in $roots) {
    foreach ($pattern in $patterns) {
        Get-ChildItem -Path $root -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'DSO|Hantek|Voltcraft|Oscilloscope' } |
            Select-Object FullName, Length, LastWriteTime |
            Format-Table -AutoSize
    }
}
