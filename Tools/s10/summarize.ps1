#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Logs,
    [string]$Baseline,
    [string]$Out
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

function Get-LogFiles([string[]]$InputPaths) {
    $expanded = @()
    foreach ($item in $InputPaths) {
        if ((Test-Path $item) -or ($item -notmatch ",")) {
            $expanded += $item
            continue
        }
        foreach ($part in ($item -split ",")) {
            $expanded += $part.Trim().Trim('"')
        }
    }
    $files = @()
    foreach ($item in $expanded) {
        if (Test-Path $item -PathType Container) {
            $files += @(Get-ChildItem -Path $item -Filter *.txt -File | Sort-Object Name)
        } elseif (Test-Path $item -PathType Leaf) {
            $files += Get-Item $item
        } else {
            throw "Нет лога: $item"
        }
    }
    return $files
}

function Get-RunRows($File) {
    $text = [IO.File]::ReadAllText($File.FullName)
    $wakeMatch = [regex]::Match($text, "mWakefulness=(\w+)")
    $wake = if ($wakeMatch.Success) { $wakeMatch.Groups[1].Value } else { "na" }
    $runs = @(Read-M3dPerfText $text)
    $rows = @()
    foreach ($run in $runs) {
        if (-not $run.Done) { continue }
        $reasons = @(Get-M3dLenientReasons $run)
        $frame = Get-M3dNumber $run.Done["frameAvg"]
        $long = Get-M3dNumber $run.Done["long"]
        $rows += [pscustomobject]@{
            File = $File.Name
            FullName = $File.FullName
            Mode = Get-M3dMode $run.Done
            FrameAvg = $frame
            Long = $long
            Wake = $wake
            Walk = $(if ([string]::IsNullOrWhiteSpace($run.Done["walk"])) { "na" } else { $run.Done["walk"] })
            Res = $run.Done["res"]
            Valid = ($reasons.Count -eq 0 -and $null -ne $frame -and $null -ne $long)
            Reason = ($reasons -join ", ")
        }
    }
    return $rows
}

$files = @(Get-LogFiles $Logs)
$results = @()
foreach ($file in $files) { $results += @(Get-RunRows $file) }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# S10 summary")
$lines.Add("")
$lines.Add("| file | mode | walk | frameAvg | long | wakefulness | valid | reason |")
$lines.Add("|---|---|---|---:|---:|---|---|---|")
foreach ($row in $results) {
    $frameText = if ($null -eq $row.FrameAvg) { "na" } else { $row.FrameAvg.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture) }
    $longText = if ($null -eq $row.Long) { "na" } else { $row.Long.ToString("0.000", [Globalization.CultureInfo]::InvariantCulture) }
    $validText = if ($row.Valid) { "1" } else { "0" }
    $wakeText = if ([string]::IsNullOrWhiteSpace($row.Wake)) { "na" } else { $row.Wake }
    $walkText = if ([string]::IsNullOrWhiteSpace($row.Walk)) { "na" } else { $row.Walk }
    $lines.Add("| $($row.File) | $($row.Mode) | $walkText | $frameText | $longText | $wakeText | $validText | $($row.Reason) |")
    Write-M3dLine ("{0} mode={1} walk={2} frameAvg={3} long={4} wakefulness={5} valid={6} {7}" -f $row.File, $row.Mode, $walkText, $frameText, $longText, $wakeText, $validText, $row.Reason)
}

$lines.Add("")
$lines.Add("| mode | walk | n | median frameAvg | spread | median long |")
$lines.Add("|---|---|---:|---:|---:|---:|")
$byKey = @{}
$groupOrder = @()
foreach ($mode in @("A", "B", "C")) {
    $walks = @($results | Where-Object { $_.Mode -eq $mode -and $_.Valid } | ForEach-Object { $_.Walk } | Select-Object -Unique)
    foreach ($walk in $walks) {
        $valid = @($results | Where-Object { $_.Mode -eq $mode -and $_.Walk -eq $walk -and $_.Valid })
        if ($valid.Count -eq 0) { continue }
        $frames = @($valid | ForEach-Object { [double]$_.FrameAvg })
        $longs = @($valid | ForEach-Object { [double]$_.Long })
        $median = Get-M3dMedian $frames
        $spread = ([double]($frames | Measure-Object -Maximum).Maximum) - ([double]($frames | Measure-Object -Minimum).Minimum)
        $medianLong = Get-M3dMedian $longs
        $key = "$mode|$walk"
        $byKey[$key] = [pscustomobject]@{ Mode = $mode; Walk = $walk; Median = $median; Spread = $spread; Long = $medianLong; N = $valid.Count }
        $groupOrder += $key
        $lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $mode, $walk, $valid.Count, `
            $median.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture), `
            $spread.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture), `
            $medianLong.ToString("0.000", [Globalization.CultureInfo]::InvariantCulture)))
        Write-M3dLine ("mode={0} walk={1} n={2} median={3} spread={4} long={5}" -f $mode, $walk, $valid.Count, `
            $median.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture), `
            $spread.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture), `
            $medianLong.ToString("0.000", [Globalization.CultureInfo]::InvariantCulture))
    }
}

