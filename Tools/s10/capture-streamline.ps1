#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("A", "B", "C", "D")]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [string]$ApcName,
    [int]$WarmupSec = 60,
    [int]$MeasureSec = 25,
    [string]$Session,
    [string]$FixtureOut,
    [string]$Serial = "RF8N31SLV7N",
    [string]$Adb,
    [string]$Streamline,
    [string]$Scene = "",
    [string]$Cam = "",
    [string]$Walk = "",
    [string]$ForceRep = "",
    [string]$MaxN = "",
    [string]$LogOut = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

$nameError = Test-M3dApcName $ApcName
if ($nameError) {
    Write-M3dLine $nameError
    exit 1
}

$adbPath = Resolve-M3dAdb -Adb $Adb
$streamPath = Resolve-M3dStreamline -Streamline $Streamline
& (Join-Path $PSScriptRoot "preflight.ps1") -Serial $Serial -Adb $adbPath -Streamline $streamPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$apcPath = Get-M3dApcPath $ApcName
Write-M3dLine ("Захват: {0}" -f $apcPath)

if (-not $Session) { $Session = Join-Path $PSScriptRoot "session-gpu.xml" }
if (-not (Test-Path $Session)) { throw "Нет session.xml: $Session" }
$sessionPath = (Resolve-Path $Session).Path

$switches = @{ A = @(1, 1); B = @(1, 0); C = @(0, 1); D = @(0, 0) }
$force = $switches[$Mode][0]
$render = $switches[$Mode][1]
$tag = $Mode
if (-not [string]::IsNullOrWhiteSpace($Walk) -and $Walk -notmatch '^[012]$') {
    throw "Walk: '$Walk' не разобран. Нужен 0, 1 или 2."
}
$camText = ""
if (-not [string]::IsNullOrWhiteSpace($Cam)) {
    $camValue = 0.0
    $camOk = [double]::TryParse($Cam, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$camValue)
    if (-not $camOk -or $camValue -le 0) { throw "Камера: '$Cam' не разобрана. Нужно положительное число." }
    $camText = $camValue.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
}
$forceRepText = ""
if (-not [string]::IsNullOrWhiteSpace($ForceRep)) {
    $forceRepValue = 0
    $forceOk = [int]::TryParse($ForceRep, [ref]$forceRepValue)
    if (-not $forceOk -or $forceRepValue -lt 1 -or $forceRepValue -gt 16) { throw "Повтор силы: '$ForceRep' не разобран. Нужно целое от 1 до 16." }
    $forceRepText = [string]$forceRepValue
}
$maxNeighborsText = "48"
if (-not [string]::IsNullOrWhiteSpace($MaxN)) {
    $maxNValue = 0
    $maxNOk = [int]::TryParse($MaxN, [ref]$maxNValue)
    if (-not $maxNOk -or $maxNValue -lt 0) { throw "Кап соседей: '$MaxN' не разобран. Нужно целое не меньше 0." }
    $maxNeighborsText = [string]$maxNValue
}
$unityArgs = "-m3d_force $force -m3d_render $render -m3d_maxn $maxNeighborsText -m3d_warmup $WarmupSec -m3d_measure $MeasureSec -m3d_autorun -m3d_quit -m3d_tag $tag"
if (-not [string]::IsNullOrWhiteSpace($Scene)) { $unityArgs = $unityArgs + " -m3d_scene $Scene" }
if (-not [string]::IsNullOrWhiteSpace($Walk)) { $unityArgs = $unityArgs + " -m3d_walk $Walk" }
if (-not [string]::IsNullOrWhiteSpace($camText)) { $unityArgs = $unityArgs + " -m3d_cam $camText" }
if (-not [string]::IsNullOrWhiteSpace($forceRepText)) { $unityArgs = $unityArgs + " -m3d_forcerep $forceRepText" }
$duration = $WarmupSec + $MeasureSec + 30
$outDir = Get-M3dOutDir

Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "am", "force-stop", "com.slonInc.vfx") | Out-Null

