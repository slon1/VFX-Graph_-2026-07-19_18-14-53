#Requires -Version 5.1
param(
    [string]$Serial = "RF8N31SLV7N",
    [string]$Adb,
    [string]$Streamline
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

$rows = @()
$failed = $false

function Add-Check([string]$Name, [string]$Value, [bool]$Ok) {
    $script:rows += [pscustomobject]@{ Name = $Name; Value = $Value; Ok = $Ok }
    if (-not $Ok) { $script:failed = $true }
}

try {
    $adbPath = Resolve-M3dAdb -Adb $Adb
    $ver = Invoke-M3d -File $adbPath -ArgumentList @("version")
    $verLine = ($ver.Text -split "`r?`n" | Where-Object { $_ -match "Version|Android Debug Bridge" } | Select-Object -First 2) -join " "
    $mismatch = $ver.Text -match "doesn't match"
    Add-Check "adb" "$adbPath $verLine" (-not $mismatch)
    if ($mismatch) { Add-Check "adb-server" "adb server version doesn't match" $false }

    $streamPath = Resolve-M3dStreamline -Streamline $Streamline
    $sver = Invoke-M3d -File $streamPath -ArgumentList @("--help")
    $sline = ($sver.Text -split "`r?`n" | Where-Object { $_ -match "Streamline" } | Select-Object -First 1)
    if (-not $sline) { $sline = ($sver.Text -split "`r?`n" | Select-Object -First 1) }
    Add-Check "streamline" "$streamPath $sline" $true

    $devices = Invoke-M3d -File $adbPath -ArgumentList @("devices", "-l")
    if ($devices.Text -match "doesn't match") {
        Add-Check "adb-server" "adb server version doesn't match" $false
    }
    $deviceLine = $devices.Text -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($Serial) } | Select-Object -First 1
    $authorized = $deviceLine -and ($deviceLine -match "\sdevice(\s|$)") -and ($deviceLine -notmatch "unauthorized|offline")
    Add-Check "device" $(if ($deviceLine) { $deviceLine.Trim() } else { "нет $Serial" }) ([bool]$authorized)

    $pkg = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "pm", "path", "com.slonInc.vfx")
    $installed = $pkg.Text -match "package:"
    Add-Check "package" $(if ($installed) { ($pkg.Text.Trim()) } else { "com.slonInc.vfx не установлен" }) $installed

    $dump = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "dumpsys", "package", "com.slonInc.vfx")
    $updated = $dump.Text -split "`r?`n" | Where-Object { $_ -match "lastUpdateTime" } | Select-Object -First 1
    Add-Check "installed" $(if ($updated) { $updated.Trim() } else { "нет lastUpdateTime" }) ([bool]$updated)

    $powerMode = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "settings", "get", "global", "low_power")
    $low = ($powerMode.Text -replace "\s", "")
    Add-Check "low_power" $low ($low -eq "0")

    $power = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "dumpsys", "power")
    $wakeMatch = [regex]::Match($power.Text, "mWakefulness=(\w+)")
    $wake = if ($wakeMatch.Success) { $wakeMatch.Groups[1].Value } else { "" }
    Add-Check "screen" $(if ($wake) { "mWakefulness=$wake" } else { "нет mWakefulness" }) (-not [string]::IsNullOrWhiteSpace($wake))
} catch {
    Add-Check "preflight" $_.Exception.Message $false
}

Write-M3dLine ("{0,-14} {1}" -f "проверка", "значение")
foreach ($row in $rows) {
    $status = if ($row.Ok) { "ok" } else { "FAIL" }
    Write-M3dLine ("{0,-14} {1}  {2}" -f $row.Name, $row.Value, $status)
}

if ($failed) { exit 1 }
exit 0
