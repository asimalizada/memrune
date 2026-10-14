param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('baseline', 'nodelay', 'coalesced', 'combined')][string]$Phase,
    [int]$Repetitions = 3,
    [int]$Keys = 1024
)

$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot 'compose.yaml'
$raw = Join-Path $PSScriptRoot "results/raw/m5a/$Phase"
New-Item -ItemType Directory -Force -Path $raw | Out-Null

$configurations = @(
    @{ Workload = 'ping'; Size = 0; Clients = 1; Pipeline = 1 },
    @{ Workload = 'ping'; Size = 0; Clients = 1; Pipeline = 16 },
    @{ Workload = 'ping'; Size = 0; Clients = 100; Pipeline = 1 },
    @{ Workload = 'ping'; Size = 0; Clients = 100; Pipeline = 16 },
    @{ Workload = 'set'; Size = 32; Clients = 100; Pipeline = 16 },
    @{ Workload = 'get'; Size = 32; Clients = 100; Pipeline = 16 }
)

$failures = @()
foreach ($config in $configurations) {
    $operations = $config.Clients * $(if ($config.Pipeline -eq 1) { 64 } else { 512 })
    $warmup = $config.Clients * 8
    foreach ($target in @('memrune', 'redis')) {
        $port = if ($target -eq 'memrune') { 6380 } else { 6379 }
        $name = "$target-$($config.Workload)-v$($config.Size)-c$($config.Clients)-p$($config.Pipeline).json"
        Write-Host "$Phase $name ($operations measured operations, $Repetitions repetitions)"
        & docker compose -f $compose run --rm benchmark --benchmark `
            --host $target --port $port --workload $config.Workload `
            --clients $config.Clients --operations $operations `
            --pipeline $config.Pipeline --value-size $config.Size `
            --warmup $warmup --repetitions $Repetitions --keys $Keys `
            --output "/results/m5a/$Phase/$name"
        if ($LASTEXITCODE -ne 0) { $failures += $name }
    }
}
if ($failures.Count -gt 0) { throw "Failed configurations: $($failures -join ', ')" }
Write-Host "Completed $Phase phase: 12 target/configuration combinations."
