param(
    [ValidateSet(2, 4, 8)][int[]]$ServerCpus = @(2, 4, 8),
    [ValidateSet('baseline', 'nodelay', 'combined')][string[]]$Modes = @('baseline', 'nodelay', 'combined'),
    [ValidateSet(2, 4, 8)][int]$ClientCpus = 2,
    [ValidateSet('memrune', 'redis')][string[]]$Targets = @('memrune', 'redis'),
    [ValidateRange(1, 20)][int]$Repetitions = 3,
    [switch]$HighLoadOnly,
    [ValidateRange(1, 10000)][int]$PipelinedOperationsPerClient = 512,
    [string]$ResultName = 'quota'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$base = Join-Path $root 'compose.yaml'
$raw = Join-Path $root "results/raw/m5b/$ResultName"
$curated = Join-Path $root "results/m5b/$ResultName.csv"
if (Test-Path $curated) { throw "Result file already exists: $curated" }
New-Item -ItemType Directory -Force -Path $raw | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $curated) | Out-Null

function Compose-Files([int]$serverCpus, [string]$mode) {
    $files = @('-f', $base)
    if ($serverCpus -ne 2) { $files += @('-f', (Join-Path $root "compose.cpu$serverCpus.yaml")) }
    if ($mode -ne 'baseline') { $files += @('-f', (Join-Path $root "compose.$mode.yaml")) }
    if ($ClientCpus -ne 2) { $files += @('-f', (Join-Path $root "compose.client$ClientCpus.yaml")) }
    return $files
}

function Read-Counters([string[]]$files, [string]$target) {
    $lines = & docker compose @files exec -T $target cat /sys/fs/cgroup/cpu.stat /sys/fs/cgroup/cpu.pressure
    if ($LASTEXITCODE -ne 0) { throw "Failed reading $target cgroup counters" }
    $data = @{}
    foreach ($line in $lines) {
        if ($line -match '^([a-z_]+) ([0-9]+)$') { $data[$matches[1]] = [long]$matches[2] }
        if ($line -match '^some .*total=([0-9]+)') { $data['pressure_some_usec'] = [long]$matches[1] }
        if ($line -match '^full .*total=([0-9]+)') { $data['pressure_full_usec'] = [long]$matches[1] }
    }
    foreach ($key in @('usage_usec', 'nr_periods', 'nr_throttled', 'throttled_usec')) {
        if (-not $data.ContainsKey($key)) { throw "Missing $key for $target" }
    }
    return $data
}

$cases = @(
    @{ Clients = 1; Pipeline = 1; Operations = 64 },
    @{ Clients = 50; Pipeline = 16; Operations = (50 * $PipelinedOperationsPerClient) },
    @{ Clients = 100; Pipeline = 16; Operations = (100 * $PipelinedOperationsPerClient) }
)
if ($HighLoadOnly) { $cases = @($cases[2]) }
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($quota in $ServerCpus) {
    foreach ($mode in $Modes) {
        $files = Compose-Files $quota $mode
        & docker compose @files up -d --force-recreate memrune redis
        if ($LASTEXITCODE -ne 0) { throw "Could not start $mode at $quota CPUs" }
        foreach ($target in $Targets) {
            # Redis has no transport mode. Run it once at each server quota.
            if ($target -eq 'redis' -and $mode -ne $Modes[0]) { continue }
            $port = if ($target -eq 'memrune') { 6380 } else { 6379 }
            foreach ($case in $cases) {
                for ($rep = 1; $rep -le $Repetitions; $rep++) {
                    $name = "q$quota-$mode-client$ClientCpus-$target-c$($case.Clients)-p$($case.Pipeline)-r$rep.json"
                    $before = Read-Counters $files $target
                    & docker compose @files run --rm --no-deps benchmark --benchmark `
                        --host $target --port $port --workload ping `
                        --clients $case.Clients --operations $case.Operations `
                        --pipeline $case.Pipeline --value-size 0 --warmup ($case.Clients * 8) `
                        --repetitions 1 --keys 1024 --output "/results/m5b/$ResultName/$name"
                    if ($LASTEXITCODE -ne 0) { throw "Invalid benchmark run: $name" }
                    $after = Read-Counters $files $target
                    $run = (Get-Content (Join-Path $raw $name) -Raw | ConvertFrom-Json).runs[0]
                    if (-not $run.valid -or $run.failedOperations -ne 0) { throw "Invalid replies: $name" }
                    $rows.Add([pscustomobject]@{
                        server_cpus = $quota; client_cpus = $ClientCpus; mode = $mode
                        target = $target; clients = $case.Clients; pipeline = $case.Pipeline
                        repetition = $rep; operations = $case.Operations
                        successful = $run.successfulOperations; failed = $run.failedOperations
                        elapsed_seconds = $run.elapsedSeconds; ops_per_second = $run.successfulOpsPerSecond
                        p50_us = $run.p50Microseconds; p95_us = $run.p95Microseconds
                        p99_us = $run.p99Microseconds
                        client_cpu_seconds_measured = $run.clientCpuSeconds
                        client_allocated_bytes_measured = $run.clientAllocatedBytes
                        client_gen0_measured = $run.clientGen0Collections
                        client_gen1_measured = $run.clientGen1Collections
                        client_gen2_measured = $run.clientGen2Collections
                        client_cgroup_cpu_seconds_measured = $run.clientCgroupCpuSeconds
                        client_cgroup_throttled_periods_measured = $run.clientCgroupThrottledPeriods
                        client_cgroup_throttled_seconds_measured = $run.clientCgroupThrottledSeconds
                        server_cpu_seconds_inclusive = ($after.usage_usec - $before.usage_usec) / 1000000.0
                        server_periods_inclusive = $after.nr_periods - $before.nr_periods
                        server_throttled_periods_inclusive = $after.nr_throttled - $before.nr_throttled
                        server_throttled_seconds_inclusive = ($after.throttled_usec - $before.throttled_usec) / 1000000.0
                        server_cpu_pressure_some_seconds_inclusive = ($after.pressure_some_usec - $before.pressure_some_usec) / 1000000.0
                        server_cpu_pressure_full_seconds_inclusive = ($after.pressure_full_usec - $before.pressure_full_usec) / 1000000.0
                    })
                    $rows | Export-Csv -Path $curated -NoTypeInformation
                    Write-Host "$name : $([math]::Round($run.successfulOpsPerSecond)) ops/s, P99 $([math]::Round($run.p99Microseconds)) us"
                }
            }
        }
    }
}
Write-Host "Wrote $curated ($($rows.Count) individual runs)."
