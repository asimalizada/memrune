# Docker benchmark

This is a local RESP2 comparison of Memrune and Redis 7. It is a diagnostic experiment, not a production performance claim. The older PING load mode in `Memrune.LoadTest` remains separate.

The controlled TCP_NODELAY and response-coalescing experiments are documented in [TRANSPORT.md](TRANSPORT.md). Their results are separate from the original Milestone 4 baseline.

## Environment

`compose.yaml` runs Memrune, Redis, and the same .NET 10 benchmark client on a private Docker network. Neither server publishes a host port. Both servers have a two CPU and 1 GiB container limit; the client has the same limit. Memrune uses `--bind 0.0.0.0` only inside this network. Normal server execution still defaults to `127.0.0.1:6380`.

The Redis image and .NET build/runtime images are pinned by digest to the versions used in the recorded report. Change these deliberately when repeating the experiment with newer runtimes or Redis patches.

Redis is started with `--save "" --appendonly no` so persistence work does not affect this in-memory comparison. `--protected-mode no` allows the client container to connect on the private network. Do not expose this Redis service to an untrusted network.

## Reproduce on Windows with Docker Desktop

From the repository root in PowerShell:

```powershell
docker compose -f benchmarks/compose.yaml build
docker compose -f benchmarks/compose.yaml up -d memrune redis
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-matrix.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/summarize.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/measure-resources.ps1
docker compose -f benchmarks/compose.yaml down
```

The matrix runs PING, SET and GET; 32- and 1024-byte values for SET/GET; 1, 50 and 100 clients; and pipeline depths 1 and 16. It runs three measured repetitions for each of the 30 configurations on each target. Each repetition creates fresh connections. The script alternates which target runs first by configuration. Defaults are 64 measured operations per client at depth 1, 512 per client at depth 16, 8 warmup operations per client, 1024 keys, and 3 repetitions. Override these with `-OperationsPerClient`, `-PipelinedOperationsPerClient`, `-WarmupPerClient`, `-Keys`, and `-Repetitions` on `run-matrix.ps1`. Keep the same options for both targets.

For one configuration, after starting the servers:

```powershell
docker compose -f benchmarks/compose.yaml run --rm benchmark --benchmark --host memrune --port 6380 --workload get --clients 50 --operations 3200 --pipeline 1 --value-size 32 --warmup 400 --repetitions 3 --keys 1024 --output /results/manual-memrune.json
docker compose -f benchmarks/compose.yaml run --rm benchmark --benchmark --host redis --port 6379 --workload get --clients 50 --operations 3200 --pipeline 1 --value-size 32 --warmup 400 --repetitions 3 --keys 1024 --output /results/manual-redis.json
```

The benchmark mode is also usable outside Docker with `dotnet run --project tools/Memrune.LoadTest -c Release -- --benchmark ...`. Every option is a name/value pair. Required workload choices are `ping`, `set`, and `get`; options are `--host`, `--port`, `--clients`, `--operations`, `--pipeline`, `--value-size`, `--warmup`, `--repetitions`, `--keys`, and `--output`. With omitted counts, the tool chooses 128 or more measured operations, 8 warmup operations per client, and 3 repetitions. The matrix script passes all counts explicitly.

## Measurement and correctness

Each run has setup, warmup, measurement, and cleanup phases. SET and GET prepopulate the same deterministic 1024-key dataset before timing; SET measures overwrites. Each client sends a batch of at most the pipeline depth, then reads and validates exactly that many ordered replies. A unique PING marker after measurement detects unexpected extra replies. Setup, connection establishment, warmup, marker checks, cleanup, and JSON output are outside the measured interval. Requests and expected payloads are generated before timing. Dataset setup and cleanup use bounded batches. Invalid or missing responses invalidate the run and prevent an aggregate median.

Elapsed time uses `Stopwatch` monotonic ticks and covers all measured requests through their validated replies. Throughput is successful operations divided by elapsed time. A command's latency is measured from just before its batch `WriteAsync` through receipt and validation of its full reply. All commands in a pipeline batch share that submission timestamp, so later commands include time behind earlier commands and client-side validation. These are **client-observed completion latencies**, not isolated server execution times. P50, P95 and P99 use nearest-rank percentiles of these per-command samples. Aggregate columns are medians of the three individual run metrics.

`results/raw/` contains detailed JSON per target and configuration and is ignored by Git. `summarize.ps1` produces curated `results/runs.csv` and `results/summary.csv` with every individual run and the per-configuration medians. `results/report.md` describes the observed environment and interpretation. Container startup and Docker Compose command time are excluded from operation timing.

`measure-resources.ps1` repeats a representative GET workload while reading Linux `/proc/1/stat` and cgroup counters before and after each target. `results/resources.csv` reports process CPU time and container-wide CPU time for the whole invocation, including setup, warmup, measurement, cleanup, and waiting. The cgroup memory peak is **since container start**, not just this workload. The resource run is separate from the throughput matrix. These counters cannot isolate per-command CPU or .NET allocation/GC; the minimal runtime image does not include `dotnet-counters` or `dotnet-trace`.

The workload is intentionally small: deterministic keys, fixed values, no expiration, no mixed command ratios, and no persistence. The benchmark client and Docker Desktop's Linux VM can limit throughput. Single-host local results can change with host load, power settings, virtualization, kernel TCP behavior, and runtime versions. Compare corresponding configurations and inspect all three repetitions; do not generalize these numbers to production deployments.
