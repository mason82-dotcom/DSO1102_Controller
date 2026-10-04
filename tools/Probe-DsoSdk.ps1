param(
    [string]$DllPath = 'C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll',
    [string]$ExePath = 'C:\Program Files (x86)\DSO-1102 USB\DSO-1102 USB.exe'
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

function Get-PeMachine([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64) { return 'Invalid/too small' }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) { return 'Invalid PE offset' }
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    switch ($machine) {
        0x014c { 'x86 (I386)' }
        0x8664 { 'x64 (AMD64)' }
        0x0200 { 'IA64' }
        0xAA64 { 'ARM64' }
        default { ('Unknown 0x{0:X4}' -f $machine) }
    }
}

function Get-PrintableStrings([string]$Path, [int]$MinimumLength = 4) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $results = New-Object System.Collections.Generic.HashSet[string]
    $ascii = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) {
        if ($b -ge 32 -and $b -le 126) { [void]$ascii.Append([char]$b) }
        else {
            if ($ascii.Length -ge $MinimumLength) { [void]$results.Add($ascii.ToString()) }
            [void]$ascii.Clear()
        }
    }
    if ($ascii.Length -ge $MinimumLength) { [void]$results.Add($ascii.ToString()) }

    for ($i = 0; $i -lt ($bytes.Length - 1); ) {
        $start = $i
        $chars = New-Object System.Text.StringBuilder
        while ($i + 1 -lt $bytes.Length) {
            $lo = $bytes[$i]
            $hi = $bytes[$i + 1]
            if ($hi -eq 0 -and $lo -ge 32 -and $lo -le 126) {
                [void]$chars.Append([char]$lo)
                $i += 2
            } else { break }
        }
        if ($chars.Length -ge $MinimumLength) { [void]$results.Add($chars.ToString()) }
        if ($i -eq $start) { $i++ }
    }
    $results
}

function Find-DumpTool {
    foreach ($name in @('dumpbin.exe', 'llvm-readobj.exe', 'llvm-objdump.exe', 'objdump.exe')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
        if ($install) {
            $candidates = Get-ChildItem -Path (Join-Path $install 'VC\Tools\MSVC') -Filter dumpbin.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Descending
            if ($candidates) { return $candidates[0].FullName }
        }
    }
    return $null
}

foreach ($path in @($DllPath, $ExePath)) {
    if (-not (Test-Path $path)) { Write-Warning "File not found: $path"; continue }
    Write-Section "File: $path"
    $item = Get-Item $path
    $hash = Get-FileHash $path -Algorithm SHA256
    $sig = Get-AuthenticodeSignature $path
    $version = $item.VersionInfo
    [pscustomobject]@{
        FullName = $item.FullName
        Length = $item.Length
        SHA256 = $hash.Hash
        PEArchitecture = Get-PeMachine $path
        FileVersion = $version.FileVersion
        ProductVersion = $version.ProductVersion
        FileDescription = $version.FileDescription
        ProductName = $version.ProductName
        CompanyName = $version.CompanyName
        OriginalFilename = $version.OriginalFilename
        SignatureStatus = $sig.Status
        Signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { $null }
    } | Format-List
}

$tool = Find-DumpTool
Write-Section 'PE export/import tool'
if (-not $tool) {
    Write-Host 'No dumpbin/llvm-objdump/objdump tool found.'
    Write-Host 'The string analysis below will still run.'
} else {
    Write-Host "Tool: $tool"
    if ($tool -match '(?i)dumpbin\.exe$') {
        Write-Section 'DLL exports'
        & $tool /nologo /exports $DllPath
        Write-Section 'DLL imports'
        & $tool /nologo /imports $DllPath
        Write-Section 'Application imports'
        & $tool /nologo /imports $ExePath
    } elseif ($tool -match '(?i)llvm-readobj\.exe$') {
        Write-Section 'DLL exports/imports'
        & $tool --coff-exports --coff-imports $DllPath
        Write-Section 'Application imports'
        & $tool --coff-imports $ExePath
    } else {
        Write-Section 'DLL dynamic symbols'
        & $tool -p $DllPath
        Write-Section 'Application dynamic symbols'
        & $tool -p $ExePath
    }
}

Write-Section 'Interesting DLL strings'
Get-PrintableStrings $DllPath |
    Where-Object { $_ -match '(?i)CreateFile|DeviceIoControl|ReadFile|WriteFile|SetupDi|GUID|VID_|PID_|USB|DSO|Hantek|Open|Close|Init|Start|Stop|Trigger|Channel|Sample|Buffer|Voltage|Gain|Offset|Firmware' } |
    Sort-Object -Unique

Write-Section 'Interesting application strings'
Get-PrintableStrings $ExePath |
    Where-Object { $_ -match '(?i)DSO1102USB|CreateFile|DeviceIoControl|ReadFile|WriteFile|SetupDi|GUID|VID_|PID_|USB|Hantek|Open|Close|Init|Start|Stop|Trigger|Channel|Sample|Buffer|Voltage|Gain|Offset|Firmware' } |
    Sort-Object -Unique
