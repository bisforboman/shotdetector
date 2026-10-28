# Times parallel detection above 8 CPUs (docs/backlog.md, "Parallel detection above 8 CPUs"; docs/parallel-detection.md):
# the released shotdetect on one pinned film, in 1 (--sequential), 2, 4, 6 and 8 chunks (the hidden --chunks N), each
# with the default decoder threads and with more (--threads T, the total the chunks share), content detector, rounds
# interleaved. Wall and CPU time, peak memory, and every run's scene CSV checked against the sequential one. Prints a
# Markdown table to paste back (also saved in the current folder as bench-cpus-<date>.md).
#
# Windows PowerShell 5.1 or pwsh 7, no admin rights. Downloads the release's archive and the film into a temp folder and
# deletes it afterwards; -KeepFilm keeps the film (in the temp folder's shotdetector-bench-film) for the next run.
#   powershell -ExecutionPolicy Bypass -File tools\bench-cpus.ps1 [-KeepFilm]
# Testing elsewhere: -Rid linux-x64 (the Linux archive), or -Binary <shotdetect> to skip the download.
[CmdletBinding()]
param(
    [ValidateSet("tears_of_steel_1280x534_h264.mov", "elephants_dream_426x240_h264.mp4", "bbb_640x360_h264.mp4")]
    [string]$Film = "tears_of_steel_1280x534_h264.mov",
    [int[]]$Chunks = @(1, 2, 4, 6, 8),
    # Decoder threads in total, shared out among the chunks; 0 is shotdetect's default. Default: 0, 1.5x and 2x the CPUs.
    [int[]]$Threads = @(),
    [int]$Rounds = 3,
    [string]$Detector = "content",
    [ValidateSet("win-x64", "linux-x64", "linux-arm64", "osx-arm64")]
    [string]$Rid = "win-x64",
    [string]$Binary = "",
    [switch]$KeepFilm
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" # Windows PowerShell 5.1's progress bar slows Invoke-WebRequest many times over
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
$inv = [Globalization.CultureInfo]::InvariantCulture

$version = "3.19.0"
# The archives' SHA-256 as GitHub recorded them at upload (the release's asset digests; it has no sums file).
$archives = @{
    "win-x64"     = @("shotdetect-v$version-win-x64.zip", "dd3c53fea7e427121eed0331782a50a90cbd932e441fbf5f0a19ce9f883f4a8a")
    "linux-x64"   = @("shotdetect-v$version-linux-x64.tar.gz", "6a0eca003ac515990043a4ec3082eea522996c8bf603408282a0d0f30b542baf")
    "linux-arm64" = @("shotdetect-v$version-linux-arm64.tar.gz", "2ffbd132401b4f649bda8f557a3bc87f2a9cc236d47bdfc880a632f430eee292")
    "osx-arm64"   = @("shotdetect-v$version-osx-arm64.tar.gz", "6e35f88852685a988a9877c1ba8aea70e542b7f9e305924dc176b7b72119d651")
}
# The films as tools/realworld.py pins them.
$films = @{
    "tears_of_steel_1280x534_h264.mov" = @("https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov",
        "efa9062d9cdb7a338e40ad530dfdf234806743f29ae6a1a136b97ece4e588e8f")
    "elephants_dream_426x240_h264.mp4" = @("https://archive.org/download/ElephantsDream/ed_1024_512kb.mp4",
        "fd6dc0e9da3a2f63487957325983f197f23c6ff1cd2b29bb71e64db567e0994c")
    "bbb_640x360_h264.mp4" = @("https://archive.org/download/BigBuckBunny_124/Content/big_buck_bunny_720p_surround.mp4",
        "46f62396c755e1ed0ab856a1521378d54196e125ef1a1643a199af087a15046b")
}

function Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

function Download([string]$url, [string]$path, [string]$sha) {
    if ((Test-Path -LiteralPath $path) -and (Sha256 $path) -eq $sha) {
        Write-Host "cached: $path"
        return
    }
    for ($try = 1; ; $try++) {
        Write-Host "downloading $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing -UserAgent "ShotDetector bench-cpus"
            break
        }
        catch {
            if ($try -ge 4) { throw }
            Write-Host "  failed ($($_.Exception.Message)), retrying"
            Start-Sleep -Seconds (2 * $try)
        }
    }
    $actual = Sha256 $path
    if ($actual -ne $sha) {
        Remove-Item -LiteralPath $path
        throw "$path has SHA-256 $actual, expected $sha"
    }
    Write-Host "  SHA-256 $actual (as pinned)"
}

