#Requires -Version 5.1
# F5.0 matrix: three spawn densities, visited caps and accepted caps with a visit ceiling.
$ErrorActionPreference = "Stop"
$inv = [Globalization.CultureInfo]::InvariantCulture

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "boids_model.cpp"
$exe = Join-Path $env:TEMP "boids_model_f5.exe"
$logDir = Join-Path $env:TEMP "f5-matrix"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$gpp = (Get-Command g++ -ErrorAction Stop).Source
& $gpp -O2 -std=c++17 -o $exe $src
if ($LASTEXITCODE -ne 0) { throw "g++ failed: $LASTEXITCODE" }

$discRx4 = [Math]::Sqrt(1500.0 * 128.0 * 128.0 / 100000.0)
$densities = @(
    [pscustomobject]@{ Id = 1; Name = "default"; DiscR = [double]12 },
    [pscustomobject]@{ Id = 2; Name = "x4"; DiscR = $discRx4 },
    [pscustomobject]@{ Id = 3; Name = "stress"; DiscR = [double]6 }
)

$work = New-Object System.Collections.Generic.List[object]
foreach ($d in $densities) {
    foreach ($cap in @(32, 48, 64, 128)) {
        foreach ($seed in 1..5) {
            $work.Add([pscustomobject]@{ Density = $d; Mode = 0; Cap = $cap; MaxVis = 0; Seed = $seed })
        }
    }
    foreach ($cap in @(16, 32, 48, 64)) {
        foreach ($maxVis in @(0, 96, 128, 160, 192)) {
            foreach ($seed in 1..5) {
                $work.Add([pscustomobject]@{ Density = $d; Mode = 1; Cap = $cap; MaxVis = $maxVis; Seed = $seed })
            }
        }
    }
}

function Parse-F5Log([string]$path, $item) {
    $rows = @()
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^#f5 t=([0-9.]+) contact=([0-9.]+) enemy=([0-9.]+) flocks=(\d+),(\d+) visMean=([0-9.]+) visP95=(\d+) visMax=(\d+) accMean=([0-9.]+) stopCeil=([0-9.]+) stopAcc=([0-9.]+) stopVis=([0-9.]+)') {
            $rows += [pscustomobject]@{
                Density = $item.Density.Id; DiscR = $item.Density.DiscR; PerTeam = 1500
                Mode = $item.Mode; Cap = $item.Cap; MaxVis = $item.MaxVis; Seed = $item.Seed
                T = $Matches[1]; Contact = [double]$Matches[2]; Enemy = [double]$Matches[3]
                F0 = [int]$Matches[4]; F1 = [int]$Matches[5]
                VisMean = [double]$Matches[6]; VisP95 = [int]$Matches[7]; VisMax = [int]$Matches[8]
                AccMean = [double]$Matches[9]
                StopCeil = [double]$Matches[10]; StopAcc = [double]$Matches[11]; StopVis = [double]$Matches[12]
            }
        } elseif ($line -match '^#f5 run visMean=([0-9.]+) visP95=(\d+) visMax=(\d+)') {
            $rows += [pscustomobject]@{
                Density = $item.Density.Id; DiscR = $item.Density.DiscR; PerTeam = 1500
                Mode = $item.Mode; Cap = $item.Cap; MaxVis = $item.MaxVis; Seed = $item.Seed
                T = "run"; Contact = ""; Enemy = ""; F0 = ""; F1 = ""
                VisMean = [double]$Matches[1]; VisP95 = [int]$Matches[2]; VisMax = [int]$Matches[3]
                AccMean = ""; StopCeil = ""; StopAcc = ""; StopVis = ""
            }
        }
    }
    if (@($rows | Where-Object { $_.T -eq "5.00" }).Count -ne 1) {
        throw "no t=5.00 in $path"
    }
    return $rows
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
        $tag = "d{0}_m{1}_c{2}_v{3}_s{4}" -f $item.Density.Id, $item.Mode, $item.Cap, $item.MaxVis, $item.Seed
        $log = Join-Path $logDir ($tag + ".txt")
        $disc = $item.Density.DiscR.ToString("G17", $inv)
        $arg = @("0.016666667", "4", "$($item.Cap)", "$($item.Seed)", "20", "1500", $disc, "$($item.Mode)", "$($item.MaxVis)")
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
            foreach ($row in (Parse-F5Log $r.Log $r.Item)) { $all.Add($row) }
            $done++
            if (($done % 20) -eq 0) { Write-Host ("done {0}/{1}" -f $done, $work.Count) }
        } else {
            $still.Add($r)
        }
    }
    $running = $still
}
Write-Host ("done {0}/{1}" -f $done, $work.Count)

