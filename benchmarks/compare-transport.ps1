param([string]$FinalSubdirectory = 'final-verified')

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'results'
$m5a = Join-Path $root 'm5a'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Number([string]$value) { [double]::Parse($value, $invariant) }
function Invariant([double]$value) { $value.ToString('R', $invariant) }

$controlled = foreach ($phase in @('baseline', 'nodelay', 'coalesced', 'combined')) {
    $path = Join-Path $m5a "$phase/summary.csv"
    foreach ($row in (Import-Csv $path)) {
        [pscustomobject]@{
            phase = $phase
            target = $row.target
            workload = $row.workload
            value_bytes = $row.value_bytes
            clients = $row.clients
            pipeline = $row.pipeline
            valid_runs = $row.valid_runs
            failed_ops = $row.total_failed_ops
            median_ops_per_second = $row.median_successful_ops_per_second
            median_p50_us = $row.median_p50_us
            median_p95_us = $row.median_p95_us
            median_p99_us = $row.median_p99_us
        }
    }
}
$controlled = @($controlled | Sort-Object workload, value_bytes, clients, pipeline, phase, target)
if ($controlled.Count -ne 48) { throw "Expected 48 controlled comparisons; found $($controlled.Count)." }
$controlled | Export-Csv -NoTypeInformation -Path (Join-Path $m5a 'controlled-comparison.csv')

$original = @(Import-Csv (Join-Path $root 'summary.csv'))
$final = @(Import-Csv (Join-Path $m5a "$FinalSubdirectory/summary.csv"))
if ($original.Count -ne 60 -or $final.Count -ne 60) { throw 'Expected 60 original and 60 final summaries.' }
$comparison = foreach ($after in $final) {
    $before = @($original | Where-Object {
        $_.target -eq $after.target -and $_.workload -eq $after.workload -and
        $_.value_bytes -eq $after.value_bytes -and $_.clients -eq $after.clients -and
        $_.pipeline -eq $after.pipeline
    })
    if ($before.Count -ne 1) { throw "Missing original result for $($after.target) $($after.workload)." }
    $prior = $before[0]
    $oldOps = Number $prior.median_successful_ops_per_second
    $newOps = Number $after.median_successful_ops_per_second
    [pscustomobject]@{
        target = $after.target
        workload = $after.workload
        value_bytes = $after.value_bytes
        clients = $after.clients
        pipeline = $after.pipeline
        original_ops_per_second = $prior.median_successful_ops_per_second
        final_ops_per_second = $after.median_successful_ops_per_second
        throughput_ratio = Invariant ($newOps / $oldOps)
        original_p50_us = $prior.median_p50_us
        final_p50_us = $after.median_p50_us
        original_p95_us = $prior.median_p95_us
        final_p95_us = $after.median_p95_us
        original_p99_us = $prior.median_p99_us
        final_p99_us = $after.median_p99_us
        final_valid_runs = $after.valid_runs
        final_failed_ops = $after.total_failed_ops
    }
}
$comparison = @($comparison | Sort-Object workload, value_bytes, clients, pipeline, target)
$comparison | Export-Csv -NoTypeInformation -Path (Join-Path $m5a 'full-comparison.csv')
Write-Host "Wrote $($controlled.Count) controlled rows and $($comparison.Count) full-matrix rows to $m5a"
