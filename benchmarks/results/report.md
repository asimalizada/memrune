# MEMRUNE Milestone 4: local Docker benchmark

Measured on 2026-10-14. These are local microbenchmark observations, not universal production performance or an optimization of Memrune. The full matrix has [180 individual runs](runs.csv) and [60 target/configuration medians](summary.csv); [resource counters](resources.csv) come from a separate representative run. Raw JSON is generated in the ignored `raw/` directory by the documented commands.

## Environment and method

- Docker Desktop 29.8.2, Linux kernel `6.18.54.1-microsoft-standard-WSL2`, x86-64 Linux VM reporting 16 CPUs and 16,462,544,896 bytes of memory. Host CPU model was not available to the benchmarking process.
- Memrune and the benchmark client: .NET runtime 10.0.13; Memrune published in Release mode from the multi-stage Dockerfile. Redis: official `redis:7-alpine` image, Redis 7.4.11, jemalloc 5.3.0.
- Both database containers: 2 CPU quota, 1 GiB memory limit, same private Docker network, no published ports. The client container has the same quota and limit. Memrune binds `0.0.0.0` explicitly inside that network; its normal default remains loopback only.
- Redis ran with `--save "" --appendonly no`, disabling RDB save points and AOF. Its protected mode was disabled only inside the private network. Memrune has no persistence.
- Identical .NET RESP2 client, request generator, connection strategy, and response validator for both targets. SET measures overwrites; GET uses a preloaded 1,024-key dataset. Setup, warmup, connections, marker checks, cleanup, and JSON output are excluded from measured elapsed time.
- 1, 50, or 100 clients; pipeline depth 1 or 16; PING and 32- or 1,024-byte SET/GET values. Each configuration ran 3 measured repetitions. At depth 1 each client issued 64 measured commands; at depth 16 each issued 512. Warmup was 8 commands per client. Target order alternated by configuration. See [the benchmark guide](../README.md) for commands and metric definitions.

All 180 measured runs were valid: **1,304,640/1,304,640 expected responses validated on each target, 0 failed operations**, plus post-measurement marker replies used to detect extra responses. The 30 configurations were run sequentially, so they do not represent simultaneous load on both servers.

## Selected results

The table shows the median across three repetitions for each target. Throughput is successful operations per second. Latencies are client-observed microseconds from batch submission through full reply receipt and validation; later replies in a pipeline include their time behind earlier replies. Complete P50/P95/P99 data for every configuration are in `summary.csv`.

| Workload | Bytes | Clients | Depth | Memrune ops/s | Redis ops/s | Memrune P50 / P99 µs | Redis P50 / P99 µs |
|:--|--:|--:|--:|--:|--:|--:|--:|
| PING | 0 | 1 | 1 | 23 | 12,496 | 43,983 / 44,553 | 80 / 111 |
| PING | 0 | 1 | 16 | 364 | 145,038 | 43,981 / 44,356 | 96 / 137 |
| PING | 0 | 50 | 1 | 1,136 | 37,068 | 44,012 / 45,726 | 970 / 19,595 |
| PING | 0 | 50 | 16 | 17,898 | 676,520 | 44,007 / 46,007 | 1,119 / 2,300 |
| PING | 0 | 100 | 1 | 2,244 | 37,181 | 44,025 / 48,349 | 1,986 / 33,652 |
| PING | 0 | 100 | 16 | 35,737 | 704,209 | 44,055 / 48,552 | 2,264 / 3,672 |
| SET | 32 | 100 | 16 | 36,225 | 573,943 | 44,058 / 48,520 | 2,663 / 5,292 |
| SET | 1,024 | 100 | 16 | 36,005 | 374,500 | 44,061 / 50,389 | 3,958 / 7,224 |
| GET | 32 | 100 | 16 | 35,942 | 600,463 | 44,025 / 48,245 | 2,555 / 5,513 |
| GET | 1,024 | 100 | 16 | 40,126 | 340,445 | 788 / 47,743 | 4,605 / 9,479 |

For the 100-client, depth-16 configurations above, Redis's throughput median was 19.7× Memrune for PING, 15.8× for 32-byte SET, 10.4× for 1,024-byte SET, 16.7× for 32-byte GET, and 8.5× for 1,024-byte GET. These ratios are specific to this client, network, and run. A low Memrune GET P50 at 1,024 bytes coexisted with a roughly 48 ms P99; the full latency distribution matters.

