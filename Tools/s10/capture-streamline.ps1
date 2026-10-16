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
    [string]$Streamline
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
$unityArgs = "-m3d_force $force -m3d_render $render -m3d_maxn 48 -m3d_warmup $WarmupSec -m3d_measure $MeasureSec -m3d_autorun -m3d_quit -m3d_tag $tag"
$duration = $WarmupSec + $MeasureSec + 30
$outDir = Get-M3dOutDir

Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "am", "force-stop", "com.slonInc.vfx") | Out-Null

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
    exit 1
}
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
    exit 1
}
if ($FixtureOut) {
    $destDir = Split-Path $FixtureOut -Parent
    if ($destDir -and -not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item $csvPath $FixtureOut -Force
    Write-M3dLine "fixture $FixtureOut"
}
Write-M3dLine "report $csvPath"
exit 0
