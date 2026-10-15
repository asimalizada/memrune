$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot '../benchmarks/compose.yaml'
& docker compose -f $compose up -d --force-recreate memrune
if ($LASTEXITCODE -ne 0) { throw 'Could not start Memrune.' }
$id = (& docker compose -f $compose ps -aq memrune).Trim()
if ($LASTEXITCODE -ne 0 -or -not $id) { throw 'Could not find Memrune container.' }
$ready = $false
for ($attempt = 0; $attempt -lt 50; $attempt++) {
    $startup = & docker logs $id
    if ($startup -contains 'MEMRUNE listening on 0.0.0.0:6380') { $ready = $true; break }
    Start-Sleep -Milliseconds 100
}
if (-not $ready) { throw 'Memrune did not reach its listening state.' }
& docker compose -f $compose stop -t 5 memrune
if ($LASTEXITCODE -ne 0) { throw 'Could not stop Memrune.' }
$exitCode = (& docker inspect $id --format '{{.State.ExitCode}}').Trim()
if ($LASTEXITCODE -ne 0 -or $exitCode -ne '0') { throw "Memrune exited with code $exitCode after SIGTERM; expected 0." }
$logs = & docker logs $id
if ($LASTEXITCODE -ne 0 -or $logs -notcontains 'MEMRUNE stopped') { throw 'Graceful shutdown log was missing.' }
Write-Host 'Docker SIGTERM shutdown: exit 0 and MEMRUNE stopped.'