function Format-CsvField($value) {
    if ($null -eq $value) { return "" }
    if ($value -is [string]) { return $value }
    if ($value -is [double] -or $value -is [float] -or $value -is [decimal]) {
        return ([double]$value).ToString("G17", $inv)
    }
    return $value.ToString($inv)
}

$csvPath = Join-Path $root "F5-accepted-vs-visited.csv"
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("density,discR,perTeam,mode,cap,maxVisited,seed,t,contact,enemy,flocks0,flocks1,visMean,visP95,visMax,accMean,stopCeil,stopAcc,stopVis")
foreach ($row in $all) {
    $disc = ([double]$row.DiscR).ToString("G17", $inv)
    $fields = @(
        $row.Density, $disc, $row.PerTeam, $row.Mode, $row.Cap, $row.MaxVis, $row.Seed, $row.T,
        $row.Contact, $row.Enemy, $row.F0, $row.F1, $row.VisMean, $row.VisP95, $row.VisMax,
        $row.AccMean, $row.StopCeil, $row.StopAcc, $row.StopVis
    )
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($field in $fields) { $parts.Add((Format-CsvField $field)) }
    [void]$sb.AppendLine(($parts -join ","))
}
[IO.File]::WriteAllText($csvPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding $true))

function Get-SeedStat($rows, [string]$prop) {
    $vals = @($rows | ForEach-Object { [double]($_.$prop) })
    $min = ($vals | Measure-Object -Minimum).Minimum
    $max = ($vals | Measure-Object -Maximum).Maximum
    $mean = ($vals | Measure-Object -Average).Average
    [pscustomobject]@{ Mean = [double]$mean; Min = [double]$min; Max = [double]$max; Width = [double]($max - $min) }
}

function Get-At($density, $mode, $cap, $maxVis, $t) {
    @($all | Where-Object {
        $_.Density -eq $density -and $_.Mode -eq $mode -and $_.Cap -eq $cap -and $_.MaxVis -eq $maxVis -and $_.T -eq $t
    })
}

function Format-Stat($stat, [string]$fmt) {
    "{0} ({1}–{2})" -f $stat.Mean.ToString($fmt, $inv), $stat.Min.ToString($fmt, $inv), $stat.Max.ToString($fmt, $inv)
}

function Test-Inside($mean, $stat) {
    return ($mean -ge ($stat.Min - 1e-6)) -and ($mean -le ($stat.Max + 1e-6))
}

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# F5.0: кап по принятым против капа по посещённым")
[void]$md.AppendLine("")
[void]$md.AppendLine("Модель ``boids_model.cpp``, ``perTeam 1500``, ``dt = 0.016666667``, ``mult = 4``, ``T = 20``, сиды 1..5. Команды в разных дисках с центрами ±22.")
[void]$md.AppendLine("")
[void]$md.AppendLine("- Плотность (1), ``discR 12``.")
[void]$md.AppendLine(("- Плотность (2), ``discR = sqrt(1500 * 128^2 / 100000) = {0}``. Это плотность одного диска ``100000 / (pi * 128^2)``." -f $discRx4.ToString("G17", $inv)))
[void]$md.AppendLine("- Плотность (3), ``discR 6``.")
[void]$md.AppendLine("")
[void]$md.AppendLine("Порядок слотов внутри ячейки в модели по индексу после перемешивания, на GPU по порядку атомиков (ADR-048 §6). Модель даёт распределения, не побитное совпадение. В режиме 0 останавливающий кандидат в ``visited`` не входит, поэтому максимум равен капу, а не капу+1.")
[void]$md.AppendLine("")
[void]$md.AppendLine("p95 — ранг ``ceil(0.95 * N)`` по возрастанию. Гейт считает средние сидов на ``t = 5``. Кадры ``t = 1`` и ``t = 15``, p95 и максимум приложены и в вердикт не входят. Итог ``run`` — все обходы с ``t >= 1``, не среднее трёх кадров.")
[void]$md.AppendLine("")
[void]$md.AppendLine("Допущение «расхождение только на плотностях (2) и (3) само по себе F5.1 не открывает» — предварительное.")
[void]$md.AppendLine("")

