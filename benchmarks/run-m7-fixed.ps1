param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]+$')][string]$Phase,
     $Modes = 'baseline,nodelay,coalesced,combined',
     $Workloads = 'ping,set,get',
     $Rates = '1000,5000,10000',
    [ValidateRange(1, 20)][int]$Repetitions = 5,
    [ValidateRange(1, 20)][int]$DurationSeconds = 3,
    [ValidateRange(1, 512)][int]$Clients = 100,
    [ValidateSet(2, 4, 8)][int]$ClientCpus = 2,
    [ValidateRange(1, 10000)][int]$MaxLagMilliseconds = 20,
    [switch]$SkipRedis
)

$ErrorActionPreference = 'Stop'
$Modes = @($Modes.Split(','))
$Workloads = @($Workloads.Split(','))
$Rates = @($Rates.Split(',') | ForEach-Object { [int]::Parse($_) })
foreach ($mode in $Modes) { if ($mode -notin @('baseline', 'nodelay', 'coalesced', 'combined')) { throw "Unknown mode: $mode" } }
foreach ($workload in $Workloads) { if ($workload -notin @('ping', 'set', 'get')) { throw "Unknown workload: $workload" } }
$base = Join-Path $PSScriptRoot 'compose.yaml'
$raw = Join-Path $PSScriptRoot "results/raw/m7/$Phase"
$curated = Join-Path $PSScriptRoot "results/m7/$Phase.csv"
if (Test-Path $curated) { throw "Curated result already exists: $curated" }
New-Item -ItemType Directory -Force (Split-Path -Parent $curated) | Out-Null
New-Item -ItemType Directory -Force $raw | Out-Null
if (@(Get-ChildItem -LiteralPath $raw -Filter '*.json' -File).Count -gt 0) {
    throw "Raw result directory already contains JSON: $raw"
}
foreach ($rate in $Rates) {
    if ($rate -lt 1 -or $rate -gt 100000 -or $rate * $DurationSeconds -gt 1000000) {
        throw "Unsupported rate or operation count: $rate"
    }
}

function Read-Counters([string]$target) {
    $lines = & docker compose -f $base exec -T $target cat /sys/fs/cgroup/cpu.stat
    if ($LASTEXITCODE -ne 0) { throw "Could not read $target cgroup CPU counters" }
    $data = @{}
    foreach ($line in $lines) {
        if ($line -match '^([a-z_]+) ([0-9]+)$') { $data[$matches[1]] = [long]$matches[2] }
    }
    foreach ($key in @('usage_usec', 'nr_periods', 'nr_throttled', 'throttled_usec')) {
        if (-not $data.ContainsKey($key)) { throw "Missing $key for $target" }
    }
    return $data
}

$rows = [System.Collections.Generic.List[object]]::new()
function Run-Case([string]$target, [string]$mode, [string]$workload, [int]$rate, [int]$rep) {
    $port = if ($target -eq 'memrune') { 6380 } else { 6379 }
    $size = if ($workload -eq 'ping') { 0 } else { 32 }
    $name = "$target-$mode-$workload-r$rate-c$Clients-client$ClientCpus-rep$rep.json"
    $output = "/results/m7/$Phase/$name"
    $operations = $rate * $DurationSeconds
    $warmup = [Math]::Max($Clients * 8, $rate * 2)
    $before = Read-Counters $target
    $files = @('-f', $base)
    if ($ClientCpus -ne 2) { $files += @('-f', (Join-Path $PSScriptRoot "compose.client$ClientCpus.yaml")) }
    & docker compose @files run --rm --no-deps benchmark --benchmark `
        --host $target --port $port --workload $workload --clients $Clients `
        --operations $operations --pipeline 1 --rate $rate --value-size $size `
        --warmup $warmup --repetitions 1 --keys 1024 `
        --max-lag-ms $MaxLagMilliseconds --output $output
    $commandExit = $LASTEXITCODE
    $after = Read-Counters $target
    $path = Join-Path $raw $name
    if (-not (Test-Path $path)) { throw "Benchmark did not write $path (exit $commandExit)" }
    $report = Get-Content $path -Raw | ConvertFrom-Json
    $run = $report.runs[0]
    if (($commandExit -eq 0) -ne [bool]$run.valid) {
        throw "Exit status and run validity disagree: $name (exit $commandExit)"
    }
    $rows.Add([pscustomobject]@{
        phase = $Phase; mode = $mode; target = $target; workload = $workload
        offered_rate = $rate; client_cpus = $ClientCpus; server_cpus = 2
        clients = $Clients; pipeline = 1; value_bytes = $size; repetition = $rep
        operations = $operations; warmup_operations = $warmup
        valid = $run.valid; successful = $run.successfulOperations
        failed = $run.failedOperations; submitted = $run.submittedOperations
        unsubmitted = $run.unsubmittedOperations; late_submissions = $run.lateSubmissions
        timeouts = $run.timedOutOperations; elapsed_seconds = $run.elapsedSeconds
        achieved_ops_per_second = $run.successfulOpsPerSecond
        p50_us = $run.p50Microseconds; p95_us = $run.p95Microseconds
        p99_us = $run.p99Microseconds
        lag_p50_us = $run.submissionLagP50Microseconds
        lag_p95_us = $run.submissionLagP95Microseconds
        lag_p99_us = $run.submissionLagP99Microseconds
        max_lag_us = $run.maxSubmissionLagMicroseconds
        client_cpu_seconds_measured = $run.clientCpuSeconds
        client_allocated_bytes_measured = $run.clientAllocatedBytes
        client_gen0_measured = $run.clientGen0Collections
        client_gen1_measured = $run.clientGen1Collections
        client_gen2_measured = $run.clientGen2Collections
        client_cgroup_throttled_periods_measured = $run.clientCgroupThrottledPeriods
        server_cpu_seconds_inclusive = ($after.usage_usec - $before.usage_usec) / 1000000.0
        server_throttled_periods_inclusive = $after.nr_throttled - $before.nr_throttled
        server_throttled_seconds_inclusive = ($after.throttled_usec - $before.throttled_usec) / 1000000.0
        error = $run.error
    })
    $rows | Export-Csv -Path $curated -NoTypeInformation
    Write-Host "$name valid=$($run.valid) success=$($run.successfulOperations)/$operations p99=$($run.p99Microseconds) us"
}

& docker compose -f $base up -d memrune redis
if ($LASTEXITCODE -ne 0) { throw 'Could not start benchmark servers' }
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    if (-not $SkipRedis -and $rep % 2 -eq 1) {
        foreach ($workload in $Workloads) { foreach ($rate in $Rates) {
            Run-Case 'redis' 'control' $workload $rate $rep
        }}
    }
    for ($offset = 0; $offset -lt $Modes.Count; $offset++) {
        $mode = $Modes[($offset + $rep - 1) % $Modes.Count]
        $files = @('-f', $base)
        if ($mode -ne 'baseline') { $files += @('-f', (Join-Path $PSScriptRoot "compose.$mode.yaml")) }
        & docker compose @files up -d --force-recreate memrune
        if ($LASTEXITCODE -ne 0) { throw "Could not start Memrune mode $mode" }
        foreach ($workload in $Workloads) { foreach ($rate in $Rates) {
            Run-Case 'memrune' $mode $workload $rate $rep
        }}
    }
    if (-not $SkipRedis -and $rep % 2 -eq 0) {
        foreach ($workload in $Workloads) { foreach ($rate in $Rates) {
            Run-Case 'redis' 'control' $workload $rate $rep
        }}
    }
}
Write-Host "Wrote $($rows.Count) individual fixed-rate runs to $curated"
