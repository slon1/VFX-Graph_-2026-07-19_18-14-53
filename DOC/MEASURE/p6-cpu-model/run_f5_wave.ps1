#Requires -Version 5.1
# F5.0b wave-cost proxy. Reads #f5 t= and #f5 wave. Does not change the model.
$ErrorActionPreference = "Stop"
$inv = [Globalization.CultureInfo]::InvariantCulture

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "boids_model.cpp"
$exe = Join-Path $env:TEMP "boids_model_f5.exe"
$logDir = Join-Path $env:TEMP "f5-wave"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$gppCmd = Get-Command g++ -ErrorAction SilentlyContinue
if ($gppCmd) { $gpp = $gppCmd.Source }
else { $gpp = "C:\Users\User\AppData\Local\Microsoft\WinGet\Packages\BrechtSanders.WinLibs.POSIX.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\mingw64\bin\g++.exe" }
& $gpp -O2 -std=c++17 -o $exe $src
if ($LASTEXITCODE -ne 0) { throw "g++ failed: $LASTEXITCODE" }

$discRx4 = [Math]::Sqrt(1500.0 * 128.0 * 128.0 / 100000.0)
$discLiteral = "15.676734353812339"
if ($discRx4.ToString("G17", $inv) -ne $discLiteral) { throw "discR drifted" }
$densities = @(
    [pscustomobject]@{ Id = 1; DiscR = [double]12 },
    [pscustomobject]@{ Id = 2; DiscR = $discRx4 },
    [pscustomobject]@{ Id = 3; DiscR = [double]6 }
)
$configs = New-Object System.Collections.Generic.List[object]
foreach ($cap in @(48, 96, 128, 160, 192, 256)) {
    $configs.Add([pscustomobject]@{ Mode = 0; Cap = $cap; MaxVis = 0 })
}
$configs.Add([pscustomobject]@{ Mode = 1; Cap = 48; MaxVis = 0 })
$configs.Add([pscustomobject]@{ Mode = 1; Cap = 48; MaxVis = 192 })
foreach ($maxVis in @(128, 160, 192)) {
    $configs.Add([pscustomobject]@{ Mode = 1; Cap = 64; MaxVis = $maxVis })
}

$work = New-Object System.Collections.Generic.List[object]
foreach ($d in $densities) {
    foreach ($cfg in $configs) {
        foreach ($seed in 1..5) {
            $work.Add([pscustomobject]@{ Density = $d; Mode = $cfg.Mode; Cap = $cfg.Cap; MaxVis = $cfg.MaxVis; Seed = $seed; T = "6" })
        }
        $work.Add([pscustomobject]@{ Density = $d; Mode = $cfg.Mode; Cap = $cfg.Cap; MaxVis = $cfg.MaxVis; Seed = 1; T = "20" })
    }
}

function Format-CsvField($value) {
    if ($null -eq $value) { return "" }
    if ($value -is [string]) { return $value }
    if ($value -is [double] -or $value -is [float] -or $value -is [decimal]) {
        return ([double]$value).ToString("G17", $inv)
    }
    return $value.ToString($inv)
}

