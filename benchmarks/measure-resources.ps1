param(
    [ValidateSet('ping', 'set', 'get')][string]$Workload = 'get',
    [int]$ValueSize = 1024,
    [int]$Clients = 100,
    [int]$Operations = 51200,
    [int]$Pipeline = 16,
    [int]$Warmup = 800,
    [int]$Repetitions = 3,
    [int]$Keys = 1024,
    [string]$ResultSubdirectory = '',
    [string]$OutputFile = ''
)

$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot 'compose.yaml'
$results = Join-Path $PSScriptRoot 'results'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$outputDirectory = if ($ResultSubdirectory) { "/results/$ResultSubdirectory" } else { '/results' }
$csvPath = if ($OutputFile) { Join-Path $results $OutputFile } else { Join-Path $results 'resources.csv' }

function Read-Counters([string]$target) {
    $stat = (& docker compose -f $compose exec -T $target cat /proc/1/stat) -split ' '
    if ($LASTEXITCODE -ne 0) { throw "Could not read $target process CPU counters." }
    $cpu = & docker compose -f $compose exec -T $target cat /sys/fs/cgroup/cpu.stat
    if ($LASTEXITCODE -ne 0) { throw "Could not read $target container CPU counters." }
    $usage = ($cpu | Where-Object { $_ -match '^usage_usec ' }) -replace '^usage_usec ', ''
    $throttledPeriods = ($cpu | Where-Object { $_ -match '^nr_throttled ' }) -replace '^nr_throttled ', ''
    $throttledMicroseconds = ($cpu | Where-Object { $_ -match '^throttled_usec ' }) -replace '^throttled_usec ', ''
    $peak = & docker compose -f $compose exec -T $target cat /sys/fs/cgroup/memory.peak
    if ($LASTEXITCODE -ne 0) { throw "Could not read $target container memory peak." }
    [pscustomobject]@{
        process_ticks = [long]$stat[13] + [long]$stat[14]
        container_cpu_microseconds = [long]$usage
        container_throttled_periods = [long]$throttledPeriods
        container_throttled_microseconds = [long]$throttledMicroseconds
        container_memory_peak_bytes = [long]$peak
    }
}

$rows = foreach ($target in @('memrune', 'redis')) {
    $port = if ($target -eq 'memrune') { 6380 } else { 6379 }
    $before = Read-Counters $target
    $name = "resource-$target.json"
    $output = & docker compose -f $compose run --rm benchmark --benchmark `
        --host $target --port $port --workload $Workload --clients $Clients `
        --operations $Operations --pipeline $Pipeline --value-size $ValueSize `
        --warmup $Warmup --repetitions $Repetitions --keys $Keys `
        --output "$outputDirectory/$name"
    if ($LASTEXITCODE -ne 0) { throw "$target resource benchmark failed." }
    Write-Host $output
    $after = Read-Counters $target
    [pscustomobject]@{
        target = $target
        workload = $Workload
        value_bytes = $ValueSize
        clients = $Clients
        operations_per_repetition = $Operations
        pipeline = $Pipeline
        warmup_per_repetition = $Warmup
        repetitions = $Repetitions
        process_cpu_seconds = ((($after.process_ticks - $before.process_ticks) / 100.0).ToString('R', $invariant))
        container_cpu_seconds = ((($after.container_cpu_microseconds - $before.container_cpu_microseconds) / 1000000.0).ToString('R', $invariant))
        container_throttled_periods = $after.container_throttled_periods - $before.container_throttled_periods
        container_throttled_seconds_counter = ((($after.container_throttled_microseconds - $before.container_throttled_microseconds) / 1000000.0).ToString('R', $invariant))
        container_memory_peak_bytes_since_start = $after.container_memory_peak_bytes
    }
}
$csvDirectory = Split-Path -Parent $csvPath
New-Item -ItemType Directory -Force -Path $csvDirectory | Out-Null
$rows | Export-Csv -NoTypeInformation -Path $csvPath
$rows | Format-Table -AutoSize
