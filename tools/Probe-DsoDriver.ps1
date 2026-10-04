param(
    [string]$ServiceName = 'DSO11022',
    [string]$PublishedInf = 'oem19.inf'
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

Write-Host 'DSO1102 driver/API probe' -ForegroundColor Cyan

Write-Section 'PnP driver package'
try {
    pnputil.exe /enum-drivers /files |
        Select-String -Pattern $PublishedInf -Context 0,30 |
        ForEach-Object { $_.Context.PreContext + $_.Line + $_.Context.PostContext }
}
catch {
    Write-Warning "pnputil /enum-drivers /files failed: $($_.Exception.Message)"
}

Write-Section 'Published INF'
$publishedInfPath = Join-Path $env:windir "INF\$PublishedInf"

if (Test-Path $publishedInfPath) {
    Write-Host "Path: $publishedInfPath"
    Get-Content $publishedInfPath |
        Select-String -Pattern 'Provider|DriverVer|CatalogFile|ServiceBinary|AddService|AddReg|CopyFiles|SourceDisksFiles|Class|ClassGuid|DeviceInterface|GUID|DLL|SYS' |
        ForEach-Object { $_.Line }
}
else {
    Write-Warning "Published INF not found at $publishedInfPath"
}

Write-Section 'Kernel/service registration'
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
        Get-Item $imagePath |
            Select-Object FullName, Length, CreationTimeUtc, LastWriteTimeUtc, VersionInfo |
            Format-List

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

Write-Section 'System driver'
Get-CimInstance Win32_SystemDriver -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue |
    Select-Object Name, DisplayName, State, Status, StartMode, PathName |
    Format-List

Write-Section 'DSO1102 device registry'
$enumRoot = 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB\VID_04B5&PID_1102'
if (Test-Path $enumRoot) {
    Get-ChildItem $enumRoot -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Instance: $($_.PSChildName)"
        Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue |
            Select-Object FriendlyName, DeviceDesc, Mfg, Class, ClassGUID, Service, Driver |
            Format-List

        $parameters = Join-Path $_.PSPath 'Device Parameters'
        if (Test-Path $parameters) {
            Get-ItemProperty $parameters -ErrorAction SilentlyContinue | Format-List
        }
    }
}

Write-Section 'Installed DSO/Hantek/Voltcraft software'
$uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$installed = Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
    Where-Object {
        $_.DisplayName -match '(?i)Hantek|Voltcraft|DSO.?1102|DSO.?2202|Oscilloscope' -or
        $_.Publisher -match '(?i)Hantek|Voltcraft'
    } |
    Select-Object DisplayName, DisplayVersion, Publisher, InstallLocation, InstallSource, UninstallString

if ($installed) {
    $installed | Format-List
}
else {
    Write-Host 'No matching uninstall entry found.'
}

Write-Section 'Candidate installation directories'
$candidateRoots = @(
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)},
    $env:ProgramData,
    $env:LOCALAPPDATA,
    $env:APPDATA,
    'C:\Hantek',
    'C:\DSO',
    'C:\Voltcraft'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$interestingNames = '(?i)Hantek|Voltcraft|DSO.?1102|DSO.?2202|Oscilloscope'

foreach ($root in $candidateRoots) {
    Get-ChildItem -Path $root -Directory -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\Microsoft\\Windows\\Containers\\' -and
            ($_.Name -match $interestingNames -or $_.FullName -match $interestingNames)
        } |
        Select-Object FullName |
        Format-Table -AutoSize
}

Write-Section 'Candidate SDK/API files'
$extensions = @('.dll', '.exe', '.lib', '.h', '.hpp', '.c', '.cpp', '.bas', '.vi', '.lvlib', '.tlb')
$seen = @{}

foreach ($root in $candidateRoots) {
    Get-ChildItem -Path $root -File -Recurse -Depth 5 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\Microsoft\\Windows\\Containers\\' -and
            $extensions -contains $_.Extension.ToLowerInvariant() -and
            (
                $_.Name -match $interestingNames -or
                $_.DirectoryName -match $interestingNames
            )
        } |
        ForEach-Object {
            if (-not $seen.ContainsKey($_.FullName)) {
                $seen[$_.FullName] = $true
                [pscustomobject]@{
                    FullName = $_.FullName
                    Length = $_.Length
                    LastWriteTime = $_.LastWriteTime
                }
            }
        }
}

Write-Section 'Mounted CD/DVD SDK candidates'
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=5' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $drive = $_.DeviceID + '\'
        if (Test-Path $drive) {
            Write-Host "Scanning $drive"
            Get-ChildItem $drive -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Name -match '(?i)SDK|Second|LabVIEW|Visual.?Basic|VB|VC|Builder|DSO1102|DSO-1102'
                } |
                Select-Object FullName, Length, LastWriteTime |
                Format-Table -AutoSize
        }
    }