function Parse-WaveLog([string]$path, $item) {
    $rows = New-Object System.Collections.Generic.List[object]
    $waves = @{}
    $gate = New-Object System.Collections.Generic.List[string]
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^#f5 t=(1\.00|5\.00) contact=([0-9.]+) enemy=([0-9.]+) flocks=(\d+),(\d+) visMean=([0-9.]+) ') {
            $rows.Add([pscustomobject]@{
                T = $Matches[1]
                Contact = [double]$Matches[2]
                Enemy = [double]$Matches[3]
                F0 = [int]$Matches[4]
                F1 = [int]$Matches[5]
                VisMean = [double]$Matches[6]
            })
            $gate.Add($line)
        } elseif ($line -match '^#f5 wave t=(1\.00|5\.00) eff8=([0-9.]+) eff16=([0-9.]+) eff32=([0-9.]+)') {
            $waves[$Matches[1]] = [pscustomobject]@{
                Eff8 = [double]$Matches[2]
                Eff16 = [double]$Matches[3]
                Eff32 = [double]$Matches[4]
            }
            $gate.Add($line)
        }
    }
    if ($gate.Count -ne 4) { throw "expected 4 gate lines in $path" }
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($row in $rows) {
        $w = $waves[$row.T]
        if ($null -eq $w) { throw "no wave for t=$($row.T) in $path" }
        $out.Add([pscustomobject]@{
            Density = $item.Density.Id; DiscR = $item.Density.DiscR; PerTeam = 1500
            Mode = $item.Mode; Cap = $item.Cap; MaxVis = $item.MaxVis; Seed = $item.Seed
            RunT = $item.T; T = $row.T
            Contact = $row.Contact; Enemy = $row.Enemy; F0 = $row.F0; F1 = $row.F1
            VisMean = $row.VisMean; Eff8 = $w.Eff8; Eff16 = $w.Eff16; Eff32 = $w.Eff32
            Gate = ($gate -join "`n")
        })
    }
    return $out
}

$slots = [Math]::Max(2, [Environment]::ProcessorCount - 1)
$queue = New-Object System.Collections.Queue
foreach ($item in $work) { $queue.Enqueue($item) }
$running = New-Object System.Collections.Generic.List[object]
$all = New-Object System.Collections.Generic.List[object]
$done = 0
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($running.Count -lt $slots -and $queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $tag = "d{0}_m{1}_c{2}_v{3}_s{4}_t{5}" -f $item.Density.Id, $item.Mode, $item.Cap, $item.MaxVis, $item.Seed, $item.T
        $log = Join-Path $logDir ($tag + ".txt")
        $disc = $item.Density.DiscR.ToString("G17", $inv)
        $arg = @("0.016666667", "4", "$($item.Cap)", "$($item.Seed)", $item.T, "1500", $disc, "$($item.Mode)", "$($item.MaxVis)")
        $proc = Start-Process -FilePath $exe -ArgumentList $arg -RedirectStandardOutput $log -PassThru -WindowStyle Hidden
        $running.Add([pscustomobject]@{ Proc = $proc; Item = $item; Log = $log })
    }
    Start-Sleep -Milliseconds 400
    $still = New-Object System.Collections.Generic.List[object]
    foreach ($r in $running) {
        if ($r.Proc.HasExited) {
            $r.Proc.WaitForExit()
            $code = $r.Proc.ExitCode
            if ($null -ne $code -and $code -ne 0) { throw "model exit $code $($r.Log)" }
            foreach ($row in (Parse-WaveLog $r.Log $r.Item)) { $all.Add($row) }
            $done++
            if (($done % 30) -eq 0) { Write-Host ("done {0}/{1}" -f $done, $work.Count) }
        } else {
            $still.Add($r)
        }
    }
    $running = $still
}
Write-Host ("done {0}/{1}" -f $done, $work.Count)

$matrix = @($all | Where-Object { $_.RunT -eq "6" })
$csvPath = Join-Path $root "F5-wave-proxy.csv"
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("density,discR,perTeam,mode,cap,maxVisited,seed,t,contact,enemy,flocks0,flocks1,visMean,eff8,eff16,eff32")
foreach ($row in $matrix) {
    $fields = @(
        $row.Density, ([double]$row.DiscR).ToString("G17", $inv), $row.PerTeam, $row.Mode, $row.Cap, $row.MaxVis, $row.Seed, $row.T,
        $row.Contact, $row.Enemy, $row.F0, $row.F1, $row.VisMean, $row.Eff8, $row.Eff16, $row.Eff32
    )
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($field in $fields) { $parts.Add((Format-CsvField $field)) }
    [void]$sb.AppendLine(($parts -join ","))
}
[IO.File]::WriteAllText($csvPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding $true))

