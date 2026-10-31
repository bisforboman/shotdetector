#!/usr/bin/env pwsh
# Mutation testing for the guards that reproduce scenedetect's behaviour (tools/mutation/mutations.psd1): each
# mutation breaks one guard, and the listed tests must then fail. Fails when a mutation survives (no test covers that
# guard) or no longer applies (the code changed: update its Find text).
#
#   ./tools/mutation/Invoke-Mutations.ps1 [-Root <checkout>] [-Shard <i> -Shards <n>] [-OnlyFiles <files> [-Base <ref>]] [-Count]
# -Shard/-Shards: only every n-th selected mutation from the i-th (0-based), for CI's parallel jobs.
# -OnlyFiles: a pull request's changed files (from the root); only the mutations they concern run: those whose File is
#   one of them, whose test class (Tests up to its first '.') one of the changed test files declares, and, with -Base, the
#   entries mutations.psd1 has that its version at that git ref doesn't. Without it, every mutation runs (the nightly).
# -Count: print how many mutations are selected and stop, without building.
# Every entry's Find is checked first, selected or not (a stale entry fails the run), which needs no build.
# The checkout must have no uncommitted changes to the mutated files: each mutation is undone with 'git checkout'.
# Locally, run it in a separate worktree (git worktree add ../ShotDetector-mut HEAD) so builds don't touch your copy.
param(
    [string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [int]$Shard = 0,
    [int]$Shards = 1,
    [string[]]$OnlyFiles,
    [string]$Base,
    [switch]$Count)
$ErrorActionPreference = 'Stop'
$config = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'mutations.psd1')
# Each mutation's tests run in its Project (a test project's folder, from the root), ShotDetector.Tests by default.
$default = 'tests/ShotDetector.Tests'

function Key($m) { "$($m.File)`n$($m.Find)`n$($m.Replace)`n$($m.Tests)`n$($m.Project)" }

# Every Find must still be in its File exactly once, whichever mutations run.
$staleKeys = @{}
$stale = @(foreach ($m in $config.Mutations) {
        $file = Join-Path $Root $m.File
        $found = if (Test-Path $file) { ([regex]::Matches([IO.File]::ReadAllText($file), [regex]::Escape($m.Find))).Count } else { 0 }
        if ($found -ne 1) {
            $staleKeys[(Key $m)] = $true
            [pscustomobject]@{ Result = 'STALE'; File = $m.File; Find = $m.Find; Note = "found $found times" }
        }
    })

$selected = @($config.Mutations)
if ($PSBoundParameters.ContainsKey('OnlyFiles')) {
    # One string from the command line (pwsh -File) or several: split at commas and white space.
    $changed = @($OnlyFiles -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { $_.Replace('\', '/') })
    # The test classes the changed test files declare.
    $classes = @{}
    foreach ($f in $changed | Where-Object { $_ -like 'tests/*.cs' }) {
        $path = Join-Path $Root $f
        if (Test-Path $path) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($path), '\bclass\s+(\w+)')) { $classes[$match.Groups[1].Value] = $true }
        }
    }

    # The entries this pull request added or changed in mutations.psd1.
    $new = @{}
    if ($Base -and $changed -contains 'tools/mutation/mutations.psd1') {
        $temp = New-TemporaryFile
        try {
            git -C $Root show "${Base}:tools/mutation/mutations.psd1" | Set-Content $temp
            $old = @{}
            if ($LASTEXITCODE -eq 0) { foreach ($m in (Import-PowerShellDataFile $temp).Mutations) { $old[(Key $m)] = $true } }
            foreach ($m in $config.Mutations) { if (-not $old[(Key $m)]) { $new[(Key $m)] = $true } }
        }
        finally {
            Remove-Item $temp
        }
    }

    $selected = @($config.Mutations | Where-Object { $changed -contains $_.File -or $classes[$_.Tests.Split('.')[0]] -or $new[(Key $_)] })
}

if ($Count) {
    if ($stale.Count) { $stale | Format-Table -AutoSize -Wrap | Out-Host; throw "$($stale.Count) mutation(s) no longer apply." }
    $selected.Count
    return
}

$mine = @(for ($i = $Shard; $i -lt $selected.Count; $i += $Shards) { $selected[$i] })
if ($mine.Count) {
    foreach ($project in @($mine | ForEach-Object { if ($_.Project) { $_.Project } else { $default } } | Sort-Object -Unique)) {
        dotnet build (Join-Path $Root $project) -nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The unmutated build of $project fails." }
    }
}

$results = $stale + @(foreach ($m in $mine | Where-Object { -not $staleKeys[(Key $_)] }) {
        $file = Join-Path $Root $m.File
        $text = [IO.File]::ReadAllText($file)
        [IO.File]::WriteAllText($file, $text.Replace($m.Find, $m.Replace))
        try {
            $tests = Join-Path $Root $(if ($m.Project) { $m.Project } else { $default })
            $out = dotnet test $tests -nologo -v q --filter "FullyQualifiedName~$($m.Tests)" 2>&1
            $result = if ($LASTEXITCODE -ne 0) { if ($out -match ' error CS') { 'KILLED (build)' } else { 'KILLED' } } else { 'SURVIVED' }
        }
        finally {
            git -C $Root checkout -q -- $m.File
        }

        [pscustomobject]@{ Result = $result; File = $m.File; Find = $m.Find; Note = $m.Tests }
    })

$results | Format-Table -AutoSize -Wrap | Out-Host
if ($env:GITHUB_STEP_SUMMARY) {
    "### Mutation testing ($($mine.Count) of $($config.Mutations.Count) mutations)", '', '| Result | File | Guard |', '|---|---|---|' | Add-Content $env:GITHUB_STEP_SUMMARY
    $results | ForEach-Object { "| $($_.Result) | $($_.File) | ``$($_.Find)`` |" } | Add-Content $env:GITHUB_STEP_SUMMARY
}

$bad = @($results | Where-Object { $_.Result -in 'SURVIVED', 'STALE' })
if ($bad.Count) { throw "$($bad.Count) mutation(s) survived or no longer apply." }
Write-Host "All $($mine.Count) mutations were caught." -ForegroundColor Green