$wakeDump = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "dumpsys", "power")
$wakeMatch = [regex]::Match($wakeDump.Text, "mWakefulness=(\w+)")
$wakeValue = if ($wakeMatch.Success) { $wakeMatch.Groups[1].Value } else { "" }
if ([string]::IsNullOrWhiteSpace($wakeValue)) {
    Write-M3dLine "нет mWakefulness. Захват остановлен."
    exit 2
}
if ($wakeValue -eq "Dozing" -or $wakeValue -eq "Asleep") {
    Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "input", "keyevent", "KEYCODE_WAKEUP") | Out-Null
    Start-Sleep -Seconds 2
    $wakeDump = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "dumpsys", "power")
    $wakeMatch = [regex]::Match($wakeDump.Text, "mWakefulness=(\w+)")
    $wakeValue = if ($wakeMatch.Success) { $wakeMatch.Groups[1].Value } else { "" }
    if ([string]::IsNullOrWhiteSpace($wakeValue)) {
        Write-M3dLine "нет mWakefulness. Захват остановлен."
        exit 2
    }
}
Write-M3dLine "mWakefulness=$wakeValue"
$winDump = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "dumpsys", "window")
$winText = $winDump.Text
$rot = [regex]::Match($winText, "mCurrentRotation=ROTATION_(\w+)")
$bounds = [regex]::Match($winText, "mGlobalConfiguration=\{[\s\S]{0,600}?mBounds=Rect\(0, 0 - (\d+), (\d+)\)")
$landscape = $rot.Success -and $rot.Groups[1].Value -eq "270" -and $bounds.Success -and $bounds.Groups[1].Value -eq "1520" -and $bounds.Groups[2].Value -eq "720"
if (-not $landscape) {
    Write-M3dLine "Экран не 1520x720 ROTATION_270. Захват остановлен."
    exit 2
}

$rawLog = Join-Path (Get-M3dOutDir) "capture-logcat.txt"
Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "logcat", "-c") | Out-Null
if (Test-Path $rawLog) { Remove-Item $rawLog -Force }
$logProc = Start-Process -FilePath $adbPath -ArgumentList @("-s", $Serial, "logcat", "-s", "Unity") `
    -RedirectStandardOutput $rawLog -RedirectStandardError (Join-Path (Get-M3dOutDir) "capture-logcat.err") `
    -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 400

function Save-CaptureLog {
    if ($logProc -and -not $logProc.HasExited) {
        Stop-Process -Id $logProc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
    $raw = Read-M3dSharedText $rawLog
    $logPath = $LogOut
    if ([string]::IsNullOrWhiteSpace($logPath)) {
        if (-not [string]::IsNullOrWhiteSpace($FixtureOut)) {
            $dir = Split-Path $FixtureOut -Parent
            if ([string]::IsNullOrWhiteSpace($dir)) { $dir = "." }
            $base = [IO.Path]::GetFileNameWithoutExtension($FixtureOut)
            $logPath = Join-Path $dir ($base + "-log.txt")
        } else {
            $logPath = Join-Path (Get-M3dOutDir) ([IO.Path]::GetFileNameWithoutExtension($ApcName) + "-log.txt")
        }
    }
    $parent = Split-Path $logPath -Parent
    if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $body = "mWakefulness=$wakeValue`r`n" + $raw
    [IO.File]::WriteAllText($logPath, $body, [Text.UTF8Encoding]::new($false))
    Write-M3dLine "log $logPath"
}

$captureExit = 0
try {
$captureCmd = @"
@echo off
"$streamPath" -capture "$sessionPath" -template "Mali-G76 - Bifrost" -package com.slonInc.vfx -activity com.unity3d.player.UnityPlayerGameActivity -activity-args "-e unity '$unityArgs'" -clean-gatord -duration $duration -o "$apcPath"
"@
$captureFile = Write-M3dCmd $captureCmd
Write-M3dLine ($captureCmd.Trim())
$capture = Invoke-M3d -File "cmd.exe" -ArgumentList @("/c", $captureFile)
Remove-Item $captureFile -ErrorAction SilentlyContinue
Write-M3dLine $capture.Text.Trim()
$created = ($capture.Text -match "Created Streamline capture") -and (Test-Path $apcPath)
if (-not $created) {
    Write-M3dLine "Захват не создан."
    $captureExit = 1
} else {
if ($capture.Code -ne 0) {
    Write-M3dLine ("Streamline-cli завершился с кодом {0} после создания захвата. Каталог на месте, захват принимается." -f $capture.Code)
}

$timeline = [IO.Path]::GetFileNameWithoutExtension($ApcName) + ".csv"
$reportCmd = @"
@echo off
"$streamPath" -report "$apcPath" -timeline=$timeline -scale 100 -start $WarmupSec -stop $($WarmupSec + $MeasureSec) -default-template with-metrics -format csv -output "$outDir"
"@
$reportFile = Write-M3dCmd $reportCmd
Write-M3dLine ($reportCmd.Trim())
$report = Invoke-M3d -File "cmd.exe" -ArgumentList @("/c", $reportFile)
Remove-Item $reportFile -ErrorAction SilentlyContinue
Write-M3dLine $report.Text.Trim()
$csvPath = Join-Path $outDir $timeline
if ($report.Code -ne 0 -or -not (Test-Path $csvPath)) {
    Write-M3dLine "Отчёт Streamline не записан."
    $captureExit = 1
} else {
if ($FixtureOut) {
    $destDir = Split-Path $FixtureOut -Parent
    if ($destDir -and -not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item $csvPath $FixtureOut -Force
    Write-M3dLine "fixture $FixtureOut"
}
Write-M3dLine "report $csvPath"
}
}
} finally {
    Save-CaptureLog
}
exit $captureExit