$walksForCost = @($groupOrder | ForEach-Object { $byKey[$_].Walk } | Select-Object -Unique)
foreach ($walk in $walksForCost) {
    $keyA = "A|$walk"
    $keyB = "B|$walk"
    if (-not $byKey.ContainsKey($keyA) -or -not $byKey.ContainsKey($keyB)) { continue }
    $cost = $byKey[$keyA].Median - $byKey[$keyB].Median
    $costText = $cost.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture)
    $lines.Add("")
    $lines.Add("Render cost A-B walk=${walk}: $costText ms")
    Write-M3dLine "renderCost=$costText walk=$walk"
}

if ($Baseline) {
    if (-not (Test-Path $Baseline)) { throw "Нет baseline: $Baseline" }
    $baseRows = @(Import-Csv $Baseline)
    $lines.Add("")
    $lines.Add("| mode | delta frameAvg | delta long | note |")
    $lines.Add("|---|---:|---:|---|")
    foreach ($mode in @("A", "B", "C")) {
        $key = "$mode|na"
        if (-not $byKey.ContainsKey($key)) { continue }
        $base = $baseRows | Where-Object { $_.mode -eq $mode } | Select-Object -First 1
        if (-not $base) { continue }
        $dFrame = $byKey[$key].Median - (Get-M3dNumber $base.frameAvg)
        $dLong = $byKey[$key].Long - (Get-M3dNumber $base.long)
        $spread = $byKey[$key].Spread
        $frameNoise = [Math]::Abs($dFrame) -lt [Math]::Max(2.0 * $spread, 0.5)
        $longNoise = [Math]::Abs($dLong) -lt 0.03
        $note = if ($frameNoise) { "шум" } else { "кадр" }
        if ($longNoise) { $note = "$note, long шум" }
        $lines.Add(("| {0} | {1} | {2} | {3} |" -f $mode, `
            $dFrame.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture), `
            $dLong.ToString("0.000", [Globalization.CultureInfo]::InvariantCulture), $note))
        Write-M3dLine ("baseline mode={0} dFrame={1} dLong={2} {3}" -f $mode, `
            $dFrame.ToString("0.00", [Globalization.CultureInfo]::InvariantCulture), `
            $dLong.ToString("0.000", [Globalization.CultureInfo]::InvariantCulture), $note)
    }
}

$csvRows = @()
foreach ($row in $results) {
    $csvRows += [pscustomobject]@{
        file = $row.File
        mode = $row.Mode
        frameAvg = $row.FrameAvg
        long = $row.Long
        wakefulness = $row.Wake
        walk = $row.Walk
        valid = $(if ($row.Valid) { 1 } else { 0 })
        reason = $row.Reason
    }
}

if ($Out) {
    $md = $lines -join "`r`n"
    [IO.File]::WriteAllText($Out, $md, [Text.UTF8Encoding]::new($false))
    $csvPath = Join-Path (Split-Path $Out) "summary.csv"
    if ($csvRows.Count -gt 0) { $csvRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8 }
    Write-M3dLine "wrote $Out"
}

$failed = $false
foreach ($row in $results) {
    $expectFrame = $null
    $expectLong = $null
    if ($row.File -eq "1519383_logcat.txt") { $expectFrame = 16.71; $expectLong = 0.0 }
    if ($row.File -eq "1519383_.txt") { $expectFrame = 19.57; $expectLong = 0.171 }
    if ($null -eq $expectFrame) { continue }
    $frameOk = $null -ne $row.FrameAvg -and [Math]::Abs($row.FrameAvg - $expectFrame) -lt 0.001
    $longOk = $null -ne $row.Long -and [Math]::Abs($row.Long - $expectLong) -lt 0.0005
    if (-not $row.Valid -or -not $frameOk -or -not $longOk) {
        Write-M3dLine ("REGRESSION fail {0} frameAvg={1} long={2}" -f $row.File, $row.FrameAvg, $row.Long)
        $failed = $true
    } else {
        Write-M3dLine ("REGRESSION ok {0}" -f $row.File)
    }
}

if ($failed) { exit 1 }
exit 0
