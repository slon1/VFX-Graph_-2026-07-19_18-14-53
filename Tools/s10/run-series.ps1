#Requires -Version 5.1
param(
    [string]$Serial = "RF8N31SLV7N",
    [string]$Series = "ABABC",
    [int]$WarmupSec = 60,
    [int]$MeasureSec = 25,
    [int]$CooldownSec = 60,
    [string]$ExpectBuild,
    [string]$Tag,
    [switch]$Resume,
    [switch]$PullCsv,
    [string]$Adb,
    [string]$Streamline,
    [string]$Scene = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

$package = "com.slonInc.vfx"
$activity = "com.unity3d.player.UnityPlayerGameActivity"
$maxNeighbors = 48

function Get-ModeSwitches([string]$Mode) {
    switch ($Mode) {
        "A" { return @{ Force = 1; Render = 1 } }
        "B" { return @{ Force = 1; Render = 0 } }
        "C" { return @{ Force = 0; Render = 1 } }
        default { throw "Неизвестный режим $Mode" }
    }
}

function New-PointPlan([string]$Spec) {
    $counts = @{ A = 0; B = 0; C = 0 }
    $plan = @()
    $n = 0
    foreach ($ch in $Spec.ToCharArray()) {
        $mode = [string]$ch
        if ($mode -notmatch '^[ABC]$') { throw "Серия содержит не A/B/C: $mode" }
        $counts[$mode] = $counts[$mode] + 1
        $n++
        $pointTag = "$mode$($counts[$mode])"
        if ($Tag) { $pointTag = "$Tag$pointTag" }
        $plan += [pscustomobject]@{
            n = $n
            mode = $mode
            tag = $pointTag
            status = "pending"
            file = ""
            reason = ""
        }
    }
    return $plan
}

function Save-Progress($State, [string]$Path) {
    $json = $State | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-ExpectedArgs($Point) {
    $sw = Get-ModeSwitches $Point.mode
    $text = ("force={0},render={1},maxn={2},warmup={3},measure={4},autorun=1,quit=1,tag={5}" -f `
        $sw.Force, $sw.Render, $maxNeighbors, $WarmupSec, $MeasureSec, $Point.tag)
    if (-not [string]::IsNullOrWhiteSpace($Scene)) { $text = $text + ",scene=$Scene" }
    return $text
}

function Get-UnityArgs($Point) {
    $sw = Get-ModeSwitches $Point.mode
    $text = ("-m3d_force {0} -m3d_render {1} -m3d_maxn {2} -m3d_warmup {3} -m3d_measure {4} -m3d_autorun -m3d_quit -m3d_tag {5}" -f `
        $sw.Force, $sw.Render, $maxNeighbors, $WarmupSec, $MeasureSec, $Point.tag)
    if (-not [string]::IsNullOrWhiteSpace($Scene)) { $text = $text + " -m3d_scene $Scene" }
    return $text
}

$adbPath = Resolve-M3dAdb -Adb $Adb
$activePath = Join-Path (Get-M3dOutDir) "active-series.txt"
$measureRoot = Join-Path (Get-M3dRepoRoot) "DOC\MEASURE"

if ($Resume) {
    & (Join-Path $PSScriptRoot "preflight.ps1") -Serial $Serial -Adb $adbPath -Streamline $Streamline
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path $activePath)) { throw "Нет активной серии. Запустите без -Resume." }
    $resultDir = [IO.File]::ReadAllText($activePath).Trim()
    $progressPath = Join-Path $resultDir "progress.json"
    if (-not (Test-Path $progressPath)) { throw "Нет progress.json в $resultDir" }
    $state = Get-Content $progressPath -Raw | ConvertFrom-Json
    if ($state.stopped) {
        Write-M3dLine ("Серия остановлена: {0}. -Resume её не продолжает." -f $state.reason)
        exit 2
    }
    $points = @($state.points)
    $pending = @($points | Where-Object { $_.status -eq "pending" } | Select-Object -First 1)
    if ($pending.Count -eq 0) {
        Write-M3dLine "Серия уже завершена."
        exit 0
    }
    $invalid = @($points | Where-Object { $_.status -eq "invalid" })
    if ($invalid.Count -gt 0) {
        Write-M3dLine "-Resume не повторяет invalid."
        exit 2
    }
} else {
    & (Join-Path $PSScriptRoot "preflight.ps1") -Serial $Serial -Adb $adbPath -Streamline $Streamline
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $stamp = Get-Date -Format "yyyyMMdd-HHmm"
    $resultDir = Join-Path (Join-Path $measureRoot "nabuild-na") $stamp
    if (Test-Path $resultDir) { $resultDir = Join-Path (Join-Path $measureRoot "nabuild-na") (Get-Date -Format "yyyyMMdd-HHmmss") }
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    [IO.File]::WriteAllText($activePath, $resultDir)
    $points = @(New-PointPlan $Series)
    $state = [pscustomobject]@{
        series = $Series
        stopped = $false
        reason = ""
        resultDir = $resultDir
        points = $points
    }
    $progressPath = Join-Path $resultDir "progress.json"
    Save-Progress $state $progressPath
}

