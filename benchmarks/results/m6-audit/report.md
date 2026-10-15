# v0.1 audit benchmark smoke run - 2026-10-14

This is a **new, short verification run**, separate from the Milestone 4 and 5A/5B datasets. It checks that the current images and validated client can still measure both targets. It does not replace the full benchmark matrix or establish production performance.

## Environment and method

- Docker Desktop engine 29.8.2 on the local Windows development machine; client container reported Ubuntu 24.04.5 LTS and .NET 10.0.13.
- Compose base file: each server and the client limited to 2 CPUs and 1 GiB; private internal network; no published database ports. Memrune used its **original default transport**. Redis server reported 7.4.11; persistence disabled by Compose.
- Same .NET RESP2 benchmark client for both targets. GET, 32-byte values, 50 clients, pipeline depth 16, 1,024 deterministic preloaded keys, 400 warmup operations, 6,400 measured operations, 3 repetitions each.
- Each run validated all 6,400 ordered replies and cleanup. Setup, warmup, and cleanup are outside operation timing. Latency is client-observed time from batch submission through each reply validation. Every command in a batch shares the submission timestamp. Percentiles are nearest-rank within each run; the table uses the median of each run's metric.

| Target | Median successful ops/s | Median P50 | Median P95 | Median P99 | Failed operations |
| --- | ---: | ---: | ---: | ---: | ---: |
| Memrune | 18,099 | 44.011 ms | 47.925 ms | 50.705 ms | 0 / 19,200 |
| Redis | 607,960 | 1.219 ms | 1.546 ms | 1.686 ms | 0 / 19,200 |

The six individual runs are in [runs.csv](runs.csv). Memrune's individual throughputs were 18,182, 18,099, and 17,597 ops/s; Redis's were 607,960, 607,049, and 618,549 ops/s (rounded). This fresh run is consistent with the previously observed default-transport stall, but it is not packet-level proof of its cause. No CPU, memory, or GC counters were collected for this smoke run; see earlier controlled studies for those measurements. The combined transport mode was checked separately with official redis-cli PING and the short transport demo run below, outside this GET comparison.

## Separate transport demo check

The exact short Demo H commands were also run with one client, pipeline depth 1, 64 PINGs and three repetitions. All six runs were valid. The original transport yielded median 22.7 ops/s and P50/P95/P99 of 43.996/44.146/44.286 ms. The opt-in combined mode yielded median 6,895 ops/s and 0.129/0.212/0.293 ms. These tiny runs verify that the demo sequence works and reproduce the single-client stall; they say nothing about high-concurrency P99 or sustained throughput. Raw JSON is ignored under `benchmarks/results/raw/`.

## Reproduce

From the repository root, with Docker Desktop available:

```powershell
docker compose -f benchmarks/compose.yaml --profile runner build
docker compose -f benchmarks/compose.yaml up -d memrune redis
docker compose -f benchmarks/compose.yaml run --rm benchmark --benchmark --host memrune --port 6380 --workload get --clients 50 --operations 6400 --pipeline 16 --value-size 32 --warmup 400 --repetitions 3 --keys 1024 --output /results/m6-reproduction/memrune-get-v32-c50-p16.json
docker compose -f benchmarks/compose.yaml run --rm benchmark --benchmark --host redis --port 6379 --workload get --clients 50 --operations 6400 --pipeline 16 --value-size 32 --warmup 400 --repetitions 3 --keys 1024 --output /results/m6-reproduction/redis-get-v32-c50-p16.json
docker compose -f benchmarks/compose.yaml down
```

Raw JSON goes under ignored `benchmarks/results/raw/m6-reproduction/`. Do not overwrite the historical curated CSVs when reproducing the full matrix; use a distinct input and output subdirectory as documented in `benchmarks/README.md`. Host load, VM scheduling, Docker version, and short run length can alter results.