function Append-Table($title, $tLabel) {
    [void]$md.AppendLine("## $title")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| плотность | режим | кап | потолок | контакт | чужие | стаи | слоты среднее | p95 | максимум |")
    [void]$md.AppendLine("| --- | --- | ---: | ---: | --- | --- | --- | --- | --- | --- |")
    foreach ($d in @(1, 2, 3)) {
        $specs = @()
        foreach ($cap in @(32, 48, 64, 128)) { $specs += [pscustomobject]@{ Mode = 0; Cap = $cap; MaxVis = 0 } }
        foreach ($cap in @(16, 32, 48, 64)) {
            foreach ($maxVis in @(0, 96, 128, 160, 192)) {
                $specs += [pscustomobject]@{ Mode = 1; Cap = $cap; MaxVis = $maxVis }
            }
        }
        foreach ($spec in $specs) {
            $rows = Get-At $d $spec.Mode $spec.Cap $spec.MaxVis $tLabel
            if ($rows.Count -eq 0) { continue }
            $c = Get-SeedStat $rows "Contact"
            $e = Get-SeedStat $rows "Enemy"
            $f0 = Get-SeedStat $rows "F0"
            $f1 = Get-SeedStat $rows "F1"
            $v = Get-SeedStat $rows "VisMean"
            $p95 = Get-SeedStat $rows "VisP95"
            $mx = Get-SeedStat $rows "VisMax"
            $flocks = "{0}/{1}" -f (Format-Stat $f0 "0.00"), (Format-Stat $f1 "0.00")
            [void]$md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
                $d, $spec.Mode, $spec.Cap, $spec.MaxVis, `
                (Format-Stat $c "0.00"), (Format-Stat $e "0.00"), $flocks, `
                (Format-Stat $v "0.00"), (Format-Stat $p95 "0.00"), (Format-Stat $mx "0.00")))
        }
    }
    [void]$md.AppendLine("")
}

Append-Table "t = 5 с" "5.00"
Append-Table "t = 1 с" "1.00"
Append-Table "t = 15 с" "15.00"

[void]$md.AppendLine("## Итог прогона, слоты с t >= 1")
[void]$md.AppendLine("")
[void]$md.AppendLine("| плотность | режим | кап | потолок | среднее | p95 | максимум |")
[void]$md.AppendLine("| --- | --- | ---: | ---: | --- | --- | --- |")
foreach ($d in @(1, 2, 3)) {
    $specs = @([pscustomobject]@{ Mode = 0; Cap = 128; MaxVis = 0 })
    foreach ($maxVis in @(0, 96, 128, 160, 192)) {
        $specs += [pscustomobject]@{ Mode = 1; Cap = 48; MaxVis = $maxVis }
    }
    foreach ($spec in $specs) {
        $rows = Get-At $d $spec.Mode $spec.Cap $spec.MaxVis "run"
        $v = Get-SeedStat $rows "VisMean"
        $p95 = Get-SeedStat $rows "VisP95"
        $mx = Get-SeedStat $rows "VisMax"
        [void]$md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f `
            $d, $spec.Mode, $spec.Cap, $spec.MaxVis, `
            (Format-Stat $v "0.00"), (Format-Stat $p95 "0.00"), (Format-Stat $mx "0.00")))
    }
}
[void]$md.AppendLine("")

# Ceiling: density 1, accepted-48, t=5, mean of seeds inside min-max of maxVis 0.
$refRows = Get-At 1 1 48 0 "5.00"
$refC = Get-SeedStat $refRows "Contact"
$refE = Get-SeedStat $refRows "Enemy"
$refF0 = Get-SeedStat $refRows "F0"
$refF1 = Get-SeedStat $refRows "F1"
$chosen = $null
foreach ($maxVis in @(96, 128, 160, 192)) {
    $rows = Get-At 1 1 48 $maxVis "5.00"
    $c = Get-SeedStat $rows "Contact"
    $e = Get-SeedStat $rows "Enemy"
    $f0 = Get-SeedStat $rows "F0"
    $f1 = Get-SeedStat $rows "F1"
    $ok = (Test-Inside $c.Mean $refC) -and (Test-Inside $e.Mean $refE) -and (Test-Inside $f0.Mean $refF0) -and (Test-Inside $f1.Mean $refF1)
    if ($ok) { $chosen = $maxVis; break }
}

