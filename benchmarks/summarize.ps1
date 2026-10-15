param(
    [string]$InputSubdirectory = '',
    [string]$OutputSubdirectory = '',
    [int]$ExpectedConfigurations = 30,
    [int]$Repetitions = 3
)

$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot "results/$OutputSubdirectory"
$raw = Join-Path $PSScriptRoot "results/raw/$InputSubdirectory"
$expectedFiles = $ExpectedConfigurations * 2
New-Item -ItemType Directory -Force -Path $directory | Out-Null
if ((Test-Path (Join-Path $directory 'runs.csv')) -or
    (Test-Path (Join-Path $directory 'summary.csv'))) {
    throw "Curated results already exist in $directory. Choose a fresh -OutputSubdirectory."
}
$files = @(Get-ChildItem -Path $raw -Filter '*.json' |
    Where-Object { $_.Name -match '^(memrune|redis)-(ping|set|get)-v(0|32|1024)-c(1|50|100)-p(1|16)\.json$' })
if ($files.Count -ne $expectedFiles) { throw "Expected $expectedFiles result files; found $($files.Count)." }
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

$runs = foreach ($file in $files) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($run in $report.runs) {
        [pscustomobject]@{
            target = $report.host
            workload = $report.workload
            value_bytes = $report.valueSize
            clients = $report.clients
            pipeline = $report.pipelineDepth
            repetition = $run.repetition
            total_ops = $run.totalOperations
            success_ops = $run.successfulOperations
            failed_ops = $run.failedOperations
            elapsed_seconds = ([double]$run.elapsedSeconds).ToString('R', $invariant)
            successful_ops_per_second = ([double]$run.successfulOpsPerSecond).ToString('R', $invariant)
            p50_us = ([double]$run.p50Microseconds).ToString('R', $invariant)
            p95_us = ([double]$run.p95Microseconds).ToString('R', $invariant)
            p99_us = ([double]$run.p99Microseconds).ToString('R', $invariant)
            valid = $run.valid
            error = $run.error
        }
    }
}
$runs = @($runs | Sort-Object workload, value_bytes, clients, pipeline, target, repetition)
if ($runs.Count -ne $expectedFiles * $Repetitions) {
    throw "Expected $($expectedFiles * $Repetitions) individual runs; found $($runs.Count)."
}
$invalid = @($runs | Where-Object { -not $_.valid -or $_.failed_ops -ne 0 })
if ($invalid.Count -gt 0) { throw "$($invalid.Count) benchmark runs are invalid or have failed operations." }
$runs | Export-Csv -NoTypeInformation -Path (Join-Path $directory 'runs.csv')

$summary = foreach ($file in $files) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{
        target = $report.host
        workload = $report.workload
        value_bytes = $report.valueSize
        clients = $report.clients
        pipeline = $report.pipelineDepth
        repetitions = $report.repetitions
        total_failed_ops = ($report.runs | Measure-Object -Property failedOperations -Sum).Sum
        valid_runs = @($report.runs | Where-Object valid).Count
        median_successful_ops_per_second = ([double]$report.median.successfulOpsPerSecond).ToString('R', $invariant)
        median_p50_us = ([double]$report.median.p50Microseconds).ToString('R', $invariant)
        median_p95_us = ([double]$report.median.p95Microseconds).ToString('R', $invariant)
        median_p99_us = ([double]$report.median.p99Microseconds).ToString('R', $invariant)
    }
}
$summary = @($summary | Sort-Object workload, value_bytes, clients, pipeline, target)
$summary | Export-Csv -NoTypeInformation -Path (Join-Path $directory 'summary.csv')
Write-Host "Wrote $($runs.Count) runs and $($summary.Count) summaries to $directory"