function Update-Point($State, $Point, [string]$Status, [string]$File, [string]$Reason) {
    foreach ($item in @($State.points)) {
        if ([int]$item.n -eq [int]$Point.n) {
            $item.status = $Status
            $item.file = $File
            $item.reason = $Reason
        }
    }
}

function Move-ResultDir([string]$Dir, [string]$Build, [string]$DirtyHash) {
    $buildName = $Build
    $hashName = $DirtyHash
    if ([string]::IsNullOrWhiteSpace($buildName) -or $buildName -eq "na") { $buildName = "nabuild" }
    if ([string]::IsNullOrWhiteSpace($hashName)) { $hashName = "na" }
    $stamp = Split-Path $Dir -Leaf
    $parent = Join-Path $measureRoot "$buildName-$hashName"
    $dest = Join-Path $parent $stamp
    if ($Dir -eq $dest) { return $Dir }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path $dest) { $dest = Join-Path $parent ($stamp + "-" + (Get-Date -Format "ss")) }
    Move-Item -Path $Dir -Destination $dest
    return $dest
}

$rawLog = Join-Path (Get-M3dOutDir) "series-logcat.txt"
$timeoutSec = $WarmupSec + $MeasureSec + 30

foreach ($point in @($state.points)) {
    if ($point.status -ne "pending") { continue }

    $wake = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "dumpsys", "power")
    $wakeMatch = [regex]::Match($wake.Text, "mWakefulness=(\w+)")
    $wakeValue = if ($wakeMatch.Success) { $wakeMatch.Groups[1].Value } else { "" }
    if ([string]::IsNullOrWhiteSpace($wakeValue)) {
        $reason = "нет mWakefulness. Серия остановлена."
        Update-Point $state $point "invalid" "" $reason
        $state.stopped = $true
        $state.reason = $reason
        $state.resultDir = $resultDir
        Save-Progress $state (Join-Path $resultDir "progress.json")
        Write-M3dLine $reason
        exit 2
    }
    Write-M3dLine "mWakefulness=$wakeValue"

    Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "am", "force-stop", $package) | Out-Null
    Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "logcat", "-c") | Out-Null
    if (Test-Path $rawLog) { Remove-Item $rawLog -Force }
    $logProc = Start-Process -FilePath $adbPath -ArgumentList @("-s", $Serial, "logcat", "-s", "Unity") `
        -RedirectStandardOutput $rawLog -RedirectStandardError (Join-Path (Get-M3dOutDir) "series-logcat.err") `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 400

    $unityArgs = Get-UnityArgs $point
    $startLine = "adb -s $Serial shell `"am start -n $package/$activity -e unity '$unityArgs'`""
    Write-M3dLine $startLine
    $cmd = @"
@echo off
"$adbPath" -s $Serial shell "am start -n $package/$activity -e unity '$unityArgs'"
"@
    $cmdPath = Write-M3dCmd $cmd
    $start = Invoke-M3d -File "cmd.exe" -ArgumentList @("/c", $cmdPath)
    Remove-Item $cmdPath -ErrorAction SilentlyContinue
    Write-M3dLine $start.Text.Trim()

    $startFailed = ($start.Text -match "Error:|Exception") -or ($start.Text -notmatch "has extras")
    $done = $false
    if (-not $startFailed) {
        $deadline = (Get-Date).AddSeconds($timeoutSec)
        while ((Get-Date) -lt $deadline) {
            $chunk = Read-M3dSharedText $rawLog
            if ($chunk -match "phase=DONE") { $done = $true; break }
            Start-Sleep -Seconds 2
        }
    }
    if ($logProc -and -not $logProc.HasExited) { Stop-Process -Id $logProc.Id -Force -ErrorAction SilentlyContinue }

    $rawText = Read-M3dSharedText $rawLog
    $perfLines = @($rawText -split "`r?`n" | Where-Object { $_ -match "M3D_PERF" })
    $runs = @(Read-M3dPerfText ($perfLines -join "`n"))
    $run = $runs | Select-Object -Last 1
    $info = $null
    $doneFields = $null
    if ($run) { $info = $run.Info; $doneFields = $run.Done }

    if ($info -and $resultDir -match '\\nabuild-na\\') {
        $resultDir = Move-ResultDir $resultDir $info["build"] $info["dirtyhash"]
        [IO.File]::WriteAllText($activePath, $resultDir)
        $state.resultDir = $resultDir
    }

    $res = "na"
    if ($doneFields -and $doneFields["res"]) { $res = $doneFields["res"] }
    elseif ($info -and $info["screen"]) { $res = $info["screen"] }
    $side = Get-M3dShortSide $res
    $logName = "{0:D2}-{1}-{2}.txt" -f [int]$point.n, $point.mode, $side
    $logPath = Join-Path $resultDir $logName
    $logBody = "mWakefulness=$wakeValue`r`n" + ($perfLines -join "`r`n") + "`r`n"
    [IO.File]::WriteAllText($logPath, $logBody, [Text.UTF8Encoding]::new($false))

    $reasons = @()
    if ($startFailed) { $reasons += "am start без has extras или с ошибкой" }
    if (-not $done) { $reasons += "нет phase=DONE за $timeoutSec с" }
    $expected = Get-ExpectedArgs $point
    if (-not $info) { $reasons += "нет M3D_PERF_INFO" }
    else {
        if ($info["applied"] -ne "1") { $reasons += "applied=$($info['applied'])" }
        if ($info["args"] -ne $expected) { $reasons += "args=$($info['args'])" }
        if ($info["devbuild"] -ne "1") { $reasons += "devbuild=$($info['devbuild'])" }
        if ($info["powersave"] -ne "0") { $reasons += "powersave=$($info['powersave'])" }
        if ($ExpectBuild -and $info["build"] -ne $ExpectBuild) { $reasons += "build=$($info['build'])" }
    }
    if ($run -and $run.Warns -gt 0) { $reasons += "M3D_PERF_WARN" }

    Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "shell", "am", "force-stop", $package) | Out-Null

    if ($PullCsv) {
        $remote = "/storage/emulated/0/Android/data/com.slonInc.vfx/files/m3d_perf.csv"
        $local = Join-Path $resultDir ("{0:D2}-{1}-device.csv" -f [int]$point.n, $point.mode)
        $pull = Invoke-M3d -File $adbPath -ArgumentList @("-s", $Serial, "pull", $remote, $local)
        Write-M3dLine $pull.Text.Trim()
    }

    if ($reasons.Count -gt 0) {
        $reason = $reasons -join "; "
        Update-Point $state $point "invalid" $logName $reason
        $state.stopped = $true
        $state.reason = $reason
        $state.resultDir = $resultDir
        Save-Progress $state (Join-Path $resultDir "progress.json")
        Write-M3dLine ("Точка {0} invalid: {1}" -f $point.tag, $reason)
        exit 2
    }

    Update-Point $state $point "ok" $logName ""
    $state.resultDir = $resultDir
    Save-Progress $state (Join-Path $resultDir "progress.json")
    Write-M3dLine ("Точка {0} ok {1}" -f $point.tag, $logName)

    $more = @($state.points | Where-Object { $_.status -eq "pending" })
    if ($more.Count -gt 0 -and $CooldownSec -gt 0) {
        Write-M3dLine ("Пауза {0} с" -f $CooldownSec)
        Start-Sleep -Seconds $CooldownSec
    }
}

$summary = Join-Path $resultDir "summary.md"
& (Join-Path $PSScriptRoot "summarize.ps1") -Logs $resultDir -Out $summary
exit $LASTEXITCODE