function Get-Rows($density, $mode, $cap, $maxVis, $t) {
    @($matrix | Where-Object {
        $_.Density -eq $density -and $_.Mode -eq $mode -and $_.Cap -eq $cap -and $_.MaxVis -eq $maxVis -and $_.T -eq $t
    })
}

function Get-Mean($rows, [string]$prop) {
    $vals = @($rows | ForEach-Object { [double]($_.$prop) })
    ($vals | Measure-Object -Average).Average
}

function Format-Spread($rows, [string]$prop, [string]$fmt) {
    $vals = @($rows | ForEach-Object { [double]($_.$prop) })
    $mean = ($vals | Measure-Object -Average).Average
    $min = ($vals | Measure-Object -Minimum).Minimum
    $max = ($vals | Measure-Object -Maximum).Maximum
    $dash = [char]0x2013
    "{0} ({1}{2}{3})" -f $mean.ToString($fmt, $inv), $min.ToString($fmt, $inv), $dash, $max.ToString($fmt, $inv)
}

function Format-Mean($rows, [string]$prop, [string]$fmt) {
    (Get-Mean $rows $prop).ToString($fmt, $inv)
}

$fail = New-Object System.Collections.Generic.List[string]
$t6miss = 0
foreach ($d in $densities) {
    foreach ($cfg in $configs) {
        $a = @($all | Where-Object {
            $_.RunT -eq "6" -and $_.Seed -eq 1 -and $_.Density -eq $d.Id -and $_.Mode -eq $cfg.Mode -and $_.Cap -eq $cfg.Cap -and $_.MaxVis -eq $cfg.MaxVis -and $_.T -eq "1.00"
        })
        $b = @($all | Where-Object {
            $_.RunT -eq "20" -and $_.Seed -eq 1 -and $_.Density -eq $d.Id -and $_.Mode -eq $cfg.Mode -and $_.Cap -eq $cfg.Cap -and $_.MaxVis -eq $cfg.MaxVis -and $_.T -eq "1.00"
        })
        if ($a.Count -ne 1 -or $b.Count -ne 1) { throw "T check pair missing d$($d.Id) m$($cfg.Mode) c$($cfg.Cap) v$($cfg.MaxVis)" }
        if ($a[0].Gate -ne $b[0].Gate) {
            $t6miss++
            $fail.Add(("T=6 vs T=20 d={0} mode={1} cap={2} ceiling={3}" -f $d.Id, $cfg.Mode, $cfg.Cap, $cfg.MaxVis))
        }
    }
}

function Add-Diff($label, $got, $adr) {
    $diff = [Math]::Abs($got - $adr)
    if ($diff -gt 0.01 + 1e-9) {
        $fail.Add(("{0}: got {1} adr {2} diff {3}" -f $label, $got.ToString("G17", $inv), $adr.ToString("G17", $inv), $diff.ToString("G17", $inv)))
    }
}

