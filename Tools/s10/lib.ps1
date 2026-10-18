# Shared helpers for the S10 measurement scripts. PowerShell 5.1.
$script:M3dToolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-M3dRepoRoot {
    return (Resolve-Path (Join-Path $script:M3dToolsRoot "..\..")).Path
}

function Get-M3dOutDir {
    $dir = Join-Path $script:M3dToolsRoot "out"
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    return $dir
}

function Write-M3dLine([string]$Text) {
    Write-Output $Text
}

function Resolve-M3dAdb {
    param([string]$Adb)
    if ($Adb) {
        if (-not (Test-Path $Adb)) { throw "adb не найден: $Adb" }
        return (Resolve-Path $Adb).Path
    }
    if ($env:M3D_ADB) {
        if (-not (Test-Path $env:M3D_ADB)) { throw "M3D_ADB не найден: $($env:M3D_ADB)" }
        return (Resolve-Path $env:M3D_ADB).Path
    }
    $local = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
    if (Test-Path $local) { return (Resolve-Path $local).Path }
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if (-not $cmd) { $cmd = Get-Command adb -ErrorAction SilentlyContinue }
    if ($cmd) { return $cmd.Source }
    throw "adb не найден. Задайте -Adb или M3D_ADB, либо положите platform-tools в LOCALAPPDATA."
}

function Resolve-M3dStreamline {
    param([string]$Streamline)
    if ($Streamline) {
        if (-not (Test-Path $Streamline)) { throw "Streamline-cli не найден: $Streamline" }
        return (Resolve-Path $Streamline).Path
    }
    if ($env:M3D_STREAMLINE) {
        if (-not (Test-Path $env:M3D_STREAMLINE)) { throw "M3D_STREAMLINE не найден: $($env:M3D_STREAMLINE)" }
        return (Resolve-Path $env:M3D_STREAMLINE).Path
    }
    $cmd = Get-Command Streamline-cli.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "Streamline-cli.exe не найден. Задайте -Streamline или M3D_STREAMLINE, либо добавьте его в PATH."
}

function Invoke-M3d {
    param([string]$File, [string[]]$ArgumentList)
    $quoted = @()
    foreach ($arg in $ArgumentList) {
        if ($null -eq $arg) { continue }
        if ($arg -match '[\s"]') { $quoted += ('"' + ($arg -replace '"', '\"') + '"') }
        else { $quoted += $arg }
    }
    $outFile = Join-Path (Get-M3dOutDir) ("m3d-" + [guid]::NewGuid().ToString("N") + ".tmp")
    $errFile = "$outFile.err"
    try {
        $proc = Start-Process -FilePath $File -ArgumentList ($quoted -join " ") -Wait -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $text = ""
        if (Test-Path $outFile) { $text += [IO.File]::ReadAllText($outFile) }
        if (Test-Path $errFile) { $text += [IO.File]::ReadAllText($errFile) }
        return [pscustomobject]@{ Code = $proc.ExitCode; Text = $text }
    } finally {
        Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
    }
}

function Read-M3dSharedText([string]$Path) {
    if (-not (Test-Path $Path)) { return "" }
    $fs = New-Object System.IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $sr = New-Object System.IO.StreamReader($fs)
        return $sr.ReadToEnd()
    } finally {
        if ($sr) { $sr.Dispose() } else { $fs.Dispose() }
    }
}

function Get-M3dFields([string]$Line) {
    $map = @{}
    $idx = $Line.IndexOf("M3D_PERF")
    $msg = $Line
    if ($idx -ge 0) { $msg = $Line.Substring($idx) }
    foreach ($m in [regex]::Matches($msg, '(?:^|\s)([A-Za-z]+)=(\S+)')) {
        $map[$m.Groups[1].Value] = $m.Groups[2].Value
    }
    return $map
}

function Read-M3dPerfText([string]$Text) {
    $runs = @()
    $info = $null
    $warns = 0
    $done = $null
    $lines = @()
    foreach ($raw in ($Text -split "`r?`n")) {
        if ($raw -notmatch "M3D_PERF") { continue }
        $lines += $raw
        if ($raw -match "M3D_PERF_WARN") { $warns++ ; continue }
        if ($raw -match "M3D_PERF_INFO") {
            if ($info -or $done) {
                $runs += [pscustomobject]@{ Info = $info; Warns = $warns; Done = $done }
                $warns = 0
                $done = $null
            }
            $info = Get-M3dFields $raw
            continue
        }
        $fields = Get-M3dFields $raw
        if ($fields["phase"] -eq "DONE") { $done = $fields }
    }
    if ($info -or $done -or $warns -gt 0) {
        $runs += [pscustomobject]@{ Info = $info; Warns = $warns; Done = $done }
    }
    return $runs
}

