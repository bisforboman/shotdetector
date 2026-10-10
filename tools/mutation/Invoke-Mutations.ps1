#!/usr/bin/env pwsh
# Mutation testing for the guards that reproduce scenedetect's behaviour (tools/mutation/mutations.psd1): each
# mutation breaks one guard, and the listed tests must then fail. Fails when a mutation survives (no test covers that
# guard) or no longer applies (the code changed: update its Find text).
#
#   ./tools/mutation/Invoke-Mutations.ps1 [-Root <checkout>] [-Shard <i> -Shards <n>]
# -Shard/-Shards: only every n-th mutation from the i-th (0-based), for CI's parallel jobs.
# The checkout must have no uncommitted changes to the mutated files: each mutation is undone with 'git checkout'.
# Locally, run it in a separate worktree (git worktree add ../ShotDetector-mut HEAD) so builds don't touch your copy.
param([string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent), [int]$Shard = 0, [int]$Shards = 1)
$ErrorActionPreference = 'Stop'
$config = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'mutations.psd1')
$tests = Join-Path $Root 'tests/ShotDetector.Tests'

dotnet build $tests -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The unmutated build fails.' }

$mine = @(for ($i = $Shard; $i -lt $config.Mutations.Count; $i += $Shards) { $config.Mutations[$i] })
$results = foreach ($m in $mine) {
    $file = Join-Path $Root $m.File
    $text = [IO.File]::ReadAllText($file)
    $count = ([regex]::Matches($text, [regex]::Escape($m.Find))).Count
    if ($count -ne 1) {
        [pscustomobject]@{ Result = 'STALE'; File = $m.File; Find = $m.Find; Note = "found $count times" }
        continue
    }

    [IO.File]::WriteAllText($file, $text.Replace($m.Find, $m.Replace))
    try {
        $out = dotnet test $tests -nologo -v q --filter "FullyQualifiedName~$($m.Tests)" 2>&1
        $result = if ($LASTEXITCODE -ne 0) { if ($out -match ' error CS') { 'KILLED (build)' } else { 'KILLED' } } else { 'SURVIVED' }
    }
    finally {
        git -C $Root checkout -q -- $m.File
    }

    [pscustomobject]@{ Result = $result; File = $m.File; Find = $m.Find; Note = $m.Tests }
}

$results | Format-Table -AutoSize -Wrap | Out-Host
if ($env:GITHUB_STEP_SUMMARY) {
    '### Mutation testing', '', '| Result | File | Guard |', '|---|---|---|' | Add-Content $env:GITHUB_STEP_SUMMARY
    $results | ForEach-Object { "| $($_.Result) | $($_.File) | ``$($_.Find)`` |" } | Add-Content $env:GITHUB_STEP_SUMMARY
}

$bad = @($results | Where-Object { $_.Result -in 'SURVIVED', 'STALE' })
if ($bad.Count) { throw "$($bad.Count) mutation(s) survived or no longer apply." }
Write-Host "All $($results.Count) mutations were caught." -ForegroundColor Green