$waveExpect = @(
    [pscustomobject]@{ D = 1; E8 = 127.00; E16 = 127.57; E32 = 127.84; A8 = 163.49; A16 = 175.44; A32 = 183.46; P0 = 29; P1 = 44 },
    [pscustomobject]@{ D = 2; E8 = 126.99; E16 = 127.38; E32 = 127.60; A8 = 143.50; A16 = 153.79; A32 = 163.11; P0 = 13; P1 = 28 },
    [pscustomobject]@{ D = 3; E8 = 127.43; E16 = 127.71; E32 = 127.96; A8 = 171.99; A16 = 180.75; A32 = 186.17; P0 = 35; P1 = 45 }
)
$pctNote = @{}
foreach ($ex in $waveExpect) {
    $vis = Get-Rows $ex.D 0 128 0 "5.00"
    $acc = Get-Rows $ex.D 1 48 192 "5.00"
    $ve = @( (Get-Mean $vis "Eff8"), (Get-Mean $vis "Eff16"), (Get-Mean $vis "Eff32") )
    $ae = @( (Get-Mean $acc "Eff8"), (Get-Mean $acc "Eff16"), (Get-Mean $acc "Eff32") )
    $adrV = @($ex.E8, $ex.E16, $ex.E32)
    $adrA = @($ex.A8, $ex.A16, $ex.A32)
    $names = @("eff8", "eff16", "eff32")
    $pcts = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt 3; $i++) {
        Add-Diff ("d{0} t=5 visited-128 {1}" -f $ex.D, $names[$i]) $ve[$i] $adrV[$i]
        Add-Diff ("d{0} t=5 accepted-48/192 {1}" -f $ex.D, $names[$i]) $ae[$i] $adrA[$i]
        $pct = [int][Math]::Round((($ae[$i] - $ve[$i]) / $ve[$i]) * 100.0, 0, [MidpointRounding]::AwayFromZero)
        $pcts.Add($pct)
    }
    $lo = ($pcts | Measure-Object -Minimum).Minimum
    $hi = ($pcts | Measure-Object -Maximum).Maximum
    $pctNote[$ex.D] = ("+{0}{1}+{2}" -f $lo, ([char]0x2026), $hi)
    if ($lo -ne $ex.P0 -or $hi -ne $ex.P1) {
        $fail.Add(("d{0} percent range {1} adr +{2}{3}+{4}" -f $ex.D, $pctNote[$ex.D], $ex.P0, ([char]0x2026), $ex.P1))
    }
}

$pairs = @(
    [pscustomobject]@{ Mode = 0; Cap = 128; MaxVis = 0; C = 24.23; En = 2.23; Eff = 127.57; Label = "visited-128" },
    [pscustomobject]@{ Mode = 1; Cap = 64; MaxVis = 128; C = 27.36; En = 1.83; Eff = 127.30; Label = "accepted-64/128" },
    [pscustomobject]@{ Mode = 0; Cap = 160; MaxVis = 0; C = 15.90; En = 0.77; Eff = 159.51; Label = "visited-160" },
    [pscustomobject]@{ Mode = 1; Cap = 64; MaxVis = 160; C = 12.95; En = 0.26; Eff = 156.14; Label = "accepted-64/160" },
    [pscustomobject]@{ Mode = 0; Cap = 192; MaxVis = 0; C = 6.00; En = 0.23; Eff = 190.88; Label = "visited-192" },
    [pscustomobject]@{ Mode = 1; Cap = 64; MaxVis = 192; C = 6.68; En = 0.14; Eff = 181.25; Label = "accepted-64/192" }
)
foreach ($p in $pairs) {
    $rows = Get-Rows 1 $p.Mode $p.Cap $p.MaxVis "5.00"
    Add-Diff ("pair {0} contact" -f $p.Label) (Get-Mean $rows "Contact") $p.C
    Add-Diff ("pair {0} enemy" -f $p.Label) (Get-Mean $rows "Enemy") $p.En
    Add-Diff ("pair {0} eff16" -f $p.Label) (Get-Mean $rows "Eff16") $p.Eff
}

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("Вердикт «писать кернел» в ``F5-accepted-vs-visited.md`` не принят, см. ADR-052 §1.1.")
[void]$md.AppendLine("")
[void]$md.AppendLine("Оговорка: все слоты считаются одинаково дорогими, порядок слотов внутри ячейки в модели по перемешиванию, на GPU по атомикам. Это модельная оценка, не замер. Нити волны — подряд идущие слоты в порядке ячеек. ``eff16 / visited-128`` — отношение средних по сидам к среднему ``eff16`` visited-128 той же плотности и того же кадра.")
[void]$md.AppendLine("")
[void]$md.AppendLine("Модель ``boids_model.cpp``, ``perTeam 1500``, ``dt = 0.016666667``, ``mult = 4``, ``T = 6``, сиды 1..5. ``discR`` плотности (2) = ``$discLiteral``.")
[void]$md.AppendLine("")