function Get-M3dLenientReasons($Run) {
    $reasons = @()
    $info = $Run.Info
    if ($info) {
        if ($info.ContainsKey("applied") -and $info["applied"] -eq "0") { $reasons += "applied=0" }
        if ($info.ContainsKey("devbuild") -and $info["devbuild"] -eq "0") { $reasons += "devbuild=0" }
        if ($info.ContainsKey("powersave") -and $info["powersave"] -eq "1") { $reasons += "powersave=1" }
        if ($info.ContainsKey("build") -and $info["build"] -eq "na") { $reasons += "build=na" }
    }
    if ($Run.Warns -gt 0) { $reasons += "M3D_PERF_WARN" }
    return $reasons
}

function Get-M3dMode([hashtable]$Done) {
    if (-not $Done) { return "" }
    $force = $Done["force"]
    $render = $Done["render"]
    if ($force -eq "1" -and $render -eq "1") { return "A" }
    if ($force -eq "1" -and $render -eq "0") { return "B" }
    if ($force -eq "0" -and $render -eq "1") { return "C" }
    return ""
}

function Get-M3dShortSide([string]$Res) {
    if ($Res -notmatch '^(\d+)x(\d+)$') { return "na" }
    $a = [int]$Matches[1]
    $b = [int]$Matches[2]
    return [string]([Math]::Min($a, $b))
}

function Get-M3dNumber([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $t = $Text.Trim()
    $n = 0.0
    if ([double]::TryParse($t, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$n)) {
        return $n
    }
    return $null
}

function Get-M3dMedian([double[]]$Values) {
    $s = @($Values | Sort-Object)
    $n = $s.Count
    if ($n -eq 0) { return $null }
    if ($n % 2 -eq 1) { return [double]$s[[int][Math]::Floor($n / 2)] }
    return ([double]$s[$n / 2 - 1] + [double]$s[$n / 2]) / 2.0
}

function Convert-M3dCsvNumber([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return 0.0 }
    $t = $Value.Trim().Trim('"').Replace(" ", "")
    if ($t.Contains(",") -and -not $t.Contains(".")) { $t = $t.Replace(",", ".") }
    return [double]::Parse($t, [Globalization.CultureInfo]::InvariantCulture)
}

function Read-M3dTimeline([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    $range = [regex]::Match($text, 'Range:\s*([0-9]+(?:[.,][0-9]+)?)\s+seconds to\s+([0-9]+(?:[.,][0-9]+)?)\s+seconds')
    $duration = $null
    if ($range.Success) {
        $duration = (Convert-M3dCsvNumber $range.Groups[2].Value) - (Convert-M3dCsvNumber $range.Groups[1].Value)
    }
    $headerAt = $text.IndexOf("Index (s),")
    if ($headerAt -lt 0) { throw "В $Path нет шапки Index (s)" }
    $slice = $text.Substring($headerAt)
    Add-Type -AssemblyName Microsoft.VisualBasic -ErrorAction SilentlyContinue
    $reader = New-Object System.IO.StringReader($slice)
    $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($reader)
    $parser.SetDelimiters(",")
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false
    $header = $parser.ReadFields()
    $rows = New-Object System.Collections.Generic.List[object]
    while (-not $parser.EndOfData) {
        $fields = $parser.ReadFields()
        if ($fields -and $fields.Length -gt 0 -and -not [string]::IsNullOrWhiteSpace($fields[0])) {
            $rows.Add($fields)
        }
    }
    $parser.Close()
    if (-not $duration -or $duration -le 0) { $duration = $rows.Count / 100.0 }
    return [pscustomobject]@{ Header = $header; Rows = $rows; DurationSec = [double]$duration }
}

function Get-M3dColumn([string[]]$Header, [string]$Name) {
    for ($i = 0; $i -lt $Header.Count; $i++) {
        if ($Header[$i] -eq $Name) { return $i }
    }
    return -1
}

function Get-M3dColumnSum($Timeline, [string]$Name) {
    $idx = Get-M3dColumn $Timeline.Header $Name
    if ($idx -lt 0) { return $null }
    $sum = 0.0
    foreach ($row in $Timeline.Rows) {
        if ($idx -ge $row.Length) { continue }
        $sum += Convert-M3dCsvNumber $row[$idx]
    }
    return $sum
}

function Test-M3dApcName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return "имя захвата пустое" }
    if ($Name -match '[\\/]' -or $Name.Contains("..")) { return "имя захвата содержит путь" }
    if ($Name -notmatch '\.apc$') { return "имя захвата должно заканчиваться на .apc" }
    return ""
}

function Get-M3dApcPath([string]$ApcName) {
    $reason = Test-M3dApcName $ApcName
    if ($reason) { throw $reason }
    $docs = [Environment]::GetFolderPath("MyDocuments")
    $dir = Join-Path $docs "Streamline"
    if (-not (Test-Path $dir)) { throw "Нет папки захватов: $dir" }
    return (Join-Path $dir $ApcName)
}

function Write-M3dCmd([string]$Body) {
    $path = Join-Path (Get-M3dOutDir) ("m3d-" + [guid]::NewGuid().ToString("N") + ".tmp.cmd")
    [IO.File]::WriteAllText($path, $Body, [Text.Encoding]::ASCII)
    return $path
}
