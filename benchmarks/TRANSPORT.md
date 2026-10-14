# Milestone 5A transport experiments

The normal Memrune command still defaults to the original response writer and accepted-socket TCP setting. The experiment flags are `--tcp-nodelay` and `--coalesce-responses`. They can be used separately or together. `--tcp-nodelay` sets `TcpClient.NoDelay` on accepted sockets; `--coalesce-responses` gives each client session a reusable RESP output buffer and submits each complete response in one `NetworkStream.WriteAsync`. The default bind address remains `127.0.0.1` outside Docker.

The benchmark client and workload parameters are unchanged from Milestone 4. All results go under `benchmarks/results/m5a/` or ignored `benchmarks/results/raw/m5a/`; the Milestone 4 baseline in `benchmarks/results/` is preserved. `run-transport.ps1` **runs requests against the current containers; it does not switch the server mode**. The Compose override used to start Memrune selects the mode.

From the repository root in PowerShell:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
docker compose -f benchmarks/compose.yaml build
docker compose -f benchmarks/compose.yaml up -d memrune redis
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-transport.ps1 -Phase baseline

docker compose -f benchmarks/compose.yaml -f benchmarks/compose.nodelay.yaml up -d --force-recreate memrune
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-transport.ps1 -Phase nodelay

docker compose -f benchmarks/compose.yaml -f benchmarks/compose.coalesced.yaml up -d --force-recreate memrune
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-transport.ps1 -Phase coalesced

docker compose -f benchmarks/compose.yaml -f benchmarks/compose.combined.yaml up -d --force-recreate memrune
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-transport.ps1 -Phase combined
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-matrix.ps1 -ResultSubdirectory m5a/final-verified
```

Each controlled phase covers PING at 1/100 clients and depth 1/16, plus 32-byte SET and GET at 100 clients and depth 16. Each target/configuration has three measured repetitions. The full matrix includes all Milestone 4 workloads and parameters. All benchmark commands validate every expected reply and a post-run marker. A nonzero exit code or invalid run prevents a valid median.

Generate curated CSVs after the runs:

```powershell
foreach ($phase in @('baseline', 'nodelay', 'coalesced', 'combined')) {
    powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/summarize.ps1 -InputSubdirectory "m5a/$phase" -OutputSubdirectory "m5a/$phase" -ExpectedConfigurations 6
}
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/summarize.ps1 -InputSubdirectory m5a/final-verified -OutputSubdirectory m5a/final-verified -ExpectedConfigurations 30
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/compare-transport.ps1
```

For a matched CPU and throttling diagnostic, select each Compose mode as above and run this command with its phase name:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/measure-resources.ps1 -Workload ping -ValueSize 0 -Clients 100 -Operations 51200 -Pipeline 16 -Warmup 800 -Repetitions 3 -ResultSubdirectory m5a/resource-baseline -OutputFile m5a/resources/baseline.csv
```

Replace both occurrences of `baseline` with `nodelay`, `coalesced`, or `combined` after starting the matching Memrune container. Resource counters span setup, warmup, measurement, and cleanup; cgroup memory peak is since each container started. `throttled_usec` is a Linux cgroup counter, not a direct measurement of individual response delays.

Check the final container with the official Redis CLI on the private network:

```powershell
docker compose -f benchmarks/compose.yaml exec -T redis redis-cli -h memrune -p 6380 PING
docker compose -f benchmarks/compose.yaml exec -T redis redis-cli -h memrune -p 6380 SET demo hello
docker compose -f benchmarks/compose.yaml exec -T redis redis-cli -h memrune -p 6380 GET demo
docker compose -f benchmarks/compose.yaml down
```

The report is [results/m5a/report.md](results/m5a/report.md). Controlled results and the final full matrix are in separate CSVs within that directory. Raw JSON is ignored by Git; curated CSVs and reports can be retained. The original Milestone 4 results remain in `benchmarks/results/`.
