param(
    [int]$OperationsPerClient = 64,
    [int]$PipelinedOperationsPerClient = 512,
    [int]$WarmupPerClient = 8,
    [int]$Repetitions = 3,
    [int]$Keys = 1024,
    [string]$ResultSubdirectory = ''
)

$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot 'compose.yaml'
$raw = Join-Path $PSScriptRoot "results/raw/$ResultSubdirectory"
$outputDirectory = if ($ResultSubdirectory) { "/results/$ResultSubdirectory" } else { '/results' }
New-Item -ItemType Directory -Force -Path $raw | Out-Null

$configNumber = 0
$failures = @()
foreach ($workload in @('ping', 'set', 'get')) {
    $sizes = if ($workload -eq 'ping') { @(0) } else { @(32, 1024) }
    foreach ($size in $sizes) {
        foreach ($clients in @(1, 50, 100)) {
            foreach ($pipeline in @(1, 16)) {
                $configNumber++
                $perClient = if ($pipeline -eq 1) { $OperationsPerClient } else { $PipelinedOperationsPerClient }
                $operations = $clients * $perClient
                $warmup = $clients * $WarmupPerClient
                # Alternate target order to reduce bias from always running one server first.
                $targets = if ($configNumber % 2 -eq 0) { @('redis', 'memrune') } else { @('memrune', 'redis') }
                foreach ($target in $targets) {
                    $port = if ($target -eq 'redis') { 6379 } else { 6380 }
                    $name = "$target-$workload-v$size-c$clients-p$pipeline.json"
                    Write-Host "[$configNumber/30] $name ($operations operations, $warmup warmup, $Repetitions repetitions)"
                    & docker compose -f $compose run --rm benchmark --benchmark `
                        --host $target --port $port --workload $workload --clients $clients `
                        --operations $operations --pipeline $pipeline --value-size $size `
                        --warmup $warmup --repetitions $Repetitions --keys $Keys `
                        --output "$outputDirectory/$name"
                    if ($LASTEXITCODE -ne 0) { $failures += $name }
                }
            }
        }
    }
}
if ($failures.Count -gt 0) {
    throw "Failed benchmark configurations: $($failures -join ', ')"
}
Write-Host "Completed all 60 target/configuration combinations. Raw JSON: $raw"
