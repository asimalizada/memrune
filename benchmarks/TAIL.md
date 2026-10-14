# Milestone 5B: tail latency and resource investigation

The results are in [results/m5b/report.md](results/m5b/report.md). This is a diagnostic comparison, not a production performance claim. The original Milestone 4 and 5A results are unchanged.

## Method

`run-tail-investigation.ps1` runs the existing validated RESP2 PING benchmark. It starts Memrune and Redis with the same server CPU quota and 1 GiB memory limit. The benchmark client has its own quota. Modes are the original transport (`baseline`), accepted-socket TCP_NODELAY (`nodelay`), and TCP_NODELAY plus coalesced RESP responses (`combined`). Redis is run once per server quota because it has no Memrune transport mode. Targets run sequentially on the same private Docker network.

Each repetition uses a fresh benchmark client container and a fresh TCP connection per worker. Every reply and a final per-connection marker are validated. The measured clock starts before each request batch is submitted; pipelined commands share the batch start time. The clock stops after the final validated response. The client reports process CPU time, approximate allocated bytes, and GC collection deltas from just before to just after that measured phase. On Linux cgroup v2 the current client also reports CPU usage and throttling deltas for the same phase. These reads occur outside the timed phase. The first quota and client-limit runs preceded the client cgroup-counter addition; the separately named `clientcgroup*` runs contain those fields.

The PowerShell harness reads the **server** cgroup `cpu.stat` and `cpu.pressure` before and after each single-repetition benchmark invocation. Its deltas include connection setup, warmup, marker checks, cleanup, and Docker launch/wait time. They are useful for comparing resource pressure between matched runs, but are not measured-phase CPU costs. `throttled_usec` can sum blocked time across threads and exceed wall time. The CSV records individual runs; `summarize-tail.ps1` reports medians of each metric separately.

The main quota matrix uses 64 measured PINGs for 1 client/depth 1, 25,600 for 50 clients/depth 16, and 51,200 for 100 clients/depth 16. Warmup is 8 commands per client. All configurations have three repetitions. Because the optimized runs can finish in a fraction of a second, separate high-load confirmation uses 500,000 measured PINGs at 100 clients/depth 16, again with three repetitions.

## Reproduce

Run from the repository root in PowerShell with Docker Desktop available. Choose new `-ResultName` values when preserving the existing CSV files; the runner refuses to overwrite them.

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
docker compose -f benchmarks/compose.yaml --profile runner build
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-tail-investigation.ps1 -ResultName repeat-quota
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-tail-investigation.ps1 -ServerCpus 2 -HighLoadOnly -PipelinedOperationsPerClient 5000 -ResultName repeat-long2
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-tail-investigation.ps1 -ServerCpus 4 -HighLoadOnly -PipelinedOperationsPerClient 5000 -ResultName repeat-long4
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-tail-investigation.ps1 -ServerCpus 8 -Modes combined -HighLoadOnly -PipelinedOperationsPerClient 5000 -ResultName repeat-long8-client2
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-tail-investigation.ps1 -ServerCpus 8 -Modes combined -ClientCpus 8 -HighLoadOnly -PipelinedOperationsPerClient 5000 -ResultName repeat-long8-client8
```

For a client-only quota check with the server fixed at 4 CPUs, run the same script with `-ServerCpus 4 -Modes combined -ClientCpus 4` and then `-ClientCpus 8`, using distinct result names. Use `-ServerCpus 2 -Modes combined` for the original two-CPU combined setup. `-HighLoadOnly -PipelinedOperationsPerClient 5000` selects the longer 100-client case.

The runner writes curated per-run CSVs to `benchmarks/results/m5b/<ResultName>.csv` and detailed JSON to ignored `benchmarks/results/raw/m5b/<ResultName>/`. To regenerate the retained aggregate from the retained per-run CSVs:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/summarize-tail.ps1
```

For a fresh set, call `summarize-tail.ps1` from a PowerShell prompt with `-Sources @('repeat-quota.csv','repeat-long2.csv',...) -OutputName repeat-summary.csv`. Individual repetitions should be inspected before interpreting a median. Redis controls are repeated at each quota with identical client code and workload arguments.

The normal server default remains loopback binding and the original transport settings. Docker explicitly binds Memrune to its private container network. Compose publishes no database ports.

## Diagnostic profiling

The runtime counter and stack traces were collected separately from the primary matrix. `dotnet-counters` and `dotnet-trace` version `10.0.750501` were installed temporarily from the pinned .NET 10 SDK image, copied into a running Memrune container, and removed with that container. They are not part of the production image or project dependencies. The counter capture used `System.Runtime` at one-second intervals during a separate million-PING load. The stack capture used `dotnet-sampled-thread-time` and managed rundown. Generated traces are under ignored `benchmarks/results/profiles/`.

The stack sampler measures time on .NET thread stacks, including waiting, and did not yield a reliable CPU-hotspot attribution: many samples were waits or missing symbols. `dotnet-counters` showed CPU-mode, allocation, GC, ThreadPool queue, and monitor contention counters, but its one-second windows only approximately align with the measured load. `/proc/1/io` syscall counters were unavailable in this container. No packet capture, kernel CPU sampling, or elevated container privilege was used.