[void]$md.AppendLine("## Выбор потолка и гейт")
[void]$md.AppendLine("")
if ($null -eq $chosen) {
    [void]$md.AppendLine("Потолок не выбран: на плотности 1 ни 96, ни 128, ни 160, ни 192 не удержали среднее контакта, доли чужих и обоих чисел стай внутри min–max сидов accepted-48 без потолка. Кернел не рекомендовать. Вопрос владельцу.")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("Вердикт: не писать.")
} else {
    [void]$md.AppendLine(("Выбранный ``maxVisited`` = {0} (наименьший из 96, 128, 160, 192 на плотности 1, accepted-48, t = 5, по среднему сидов)." -f $chosen))
    [void]$md.AppendLine("")
    $acc = Get-At 1 1 48 $chosen "5.00"
    $vis = Get-At 1 0 128 0 "5.00"
    $accC = Get-SeedStat $acc "Contact"
    $accE = Get-SeedStat $acc "Enemy"
    $accV = Get-SeedStat $acc "VisMean"
    $visC = Get-SeedStat $vis "Contact"
    $visE = Get-SeedStat $vis "Enemy"
    $visV = Get-SeedStat $vis "VisMean"
    $aContact = (($visC.Mean - $accC.Mean) -gt (2.0 * $visC.Width)) -and ($accE.Mean -le ($visE.Max + 1e-6))
    $aEnemy = (($visE.Mean - $accE.Mean) -gt (2.0 * $visE.Width)) -and ($accC.Mean -le ($visC.Max + 1e-6))
    $passA = $aContact -or $aEnemy
    $passB = (($visV.Mean - $accV.Mean) -ge (0.10 * $visV.Mean)) -and ($accC.Mean -le ($visC.Max + 1e-6)) -and ($accE.Mean -le ($visE.Max + 1e-6))
    $pass1 = $passA -or $passB
    $pass2 = $true
    $failDense = New-Object System.Collections.Generic.List[int]
    foreach ($d in @(2, 3)) {
        $a2 = Get-At $d 1 48 $chosen "5.00"
        $v2 = Get-At $d 0 128 0 "5.00"
        $a2C = Get-SeedStat $a2 "Contact"
        $a2E = Get-SeedStat $a2 "Enemy"
        $v2C = Get-SeedStat $v2 "Contact"
        $v2E = Get-SeedStat $v2 "Enemy"
        $okC = $a2C.Mean -le ($v2C.Max + 1e-6)
        $okE = $a2E.Mean -le ($v2E.Max + 1e-6)
        if (-not ($okC -and $okE)) { $pass2 = $false; $failDense.Add($d) }
        [void]$md.AppendLine(("Плотность {0}: контакт accepted {1} при верхней границе visited-128 {2}; чужие {3} при верхней границе {4}." -f `
            $d, $a2C.Mean.ToString("0.00", $inv), $v2C.Max.ToString("0.00", $inv), `
            $a2E.Mean.ToString("0.00", $inv), $v2E.Max.ToString("0.00", $inv)))
    }
    [void]$md.AppendLine("")
    [void]$md.AppendLine(("Плотность 1, t = 5. accepted-48/потолок {0}: контакт {1}, чужие {2}, слоты {3}. visited-128: контакт {4} (ширина {5}), чужие {6} (ширина {7}), слоты {8}." -f `
        $chosen, $accC.Mean.ToString("0.00", $inv), $accE.Mean.ToString("0.00", $inv), $accV.Mean.ToString("0.00", $inv), `
        $visC.Mean.ToString("0.00", $inv), $visC.Width.ToString("0.00", $inv), `
        $visE.Mean.ToString("0.00", $inv), $visE.Width.ToString("0.00", $inv), $visV.Mean.ToString("0.00", $inv)))
    [void]$md.AppendLine(("Условие (а): {0}. Условие (б): {1}. Условие 2: {2}." -f $passA, $passB, $pass2))
    [void]$md.AppendLine("")
    if ($pass1 -and $pass2) {
        $which = @()
        if ($passA) { $which += "(а)" }
        if ($passB) { $which += "(б)" }
        [void]$md.AppendLine(("Вердикт: писать кернел. На плотности 1 сработало условие {0}. Плотности 2 и 3 не хуже visited-128." -f ($which -join " и ")))
    } elseif (-not $pass1) {
        [void]$md.AppendLine("Вердикт: не писать. На плотности 1 не выполнено ни (а), ни (б).")
    } else {
        [void]$md.AppendLine(("Вердикт: не писать. Условие 1 выполнено, условие 2 нет на плотностях {0}. Вопрос владельцу." -f ($failDense -join ", ")))
    }
}

$mdPath = Join-Path $root "F5-accepted-vs-visited.md"
[IO.File]::WriteAllText($mdPath, $md.ToString(), (New-Object System.Text.UTF8Encoding $true))
Write-Host "wrote $csvPath"
Write-Host "wrote $mdPath"