function Median([double[]]$values) {
    $s = @($values | Sort-Object)
    if ($s.Count % 2) { return $s[($s.Count - 1) / 2] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}

function Fmt([string]$format, [object[]]$values) { [string]::Format($inv, $format, $values) }

# One shotdetect run: wall time, CPU time (the process's own, which with in-process decoding is all of it), peak
# working set, and the chunk count it reports ("decoded in-process in N chunks" on stderr).
function Run([string]$exe, [string[]]$arguments) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join " "
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    # Sampled while it runs as well: .NET on Linux can't read an exited process's CPU time (Windows can, exactly).
    $peak = 0L
    $cpu = 0.0
    while (-not $p.WaitForExit(100)) {
        try {
            $p.Refresh()
            if ($p.PeakWorkingSet64 -gt $peak) { $peak = $p.PeakWorkingSet64 }
            $cpu = $p.TotalProcessorTime.TotalSeconds
        }
        catch { }
    }
    $p.WaitForExit()
    $wall = $watch.Elapsed.TotalSeconds
    try { $final = $p.TotalProcessorTime.TotalSeconds; if ($final -gt $cpu) { $cpu = $final } } catch { }
    $stderr = $err.Result
    [void]$out.Result
    if ($p.ExitCode -ne 0) { throw "shotdetect $($psi.Arguments) exited with $($p.ExitCode):`n$stderr" }
    $ran = 1
    if ($stderr -match ' in (\d+) chunks') { $ran = [int]$Matches[1] }
    $warning = @($stderr -split "`r?`n" | Where-Object { $_ -match 'sequentially' }) | Select-Object -First 1
    return [pscustomobject]@{ Wall = $wall; Cpu = $cpu; PeakMB = $peak / 1MB; Ran = $ran; Warning = $warning }
}

$cpus = [Environment]::ProcessorCount
if ($Threads.Count -eq 0) { $Threads = @(0, [int][Math]::Round($cpus * 1.5), 2 * $cpus) | Select-Object -Unique }

