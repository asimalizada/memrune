# Docker benchmark

This is a local RESP2 comparison of Memrune and Redis 7. It is a diagnostic experiment, not a production performance claim. The older PING load mode in `Memrune.LoadTest` remains separate.

The controlled TCP_NODELAY and response-coalescing experiments are documented in [TRANSPORT.md](TRANSPORT.md). Their results are separate from the original Milestone 4 baseline.

The server/client CPU quota and tail-latency investigation is documented in [TAIL.md](TAIL.md), with [individual results and analysis](results/m5b/report.md). It uses the same validated RESP2 client and keeps the earlier result sets intact.

## Environment

`compose.yaml` runs Memrune, Redis, and the same .NET 10 benchmark client on a private Docker network. Neither server publishes a host port. Both servers have a two CPU and 1 GiB container limit; the client has the same limit. Memrune uses `--bind 0.0.0.0` only inside this network. Normal server execution still defaults to `127.0.0.1:6380`.

The Redis image and .NET build/runtime images are pinned by digest to the versions used in the recorded report. Change these deliberately when repeating the experiment with newer runtimes or Redis patches.

Redis is started with `--save "" --appendonly no` so persistence work does not affect this in-memory comparison. `--protected-mode no` allows the client container to connect on the private network. Do not expose this Redis service to an untrusted network.

## Reproduce on Windows with Docker Desktop

From the repository root in PowerShell:

```powershell
docker compose -f benchmarks/compose.yaml --profile runner build
docker compose -f benchmarks/compose.yaml up -d memrune redis
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-matrix.ps1 -ResultSubdirectory reproduction-v01
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/summarize.ps1 -InputSubdirectory reproduction-v01 -OutputSubdirectory reproduction-v01
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/measure-resources.ps1 -ResultSubdirectory reproduction-v01 -OutputFile reproduction-v01/resources.csv
docker compose -f benchmarks/compose.yaml down
```

Use a new subdirectory name for each reproduction. The matrix runner refuses a raw directory already containing JSON, and the summarizer and resource script refuse to overwrite curated CSVs. The commands above keep the original Milestone 4 and later evidence intact. The [v0.1 audit smoke run](results/m6-audit/report.md) is a separate short check.

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

## Milestone 7: matched offered load and transport defaults

The normal executable now enables TCP_NODELAY and coalesced responses. This Compose base file explicitly disables both to preserve the historical original-transport benchmark; the three Compose overrides select TCP_NODELAY only, coalescing only, or both. Each mode is explicit in the resolved Compose command. Use the combined override for the current default behavior.

The existing benchmark mode remains closed-loop: each connection sends a bounded batch, validates its replies, then sends the next batch. It measures achieved maximum throughput for that client and pipeline configuration. Its per-command latency starts before batch submission and ends after that reply is validated, so commands later in a batch include queueing behind earlier replies. Faster and slower servers receive different offered loads in this mode.

Add `--rate OPS_PER_SECOND` to use fixed-rate mode. It requires `--pipeline 1`, assigns global monotonic arrival times across the configured clients, and permits one in-flight command per connection. Latency is scheduled arrival to validated reply, including client submission delay and queueing. The report separates intended rate, achieved successful throughput, late submissions, submission-lag percentiles and maximum, timeouts, and unsubmitted work. A run is invalid if any reply is wrong/missing, the 20 ms default submission-lag limit is exceeded, or achieved throughput is below 95% of intended rate. Invalid runs have no latency percentiles or aggregate median. The rate generator uses bounded per-client loops, not one task per operation.

Timer wakes, benchmark-client CPU limits, and Docker scheduling add latency. The 100-client, two-CPU client can become limiting by 10,000 to 25,000 operations/s; 50,000 pipeline-1 operations/s failed pilot validity checks even with eight client CPUs. Treat these as client limits, not database capacity claims. Inspect client CPU/throttling and submission lag alongside server counters. The fixed-rate scripts use a two-second warmup because a half-second warmup produced first-repetition late-submission failures in pilot runs.

From the repository root after building the images, these scripts preserve every individual run in curated CSV under `benchmarks/results/m7/`; detailed JSON stays in ignored `benchmarks/results/raw/m7/`. Choose a fresh `-Phase` name for each reproduction:

```powershell
docker compose -f benchmarks/compose.yaml --profile runner build
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-m7-fixed.ps1 -Phase reproduce-fixed -Modes baseline,nodelay,coalesced,combined -Workloads ping -Rates 1000,5000 -Repetitions 5 -DurationSeconds 3 -Clients 100 -ClientCpus 2
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-m7-closed.ps1 -Phase reproduce-closed -Modes baseline,nodelay,coalesced,combined -CaseNames ping-c100-p16 -Repetitions 5
docker compose -f benchmarks/compose.yaml down
```

The fixed harness interleaves transport modes by repetition and alternates Redis control order. Its server cgroup CPU and throttling deltas span the whole invocation, including setup, warmup, cleanup, and process waiting; client process CPU, allocation, GC, and cgroup throttling counters cover the measured phase. These are not interchangeable counters. The separate `AllocationProbe` is a managed allocation diagnostic, not a TCP benchmark. See the [Milestone 7 report](results/m7/report.md) for exact versions, valid and invalid runs, profiling limits, and comparison results.