function Append-WaveTable([string]$title, [string]$tLabel) {
    [void]$md.AppendLine("## $title")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| плотность | режим | кап | потолок | контакт | чужие | слоты среднее | eff8 | eff16 | eff32 | eff16 / visited-128 |")
    [void]$md.AppendLine("| --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |")
    foreach ($d in @(1, 2, 3)) {
        $base = Get-Rows $d 0 128 0 $tLabel
        $baseEff = Get-Mean $base "Eff16"
        foreach ($cfg in $configs) {
            $rows = Get-Rows $d $cfg.Mode $cfg.Cap $cfg.MaxVis $tLabel
            $rule = "visited"
            if ($cfg.Mode -eq 1) { $rule = "accepted" }
            $ratio = (Get-Mean $rows "Eff16") / $baseEff
            [void]$md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} |" -f `
                $d, $rule, $cfg.Cap, $cfg.MaxVis, `
                (Format-Spread $rows "Contact" "0.00"), (Format-Spread $rows "Enemy" "0.00"), `
                (Format-Mean $rows "VisMean" "0.00"), (Format-Mean $rows "Eff8" "0.00"), `
                (Format-Mean $rows "Eff16" "0.00"), (Format-Mean $rows "Eff32" "0.00"), `
                $ratio.ToString("0.000", $inv)))
        }
    }
    [void]$md.AppendLine("")
}

Append-WaveTable "t = 5 с" "5.00"
Append-WaveTable "t = 1 с" "1.00"

[void]$md.AppendLine("## Пары равного поведения, плотность 1, t = 5 с")
[void]$md.AppendLine("")
[void]$md.AppendLine("| пара | контакт | чужие | eff16 |")
[void]$md.AppendLine("| --- | ---: | ---: | ---: |")
for ($i = 0; $i -lt $pairs.Count; $i += 2) {
    $left = $pairs[$i]
    $right = $pairs[$i + 1]
    foreach ($p in @($left, $right)) {
        $rows = Get-Rows 1 $p.Mode $p.Cap $p.MaxVis "5.00"
        [void]$md.AppendLine(("| {0} | {1} | {2} | {3} |" -f $p.Label, `
            (Format-Mean $rows "Contact" "0.00"), (Format-Mean $rows "Enemy" "0.00"), (Format-Mean $rows "Eff16" "0.00")))
    }
}
[void]$md.AppendLine("")
[void]$md.AppendLine("Разница цены волны accepted-48/192 к visited-128 на t = 5, после округления каждого отношения до целого:")
foreach ($d in @(1, 2, 3)) {
    [void]$md.AppendLine(("- плотность {0}: {1}" -f $d, $pctNote[$d]))
}
[void]$md.AppendLine("")
if ($t6miss -eq 0) {
    [void]$md.AppendLine("Сид 1, все 11 конфигураций и три плотности: строки ``#f5 t=`` и ``#f5 wave`` на t = 1 и t = 5 при ``T = 6`` и ``T = 20`` совпали побайтово.")
} else {
    [void]$md.AppendLine("Сид 1: строки ``T = 6`` и ``T = 20`` различаются.")
}
[void]$md.AppendLine("")
if ($fail.Count -eq 0) {
    [void]$md.AppendLine("Гейт: пройден.")
} else {
    [void]$md.AppendLine("Гейт: остановка. Расхождения:")
    [void]$md.AppendLine("")
    foreach ($line in $fail) { [void]$md.AppendLine("- $line") }
}
$mdPath = Join-Path $root "F5-wave-proxy.md"
[IO.File]::WriteAllText($mdPath, $md.ToString(), (New-Object System.Text.UTF8Encoding $true))
if ($fail.Count -gt 0) { throw ("gate failed: {0}" -f $fail.Count) }
Write-Host "gate ok"