## Repeatability and resources

Representative individual throughputs in run order, in ops/s:

| Configuration | Memrune runs | Redis runs |
|:--|:--|:--|
| PING, 1 client, depth 1 | 23, 23, 23 | 11,067, 12,691, 12,496 |
| PING, 100 clients, depth 16 | 35,737, 36,034, 35,465 | 704,209, 708,422, 695,997 |
| SET 1,024 bytes, 100 clients, depth 16 | 36,123, 36,005, 35,073 | 315,335, 374,500, 446,679 |
| GET 1,024 bytes, 100 clients, depth 16 | 40,092, 40,126, 40,388 | 345,339, 340,445, 305,097 |

The separate 100-client, depth-16, 1,024-byte GET resource run issued 51,200 measured commands per repetition, plus setup and warmup. Linux `/proc/1/stat` recorded **5.31 process CPU seconds for Memrune and 0.40 for Redis** across all three repetitions; cgroup `cpu.stat` recorded **5.38 and 0.47 container CPU seconds**. The cgroup memory peaks since container startup were **41.3 MiB and 39.8 MiB**, respectively. The resource run's median measured throughput was 36,832 ops/s for Memrune and 451,457 ops/s for Redis; it is separate from the matrix table. CPU counters include setup, warmup, cleanup, and waiting, and memory peaks include previous matrix activity. They do not isolate per-command cost. A `docker stats --no-stream` sample during the matrix showed roughly 34 MiB Memrune and 6.8 MiB Redis Docker-reported usage, but it was an instant sample while the targets were not at matched workload points, so it is not a direct memory comparison. .NET allocation and GC counters were unavailable in the minimal runtime image; no values are inferred for them.

## Bottleneck assessment

**Observed:** Memrune's PING, SET, and GET with one client and depth 1 all had roughly 44 ms P50, including the workload with no storage access. At 100 clients and depth 16, throughput was much higher, while the 32-byte workload P50 stayed near 44 ms. This is strong evidence that request/response transport behavior dominates these measurements. It does not support calling the store's single lock the primary bottleneck.

**Suspected, not yet proven by packet capture:** Memrune's RESP writer issues separate socket writes for reply prefix, payload, and CRLF; accepted server sockets use the default `TcpClient.NoDelay` setting. Interaction with TCP Nagle and delayed acknowledgments is consistent with the repeated ~44 ms stalls. No engine changes were made to test or fix this hypothesis in Milestone 4.

**Code-level candidates requiring profiling:** the RESP reader allocates an argument array, a byte array per bulk argument, and a numeric buffer/string per parsed length; command dispatch decodes the command name; the writer allocates ASCII line bytes and writes reply fragments separately; storage uses one lock and copies owned key/value bytes. These costs are visible in code but their shares of measured CPU or latency were not quantified. `dotnet-counters` and `dotnet-trace` were absent from the runtime image; a future controlled profiling run should measure allocations, GC, syscall/write counts, CPU stacks, and lock contention before changing these paths.

## Limits and next experiments

The client measures batch submission through ordered response validation, so latency includes client scheduling, TCP, server queueing, and client parsing. Pipelined command latencies share one batch start time. This is a local Docker Desktop/WSL2 microbenchmark with a small fixed dataset, no mixed traffic, no expiration, no persistence, short repetitions, and sequential targets. CPU quotas are equal, but implementation languages, memory managers, response segmentation, and server architecture differ. The benchmark client itself can cap throughput; especially Redis's results should be checked with client CPU diagnostics before claiming a server limit. Power state, host load, Docker versions, and network stack settings can change results.

Recommended optimization experiments for the next approved milestone, ranked by expected usefulness:

1. **Test the transport hypothesis:** capture TCP segments/ACK timing and compare a controlled server `NoDelay` or contiguous-response-write variant against the unchanged baseline. Keep response correctness tests and identical workload parameters.
2. **Measure syscall and allocation profiles:** add diagnostic tooling to a separate image or run `dotnet-trace`/counters externally; attribute CPU, allocation, and GC by call path before editing parser/writer code.
3. **Isolate the benchmark client ceiling:** measure client CPU saturation and run a second validated client implementation or split client load across containers, without changing the two targets asymmetrically.
4. **Test storage contention only after transport effects are controlled:** compare PING, GET and SET with independent versus shared keys while collecting contention/CPU evidence.
