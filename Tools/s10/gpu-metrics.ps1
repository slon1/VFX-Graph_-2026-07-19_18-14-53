#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Timeline,
    [double]$FrameAvgMs = 0,
    [int]$ParticleCount = 0,
    [string]$Out
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

if (-not (Test-Path $Timeline)) { throw "Нет timeline: $Timeline" }

$tl = Read-M3dTimeline $Timeline
$gpu = Get-M3dColumnSum $tl "Mali GPU Cycles:GPU active"
$nonFrag = Get-M3dColumnSum $tl "Mali GPU Cycles:Non-fragment queue active"
$frag = Get-M3dColumnSum $tl "Mali GPU Cycles:Fragment queue active"
if ($null -eq $gpu -or $gpu -le 0) { throw "Нет колонки Mali GPU Cycles:GPU active или сумма нулевая" }

$duration = [double]$tl.DurationSec
$activeHz = $gpu / $duration
$activeMHz = $activeHz / 1e6
$nonFragShare = if ($nonFrag) { $nonFrag / $gpu } else { $null }
$fragShare = if ($frag) { $frag / $gpu } else { $null }

$clockIdx = Get-M3dColumn $tl.Header "Mali Clock:Frequency (Device #0)"
$gpuIdx = Get-M3dColumn $tl.Header "Mali GPU Cycles:GPU active"
$clocks = @()
$utils = @()
if ($clockIdx -ge 0) {
    foreach ($row in $tl.Rows) {
        if ($clockIdx -ge $row.Length -or $gpuIdx -ge $row.Length) { continue }
        $clock = Convert-M3dCsvNumber $row[$clockIdx]
        $active = Convert-M3dCsvNumber $row[$gpuIdx]
        if ($clock -gt 0) {
            $clocks += $clock
            $utils += ($active / ($clock * 0.01))
        }
    }
}
$clockMHz = $null
$utilMean = $null
if ($clocks.Count -gt 0) {
    $clockMHz = (Get-M3dMedian $clocks) / 1e6
    $utilMean = ($utils | Measure-Object -Average).Average
}

$frames = $null
if ($FrameAvgMs -gt 0) { $frames = $duration / ($FrameAvgMs / 1000.0) }

function Get-PerFrame([string]$Column) {
    if ($null -eq $frames -or $frames -le 0) { return $null }
    $sum = Get-M3dColumnSum $tl $Column
    if ($null -eq $sum) { return $null }
    return $sum / $frames
}

$gpuCyclesPerFrame = $null
$cyclesPerParticle = $null
if ($null -ne $frames -and $frames -gt 0) {
    $gpuCyclesPerFrame = $gpu / $frames
    if ($ParticleCount -gt 0) { $cyclesPerParticle = $gpuCyclesPerFrame / $ParticleCount }
}

$compute = Get-PerFrame "Mali Shader Core Cycles:Execution core active"
$atomics = Get-PerFrame "Mali Load/Store Unit Cycles:Atomic accesses"
$readBeats = Get-M3dColumnSum $tl "Mali External Bus Beats:Read beats"
$writeBeats = Get-M3dColumnSum $tl "Mali External Bus Beats:Write beats"
$beatsPerFrame = $null
if ($null -ne $frames -and $frames -gt 0 -and $null -ne $readBeats -and $null -ne $writeBeats) {
    $beatsPerFrame = ($readBeats + $writeBeats) / $frames
}

$diverged = Get-M3dColumnSum $tl "Mali ALU Instructions:Diverged instructions"
$executed = Get-M3dColumnSum $tl "Mali ALU Instructions:Executed instructions"
$divergedShare = $null
if ($diverged -and $executed -and $executed -gt 0) { $divergedShare = $diverged / $executed }

$partial = Get-M3dColumnSum $tl "Mali Load/Store Unit Cycles:Partial reads"
$full = Get-M3dColumnSum $tl "Mali Load/Store Unit Cycles:Full reads"
$partialShare = $null
if ($null -ne $partial -and $null -ne $full -and ($partial + $full) -gt 0) {
    $partialShare = $partial / ($partial + $full)
}

$visible = Get-M3dColumnSum $tl "Mali Primitive Culling:Visible primitives"
$culled = 0.0
$hasCull = $false
foreach ($name in @(
    "Mali Primitive Culling:Facing or XY plane culled primitives",
    "Mali Primitive Culling:Sample culled primitives",
    "Mali Primitive Culling:Z plane culled primitives")) {
    $part = Get-M3dColumnSum $tl $name
    if ($null -ne $part) { $culled += $part; $hasCull = $true }
}
$visibleShare = $null
$culledShare = $null
if ($null -ne $visible -and $hasCull -and ($visible + $culled) -gt 0) {
    $visibleShare = $visible / ($visible + $culled)
    $culledShare = $culled / ($visible + $culled)
}

function Format-Metric($Value, [string]$Fmt) {
    if ($null -eq $Value) { return "na" }
    return ([double]$Value).ToString($Fmt, [Globalization.CultureInfo]::InvariantCulture)
}

