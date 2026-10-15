param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]+$')][string]$Phase,
    [ValidateRange(1, 20)][int]$Repetitions = 5,
    $Modes = 'baseline,nodelay,coalesced,combined',
    $CaseNames = 'ping-c1-p1,ping-c100-p16,set32-c100-p16,get32-c100-p16,get1024-c100-p16',
    [ValidateSet(2, 4, 8)][int]$ClientCpus = 2,
    [switch]$SkipRedis
)

$ErrorActionPreference = 'Stop'
$Modes = @($Modes.Split(','))
foreach ($mode in $Modes) { if ($mode -notin @('baseline', 'nodelay', 'coalesced', 'combined')) { throw "Unknown mode: $mode" } }
$base = Join-Path $PSScriptRoot 'compose.yaml'
$raw = Join-Path $PSScriptRoot "results/raw/m7/$Phase"
$curated = Join-Path $PSScriptRoot "results/m7/$Phase.csv"
if (Test-Path $curated) { throw "Curated result already exists: $curated" }
New-Item -ItemType Directory -Force (Split-Path -Parent $curated) | Out-Null
New-Item -ItemType Directory -Force $raw | Out-Null
if (@(Get-ChildItem -LiteralPath $raw -Filter '*.json' -File).Count -gt 0) { throw "Raw result directory already contains JSON: $raw" }

$cases = @(
    @{ Name = 'ping-c1-p1'; Workload = 'ping'; Size = 0; Clients = 1; Pipeline = 1; Operations = 128 },
    @{ Name = 'ping-c100-p16'; Workload = 'ping'; Size = 0; Clients = 100; Pipeline = 16; Operations = 51200 },
    @{ Name = 'set32-c100-p16'; Workload = 'set'; Size = 32; Clients = 100; Pipeline = 16; Operations = 51200 },
    @{ Name = 'get32-c100-p16'; Workload = 'get'; Size = 32; Clients = 100; Pipeline = 16; Operations = 51200 },
    @{ Name = 'get1024-c100-p16'; Workload = 'get'; Size = 1024; Clients = 100; Pipeline = 16; Operations = 51200 }
)

$selectedCaseNames = @($CaseNames.Split(','))
foreach ($name in $selectedCaseNames) {
    if ($name -notin @($cases | ForEach-Object { $_.Name })) { throw "Unknown case: $name" }
}
$cases = @($cases | Where-Object { $_.Name -in $selectedCaseNames })
function Read-Counters([string]$target) {
    $lines = & docker compose -f $base exec -T $target cat /sys/fs/cgroup/cpu.stat
    if ($LASTEXITCODE -ne 0) { throw "Could not read $target cgroup CPU counters" }
    $data = @{}
    foreach ($line in $lines) { if ($line -match '^([a-z_]+) ([0-9]+)$') { $data[$matches[1]] = [long]$matches[2] } }
    foreach ($key in @('usage_usec', 'nr_throttled', 'throttled_usec')) {
        if (-not $data.ContainsKey($key)) { throw "Missing $key for $target" }
    }
    return $data
}

$rows = [System.Collections.Generic.List[object]]::new()
function Run-Case([string]$target, [string]$mode, [hashtable]$case, [int]$rep) {
    $port = if ($target -eq 'memrune') { 6380 } else { 6379 }
    $name = "$target-$mode-$($case.Name)-client$ClientCpus-rep$rep.json"
    $warmup = $case.Clients * 8
    $before = Read-Counters $target
    $files = @('-f', $base)
    if ($ClientCpus -ne 2) { $files += @('-f', (Join-Path $PSScriptRoot "compose.client$ClientCpus.yaml")) }
    & docker compose @files run --rm --no-deps benchmark --benchmark `
        --host $target --port $port --workload $case.Workload --clients $case.Clients `
        --operations $case.Operations --pipeline $case.Pipeline --value-size $case.Size `
        --warmup $warmup --repetitions 1 --keys 1024 `
        --output "/results/m7/$Phase/$name"
    $commandExit = $LASTEXITCODE
    $after = Read-Counters $target
    $path = Join-Path $raw $name
    if (-not (Test-Path $path)) { throw "Benchmark did not write $path (exit $commandExit)" }
    $report = Get-Content $path -Raw | ConvertFrom-Json
    $run = $report.runs[0]
    if (($commandExit -eq 0) -ne [bool]$run.valid) { throw "Exit status and validity disagree: $name" }
    $rows.Add([pscustomobject]@{
        phase = $Phase; mode = $mode; target = $target; case = $case.Name
        workload = $case.Workload; value_bytes = $case.Size; clients = $case.Clients
        pipeline = $case.Pipeline; operations = $case.Operations; repetition = $rep
        client_cpus = $ClientCpus; server_cpus = 2; valid = $run.valid
        successful = $run.successfulOperations; failed = $run.failedOperations
        elapsed_seconds = $run.elapsedSeconds; successful_ops_per_second = $run.successfulOpsPerSecond
        p50_us = $run.p50Microseconds; p95_us = $run.p95Microseconds; p99_us = $run.p99Microseconds
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
    Write-Host "$name valid=$($run.valid) ops/s=$($run.successfulOpsPerSecond) p99=$($run.p99Microseconds) us"
}

& docker compose -f $base up -d memrune redis
if ($LASTEXITCODE -ne 0) { throw 'Could not start benchmark servers' }
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    if (-not $SkipRedis -and $rep % 2 -eq 1) { foreach ($case in $cases) { Run-Case 'redis' 'control' $case $rep } }
    for ($offset = 0; $offset -lt $Modes.Count; $offset++) {
        $mode = $Modes[($offset + $rep - 1) % $Modes.Count]
        $files = @('-f', $base)
        if ($mode -ne 'baseline') { $files += @('-f', (Join-Path $PSScriptRoot "compose.$mode.yaml")) }
        & docker compose @files up -d --force-recreate memrune
        if ($LASTEXITCODE -ne 0) { throw "Could not start Memrune mode $mode" }
        foreach ($case in $cases) { Run-Case 'memrune' $mode $case $rep }
    }
    if (-not $SkipRedis -and $rep % 2 -eq 0) { foreach ($case in $cases) { Run-Case 'redis' 'control' $case $rep } }
}
Write-Host "Wrote $($rows.Count) individual closed-loop runs to $curated"
