param(
    [string]$VendorId = '04B5',
    [string]$ProductId = '1102'
)

$ErrorActionPreference = 'Stop'
$needle = "VID_$VendorId&PID_$ProductId"

Write-Host "DSO1102 USB/PnP probe" -ForegroundColor Cyan
Write-Host "Target: USB\$needle" -ForegroundColor Cyan
Write-Host "Do not replace the current driver yet." -ForegroundColor Yellow
Write-Host ""

$devices = Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -match [regex]::Escape($needle)
}

if (-not $devices) {
    Write-Warning "No present device matched USB\$needle."
    exit 1
}

foreach ($device in $devices) {
    $props = @{}

    foreach ($key in @(
        'DEVPKEY_Device_DriverVersion',
        'DEVPKEY_Device_DriverProvider',
        'DEVPKEY_Device_DriverInfPath',
        'DEVPKEY_Device_Service',
        'DEVPKEY_Device_ClassGuid',
        'DEVPKEY_Device_Manufacturer'
    )) {
        try {
            $props[$key] = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName $key -ErrorAction Stop).Data
        }
        catch {
            $props[$key] = $null
        }
    }

    [pscustomobject]@{
        FriendlyName   = $device.FriendlyName
        Status         = $device.Status
        Class          = $device.Class
        InstanceId     = $device.InstanceId
        Manufacturer   = $props['DEVPKEY_Device_Manufacturer']
        DriverProvider = $props['DEVPKEY_Device_DriverProvider']
        DriverVersion  = $props['DEVPKEY_Device_DriverVersion']
        DriverInf      = $props['DEVPKEY_Device_DriverInfPath']
        Service        = $props['DEVPKEY_Device_Service']
        ClassGuid      = $props['DEVPKEY_Device_ClassGuid']
    } | Format-List
}