$fullPath = [IO.Path]::GetFullPath($Timeline)
$isOld = $fullPath -match '\\s10\\fixtures\\'
$isNew = $fullPath -match '\\s10_\\fixtures\\'
$isFixture = $isOld -or $isNew
$below600 = (-not $isFixture) -and ($activeMHz -lt 600)

Write-M3dLine ("file={0}" -f (Split-Path $Timeline -Leaf))
Write-M3dLine ("durationSec={0} rows={1}" -f (Format-Metric $duration "0.00"), $tl.Rows.Count)
Write-M3dLine ("activeMHz={0}" -f (Format-Metric $activeMHz "0.0"))
Write-M3dLine ("clockMHz={0}" -f (Format-Metric $clockMHz "0.0"))
Write-M3dLine ("nonFragment={0}" -f (Format-Metric $nonFragShare "0.000"))
Write-M3dLine ("fragment={0}" -f (Format-Metric $fragShare "0.000"))
Write-M3dLine ("utilization={0}" -f (Format-Metric $utilMean "0.000"))
Write-M3dLine ("computePerFrame={0}" -f (Format-Metric $compute "0"))
Write-M3dLine ("gpuCyclesPerFrame={0}" -f (Format-Metric $gpuCyclesPerFrame "0"))
Write-M3dLine ("cyclesPerParticle={0}" -f (Format-Metric $cyclesPerParticle "0.00"))
Write-M3dLine ("diverged={0}" -f (Format-Metric $divergedShare "0.000"))
Write-M3dLine ("partialReads={0}" -f (Format-Metric $partialShare "0.000"))
Write-M3dLine ("atomicsPerFrame={0}" -f (Format-Metric $atomics "0.0"))
Write-M3dLine ("beatsPerFrame={0}" -f (Format-Metric $beatsPerFrame "0"))
Write-M3dLine ("visible={0}" -f (Format-Metric $visibleShare "0.000"))
Write-M3dLine ("culled={0}" -f (Format-Metric $culledShare "0.000"))
if ($below600) { Write-M3dLine "INVALID activeMHz below 600" }

if (-not $Out) {
    $base = [IO.Path]::GetFileNameWithoutExtension($Timeline)
    $seriesName = Split-Path (Split-Path (Split-Path $Timeline -Parent) -Parent) -Leaf
    $Out = Join-Path (Get-M3dOutDir) ($seriesName + "-" + $base + "-gpu-metrics.csv")
}
$row = [pscustomobject]@{
    file = (Split-Path $Timeline -Leaf)
    activeMHz = Format-Metric $activeMHz "0.0"
    clockMHz = Format-Metric $clockMHz "0.0"
    nonFragment = Format-Metric $nonFragShare "0.000"
    fragment = Format-Metric $fragShare "0.000"
    utilization = Format-Metric $utilMean "0.000"
    computePerFrame = Format-Metric $compute "0"
    gpuCyclesPerFrame = Format-Metric $gpuCyclesPerFrame "0"
    cyclesPerParticle = Format-Metric $cyclesPerParticle "0.00"
    diverged = Format-Metric $divergedShare "0.000"
    partialReads = Format-Metric $partialShare "0.000"
    atomicsPerFrame = Format-Metric $atomics "0.0"
    beatsPerFrame = Format-Metric $beatsPerFrame "0"
    visible = Format-Metric $visibleShare "0.000"
    culled = Format-Metric $culledShare "0.000"
    below600 = $(if ($below600) { 1 } else { 0 })
}
$row | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8
Write-M3dLine "wrote $Out"

$failed = $false
$name = Split-Path $Timeline -Leaf
if ($isOld -and $name -eq "capture-pre.csv") {
    $mhzOk = [Math]::Abs($activeMHz - 369.7) -le (369.7 * 0.05)
    $nfOk = $null -ne $nonFragShare -and [Math]::Abs($nonFragShare - 0.995) -le 0.005
    if ($mhzOk -and $nfOk) { Write-M3dLine "CHECK ok capture-pre 369.7" } else { Write-M3dLine "CHECK fail capture-pre"; $failed = $true }
}
if ($isOld -and $name -eq "capture-post.csv") {
    $mhzOk = [Math]::Abs($activeMHz - 680.2) -le (680.2 * 0.05)
    if ($mhzOk) { Write-M3dLine "CHECK ok capture-post 680.2" } else { Write-M3dLine "CHECK fail capture-post"; $failed = $true }
}
if ($isNew) {
    $clockOk = $null -ne $clockMHz -and $clockMHz -ge 695 -and $clockMHz -le 710
    $utilOk = $null -ne $utilMean -and $utilMean -ge 0.94 -and $utilMean -le 0.99
    if ($clockOk -and $utilOk) { Write-M3dLine "CHECK ok s10_ 702" } else { Write-M3dLine "CHECK fail s10_"; $failed = $true }
}
if ($below600) { $failed = $true }

if ($failed) { exit 1 }
exit 0
