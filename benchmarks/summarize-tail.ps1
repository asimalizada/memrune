param(
    [string[]]$Sources = @('quota.csv', 'client4.csv', 'client8.csv', 'client8server8.csv',
        'long2.csv', 'long4.csv', 'long8client2.csv', 'long8client8.csv', 'long4client8.csv',
        'clientcgroup2.csv', 'clientcgroup8.csv', 'clientcgroup2server2.csv'),
    [string]$OutputName = 'summary.csv'
)

$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot 'results/m5b'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Median($rows, [string]$property) {
    $values = @($rows | ForEach-Object {
        [double]::Parse($_.$property, $invariant)
    } | Sort-Object)
    $middle = [int][math]::Floor($values.Count / 2)
    if ($values.Count % 2) { return $values[$middle] }
    return ($values[$middle - 1] + $values[$middle]) / 2
}

function Number([double]$value) { return $value.ToString('R', $invariant) }

$runs = foreach ($source in $Sources) {
    Import-Csv (Join-Path $directory $source) |
        Select-Object *, @{ Name = 'series'; Expression = { [IO.Path]::GetFileNameWithoutExtension($source) } }
}
$summary = $runs | Group-Object series, server_cpus, client_cpus, mode, target, clients, pipeline, operations |
    ForEach-Object {
        $group = $_.Group
        [pscustomobject]@{
            series = $group[0].series
            server_cpus = $group[0].server_cpus
            client_cpus = $group[0].client_cpus
            mode = $group[0].mode
            target = $group[0].target
            clients = $group[0].clients
            pipeline = $group[0].pipeline
            operations_per_repetition = $group[0].operations
            repetitions = $group.Count
            median_ops_per_second = Number (Median $group 'ops_per_second')
            median_p50_us = Number (Median $group 'p50_us')
            median_p95_us = Number (Median $group 'p95_us')
            median_p99_us = Number (Median $group 'p99_us')
            median_client_cpu_seconds_measured = Number (Median $group 'client_cpu_seconds_measured')
            median_server_cpu_seconds_inclusive = Number (Median $group 'server_cpu_seconds_inclusive')
            total_throttled_periods_inclusive = ($group | ForEach-Object { [long]$_.server_throttled_periods_inclusive } | Measure-Object -Sum).Sum
            total_throttled_seconds_inclusive = Number (($group | ForEach-Object { [double]::Parse($_.server_throttled_seconds_inclusive, $invariant) } | Measure-Object -Sum).Sum)
            total_successful_operations = ($group | ForEach-Object { [long]$_.successful } | Measure-Object -Sum).Sum
            total_failed_operations = ($group | ForEach-Object { [long]$_.failed } | Measure-Object -Sum).Sum
        }
    }
$path = Join-Path $directory $OutputName
$summary | Export-Csv -Path $path -NoTypeInformation
Write-Host "Wrote $path ($($summary.Count) configurations, $($runs.Count) individual runs)."