$tempRoot = [IO.Path]::GetTempPath()
$work = Join-Path $tempRoot ("shotdetector-bench-cpus-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$filmDir = Join-Path $tempRoot "shotdetector-bench-film"
New-Item -ItemType Directory -Force $work, $filmDir | Out-Null
try {
    if ($Binary) {
        $exe = (Resolve-Path -LiteralPath $Binary).Path
    }
    else {
        $name, $sha = $archives[$Rid]
        $archive = Join-Path $work $name
        Download "https://github.com/bisforboman/shotdetector/releases/download/v$version/$name" $archive $sha
        $bin = Join-Path $work "bin"
        New-Item -ItemType Directory -Force $bin | Out-Null
        if ($name.EndsWith(".zip")) { Expand-Archive -LiteralPath $archive -DestinationPath $bin }
        else { tar -xzf $archive -C $bin; if ($LASTEXITCODE) { throw "tar failed on $archive" } }
        $exe = Join-Path $bin "shotdetect"
        if (Test-Path -LiteralPath "$exe.exe") { $exe = "$exe.exe" }
    }
    $url, $filmSha = $films[$Film]
    $video = Join-Path $filmDir $Film
    Download $url $video $filmSha

    $versionLine = (& $exe --version | Out-String).Trim()
    if ($Rid -match '^win-') { $cpuName = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() }
    elseif (Test-Path /proc/cpuinfo) { $cpuName = ((Get-Content /proc/cpuinfo | Where-Object { $_ -match '^model name' } | Select-Object -First 1) -replace '^model name\s*:\s*', '') }
    else { $cpuName = "unknown" }
    if (-not $cpuName) { $cpuName = "unknown" }

    # The cases, sequential with the default threads first: the reference for speed and for the scene CSV.
    $cases = @()
    foreach ($c in $Chunks) {
        foreach ($t in $Threads) {
            $cases += [pscustomobject]@{ Chunks = $c; Threads = $t; Runs = @(); Csv = $true }
        }
    }
    function CaseArgs($case, [string]$csv) {
        $a = @("-i", $video, "-d", $Detector, "-q", "--csv", $csv)
        if ($case.Chunks -le 1) { $a += "--sequential" } else { $a += @("--chunks", "$($case.Chunks)") }
        if ($case.Threads -gt 0) { $a += @("--threads", "$($case.Threads)") }
        return $a
    }

    Write-Host "$versionLine on $cpuName, $cpus logical CPUs; $($cases.Count) cases x $Rounds rounds on $Film"
    $refCsv = Join-Path $work "reference.csv"
    Write-Host "warm-up (sequential, not timed; its scene CSV is the reference)"
    [void](Run $exe (CaseArgs $cases[0] $refCsv))
    $reference = Sha256 $refCsv

    for ($r = 0; $r -lt $Rounds; $r++) {
        # Interleaved, each round starting further along, so a slow spell doesn't fall on one case every round.
        $offset = [int]($r * $cases.Count / $Rounds)
        for ($k = 0; $k -lt $cases.Count; $k++) {
            $case = $cases[($k + $offset) % $cases.Count]
            $csv = Join-Path $work "run.csv"
            $run = Run $exe (CaseArgs $case $csv)
            $case.Runs += $run
            $same = (Sha256 $csv) -eq $reference
            if (-not $same) { $case.Csv = $false }
            Write-Host (Fmt "round {0}: {1} chunk(s), threads {2}: {3:0.00} s wall, {4:0.00} s CPU, ran in {5}{6}" @(
                ($r + 1), $case.Chunks, $(if ($case.Threads) { $case.Threads } else { "default" }), $run.Wall, $run.Cpu, $run.Ran,
                $(if ($same) { "" } else { ", SCENE CSV DIFFERS" })))
        }
    }

    $base = Median ($cases[0].Runs | ForEach-Object { $_.Wall })
    $os = [Environment]::OSVersion.VersionString
    $lines = @(
        "### bench-cpus: $Film, $Detector detector",
        "",
        "$versionLine ($Rid), $cpuName, $cpus logical CPUs, $os, PowerShell $($PSVersionTable.PSVersion); medians of $Rounds interleaved rounds (min-max wall), peak working set.",
        "Default threads: sequential min(CPUs, 16), chunked min(2 x CPUs, 16) in total for these h264 films; --threads T is the total the chunks share.",
        "",
        "| Chunks | Decoder threads (total / per chunk) | Ran in | Wall | CPU | Speed-up | Peak MB | Scene CSV |",
        "|---|---|---|---|---|---|---|---|"
    )
    foreach ($case in $cases) {
        if ($case.Threads -gt 0) { $total = $case.Threads }
        elseif ($case.Chunks -le 1) { $total = [Math]::Min($cpus, 16) }
        else { $total = [Math]::Min(2 * $cpus, 16) }
        $n = [Math]::Max(1, $case.Chunks)
        $per = [Math]::Max(1, [Math]::Floor($total / $n))
        if ($total -gt $n -and $total % $n) { $per = "$per-$($per + 1)" } # the remainder goes to the first chunks
        $walls = @($case.Runs | ForEach-Object { $_.Wall })
        $wall = Median $walls
        $ran = @($case.Runs | ForEach-Object { $_.Ran } | Select-Object -Unique) -join "/"
        $lines += Fmt "| {0} | {1}{2} / {3} | {5} | {6:0.00} s ({7:0.00}-{8:0.00}) | {9:0.00} s | {10:0.00}x | {11:0} | {12} |" @(
            $(if ($case.Chunks -le 1) { "1 (--sequential)" } else { $case.Chunks }),
            $(if ($case.Threads -gt 0) { "" } else { "default " }), $total, $per,
            "",
            $ran, $wall, ($walls | Measure-Object -Minimum).Minimum, ($walls | Measure-Object -Maximum).Maximum,
            (Median ($case.Runs | ForEach-Object { $_.Cpu })), ($base / $wall),
            (Median ($case.Runs | ForEach-Object { $_.PeakMB })),
            $(if ($case.Csv) { "identical" } else { "**DIFFERS**" }))
    }
    $warnings = @($cases | ForEach-Object { $_.Runs } | ForEach-Object { $_.Warning } | Where-Object { $_ } | Select-Object -Unique)
    if ($warnings.Count) { $lines += ""; $lines += $warnings | ForEach-Object { "- $_" } }

    $report = $lines -join [Environment]::NewLine
    $saved = Join-Path (Get-Location) ("bench-cpus-" + (Get-Date -Format "yyyy-MM-dd-HHmm") + ".md")
    [IO.File]::WriteAllText($saved, $report + [Environment]::NewLine)
    Write-Host ""
    Write-Host $report
    Write-Host ""
    Write-Host "Saved as ${saved}: paste the table back."
    if (@($cases | Where-Object { -not $_.Csv }).Count) { exit 1 }
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
    if (-not $KeepFilm) { Remove-Item -Recurse -Force -LiteralPath $filmDir -ErrorAction SilentlyContinue }
}
