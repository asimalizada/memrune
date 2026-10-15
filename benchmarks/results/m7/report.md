# Milestone 7: performance hardening

Measured on 2026-10-15. This is a local Docker Desktop experiment, not a production performance claim. The normal Memrune transport now enables TCP_NODELAY and coalesces each RESP reply. A focused RESP reader change reduces managed allocations. Both changes preserve the implemented Redis command subset; the four transport modes remain selectable.

## Environment and benchmark integrity

- Windows 11 host, Docker Desktop 29.8.2 with a Linux VM exposing 16 CPUs and 16.46 GB RAM. Host .NET SDK 10.0.401; container .NET runtime 10.0.13; official Redis server 7.4.11. Memrune and the C# benchmark client were built in Release.
- Each database had a 2-CPU quota and 1 GiB container limit on the same private Compose network. The benchmark client had 2 CPUs and 1 GiB for the main experiments, and 8 CPUs for the separate higher-rate experiment. Redis persistence was disabled (`--save "" --appendonly no`). No database ports were published.
- The same RESP2 request generator, TCP connection code, reply parser, and full response validation served both targets. SET and GET used 1,024 deterministic keys and identical values; preloading, warmup, marker checks, and cleanup were outside measured intervals. The benchmark client was changed before the controlled engine before/after runs, then held constant.
- The existing maximum-throughput mode is **closed loop**. It sends the next batch only after validating prior replies, so different servers receive different offered loads. Its batch timestamp is assigned to each command in that batch; P95/P99 include pipeline queueing and client parsing. At saturation, those tails cannot answer equal-offered-rate latency questions.
- The new fixed-rate mode has global monotonic arrival times, one in-flight request per connection, pipeline depth 1, bounded workers, a per-request timeout, and a 20 ms submission-lag limit by default. End-to-end latency runs from scheduled arrival to validated reply. Every scheduled operation is counted; late, unsubmitted, timed-out, and incorrect work invalidate a run. Aggregate medians are omitted for invalid groups. Successful throughput must reach at least 95% of the intended rate.
- Timer and client scheduling are part of fixed-rate latency. In valid 100-client runs at 1,000 to 10,000 ops/s, P99 submission lag was typically about 3.3–3.8 ms, close to total end-to-end P99. These runs resolve overload/validity but cannot isolate sub-millisecond server latency differences. The initial half-second warmup caused first-repetition lag failures in pilots; the final fixed-rate harness uses two seconds. Longer warmup did not eliminate every host scheduling outlier.

The benchmark reply reader also gained a ValueTask buffered-byte fast path before any final engine baseline. In a [20,000-operation Redis PING diagnostic](client-reader-pilot.csv), measured client allocation on valid repetitions fell from roughly 23 MB to 15 MB, while CPU time remained roughly 2.9–3.0 seconds over a two-second measured phase. The after-client pilot had only 3/5 valid repetitions despite a two-second warmup, so it does not establish a throughput improvement. Both database targets and all controlled engine before/after runs used the same final client implementation.

These are **confirmed methodology limits**. Coordinated omission in the closed-loop method is structural; the fixed-rate method keeps scheduled arrivals even when prior requests are slow, but deliberately marks overload invalid rather than inventing latency percentiles. The [50,000 ops/s pipeline-1 pilot](pilot-50000-client8.csv) was invalid even for Redis with an eight-CPU client (100,000 replies validated but below 95% achieved rate and about 244 ms maximum submission lag), so no 50,000 ops/s comparison is published. The short full matrix is useful for scope coverage but more sensitive to startup, host load, and order than the five-repetition controls.

## Transport controls before the parser change

Five repetitions used the same old-server image and final benchmark-client image. Values below are medians of individual run metrics. Latencies are microseconds. The [continuous one-client runs](transport-c1-final.csv) directly reproduce the interactive stall:

| Mode | PING c1/p1 ops/s | P50 | P95 | P99 |
| --- | ---: | ---: | ---: | ---: |
| Original | 22.7 | 43,994 | 44,258 | 44,532 |
| Combined | 8,539 | 103 | 173 | 197 |

At saturation with 100 clients and pipeline 16, [all four modes](transport-saturated-final.csv) behaved differently:

| Mode | Ops/s | P50 | P95 | P99 | Server cgroup throttled periods across 5 runs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Original | 35,430 | 44,097 | 48,128 | 48,645 | 0 |
| TCP_NODELAY only | 38,486 | 35,839 | 79,606 | 89,706 | 53 |
| Coalesced only | 35,581 | 44,036 | 48,093 | 48,703 | 0 |
| Combined | 88,279 | 15,027 | 48,238 | 60,102 | 20 |

The combined mode's saturated P99 is higher than original, while its achieved request rate is about 2.5 times higher. This is not an equal-load latency comparison. At a fixed [1,000 PINGs/s](transport-r1000-final.csv), all five runs were valid for every mode and Redis; P99 medians were 3,610 µs original, 3,660 µs TCP_NODELAY, 3,609 µs coalesced, 3,521 µs combined, and 3,510 µs Redis. At [5,000 PINGs/s](transport-r5000-final.csv), all five original-mode warmups exceeded the 20 ms lag limit; the other modes and Redis were 5/5 valid, with P99 medians 4,478, 4,464, 4,623, and 4,478 µs respectively. None had incorrect measured replies. The baseline invalidity is a result, not a latency number.

Response segmentation plus TCP send behavior is a confirmed transport bottleneck: changing only TCP_NODELAY/coalescing eliminates the continuous ~44 ms stall. A specific Nagle/delayed-ACK packet sequence remains **inferred**, because no packet capture or ACK timing was collected. Defaulting to combined mode is justified by the interactive stall, the 5,000 ops/s validity boundary, and the absence of a clear equal-rate tail regression at tested valid rates. The original and each single-option mode remain available via explicit switches and Compose overrides.

## Profiling and the single engine optimization

The old combined-mode server processed one million validated PINGs during a separate `dotnet-counters` capture. The System.Runtime counter sums were about 1.770 GB of allocations, 210 Gen0 collections, 0.773 s of reported GC pause, 2.80 s user CPU, and 15.38 s system CPU; reported monitor lock contentions were zero. A standalone managed allocation probe isolated about 1,648 bytes per parsed PING request, versus 104 bytes in PING command execution and zero bytes in the coalesced writer when given a reused response. This directly identified reader allocation as a meaningful cost without blaming the storage lock (PING does not access storage).

`RespReader` previously allocated a `Task<int>` for buffered byte reads and a byte array plus ASCII string for each numeric length. It now uses a ValueTask fast path for already-buffered bytes and accumulates checked integer lengths directly. The RESP limits, malformed-length errors, fragmentation behavior, binary payload handling, and command semantics are unchanged. The focused [probe](allocation-probe.csv) fell from 1,648 to 136 managed bytes per parsed PING request (about 92% less). Overflow and all four transport modes gained regression coverage.

A separate post-change million-PING `dotnet-counters` capture recorded about 263 MB of allocation, 29 Gen0 collections, 0.0176 s reported GC pause, 0.20 s user CPU, and 13.82 s system CPU. Under profiling, the validated load changed from 110,860 to 122,563 ops/s. The [counter summary](profile-summary.csv) retains these values; raw counter logs and detailed JSON remain ignored diagnostic artifacts. The counter collection windows include startup/idle samples, warmup, marker checks, and profiler overhead, so their sums are diagnostic and not precise per-command costs. A CPU method-level hotspot was not established; high remaining system CPU points toward networking/kernel work but does not identify a specific syscall or scheduling cause.

## Controlled engine before/after

The transport stayed **combined** and the client image, request generation, two-CPU limits, 1 GiB limits, and cases stayed the same. Five separate runs per case were completed before and after rebuilding only the server. Every listed run validated all expected replies. Values are medians; latency columns are P50/P95/P99 in microseconds:

| Workload | Before ops/s | After ops/s | Change | Before P50/P95/P99 | After P50/P95/P99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| PING, 1 client, depth 1 | 9,138 | 9,763 | +6.8% | 99 / 161 / 209 | 95 / 151 / 194 |
| PING, 100 clients, depth 16 | 82,667 | 93,197 | +12.7% | 15,877 / 49,505 / 65,015 | 13,044 / 45,132 / 51,164 |
| SET 32B, 100 clients, depth 16 | 72,818 | 81,100 | +11.4% | 12,009 / 81,202 / 85,608 | 6,313 / 82,633 / 84,700 |
| GET 32B, 100 clients, depth 16 | 97,781 | 110,077 | +12.6% | 10,747 / 77,004 / 83,608 | 7,313 / 45,999 / 74,223 |
| GET 1024B, 100 clients, depth 16 | 78,869 | 89,851 | +13.9% | 7,364 / 80,068 / 85,789 | 7,262 / 63,184 / 82,635 |

Individual runs, errors, client CPU/allocations/GC, and inclusive server cgroup CPU/throttling deltas are preserved in [before](engine-before-closed.csv) and [after](engine-after-closed.csv). For PING c100/p16, before throughput repetitions were 82,720 / 77,441 / 82,667 / 70,985 / 87,503 ops/s; after they were 93,197 / 105,743 / 95,943 / 89,929 / 92,025. Their respective P99 repetitions were 49,543 / 65,652 / 65,015 / 68,566 / 47,343 µs and 59,054 / 49,936 / 56,173 / 51,164 / 49,871 µs. The median inclusive server CPU for that case fell from 1.52 to 1.31 s per 51,200-operation run; median throttled periods fell from 5 to 4. These inclusive counters also contain setup and cleanup, so they are supporting context rather than per-operation CPU.

The matched-rate [before](engine-before-fixed-c2.csv) and [after](engine-after-fixed-c2.csv) sets have five valid repetitions for each Memrune and Redis PING/SET 32B/GET 32B case at 1,000 and 5,000 ops/s. Memrune P99 medians before → after were PING 3,575 → 3,499 µs at 1,000 and 4,520 → 4,339 µs at 5,000; SET 3,538 → 3,577 and 4,486 → 4,442; GET 3,526 → 3,534 and 4,437 → 4,319. The small regressions in SET/GET at 1,000 are below the observed client scheduling noise. No correctness, throughput, or major tail regression justified reverting this one optimization. No second engine optimization was made.

## Final Redis comparison

The final [full matrix individual runs](final-matrix-combined/runs.csv) and [medians](final-matrix-combined/summary.csv) cover all 1/50/100-client and depth-1/16 combinations for PING, SET 32B/1024B, and GET 32B/1024B. All 180 measured runs were valid with zero failed operations. Both targets used the same final client and two-CPU database quota. Selected closed-loop medians:

| Workload | Memrune ops/s | Redis ops/s | Redis / Memrune | Memrune P50/P95/P99 µs | Redis P50/P95/P99 µs |
| --- | ---: | ---: | ---: | ---: | ---: |
| PING c1/p1 | 8,522 | 8,388 | 0.98× | 98 / 188 / 219 | 117 / 149 / 275 |
| PING c50/p16 | 122,466 | 597,236 | 4.88× | 4,856 / 23,242 / 43,470 | 1,227 / 1,579 / 7,337 |
| PING c100/p16 | 130,419 | 598,144 | 4.59× | 7,855 / 57,736 / 67,328 | 2,324 / 3,125 / 13,948 |
| SET 32B c100/p16 | 130,566 | 541,377 | 4.15× | 4,064 / 73,251 / 82,546 | 2,696 / 3,566 / 4,511 |
| GET 32B c100/p16 | 132,547 | 597,535 | 4.51× | 4,671 / 76,932 / 80,840 | 2,586 / 3,273 / 4,333 |
| GET 1024B c100/p16 | 97,813 | 330,539 | 3.38× | 5,611 / 78,440 / 80,214 | 3,976 / 6,169 / 11,950 |

These closed-loop P99 values are measured at unequal achieved rates. The c1/p1 case has only 64 operations per repetition and does not establish a general Memrune latency win. The full matrix and the five-run controls differ in repetition order and host conditions; do not calculate parser speedup by crossing those datasets.

For equal offered load, the final two-CPU-client [fixed-rate runs](engine-after-fixed-c2.csv) achieved about 1,000 or 5,000 successful ops/s on both targets with 5/5 valid runs in each PING, SET 32B, and GET 32B case. At 5,000 ops/s, median P50/P95/P99 were Memrune 1,840/3,924/4,339 versus Redis 1,818/3,961/4,447 µs for PING; Memrune 1,853/3,997/4,442 versus Redis 1,826/3,927/4,550 for SET; and Memrune 1,856/3,935/4,319 versus Redis 1,843/4,027/4,525 for GET. Client submission-lag P99 was about 3.7–3.8 ms, so these small differences do not establish a server-side latency ordering.

With an eight-CPU client, [10,000 ops/s](final-offered-client8.csv) produced 3/3 valid runs for all three workloads on both targets. At 25,000 ops/s, Redis PING and GET were 3/3 valid and SET was 2/3 valid; Memrune PING was 1/3 valid, and SET/GET were 0/3 valid because warmup exceeded the lag limit. No aggregate matched-load tail is reported at 25,000 for these invalid groups. At 10,000 ops/s the eight-CPU client used about 4.3–4.8 CPU seconds per two-second measured phase, with zero median client cgroup throttling periods; its scheduling-lag P99 remained around 3.5 ms.

## Remaining evidence limits and next experiments

- The optimized server still allocates about 263 MB during a million-PING diagnostic and spends substantial CPU time in system mode. CPU stack attribution and packet/ACK timing remain unavailable here. Possible next work is low-overhead CPU/kernel tracing plus reply-write syscall counts, followed by a measured experiment on the remaining command-response and network costs. These are hypotheses, not implemented changes.
- The storage lock was not implicated by PING and no lock contention appeared in the PING capture. SET/GET lock contention and key-copy CPU costs were not independently quantified; no storage change was made.
- The fixed-rate scheduler has millisecond-scale jitter in Docker Desktop. A more precise load generator or separate physical client would be needed to compare sub-millisecond equal-load server latency. Pipeline depth 16 remains closed-loop; fixed-rate results apply only to depth 1.
- Short closed-loop runs, sequential targets in the full matrix, CPU quota throttling, Docker virtualization, deterministic small datasets, no persistence, and absence of production traffic restrict generalization. Increased hardware/client CPUs and parser code changes are separate effects. The original Milestone 4/5 evidence was preserved and never used as a matched before/after baseline.

## Verification and reproduction

`dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` completed; the latest full run passed 67/67 tests. Docker Release images built. The Docker SIGTERM shutdown check passed. Official `redis-cli` against Memrune returned PONG; echoed `Hello Memrune`; returned OK/value for SET/GET; counted duplicate EXISTS as 2; returned 1 for EXPIRE 0, -2 for expired TTL, and 0 for subsequent DEL; accepted SET PX 10000, returned its value and a positive TTL, then DEL returned 1. PING passed in all four Compose transport modes. Binary payload, framing, pipelining, and concurrency are also covered by automated tests.

Run from the repository root in PowerShell. Use fresh phase/output names because the scripts refuse to overwrite curated evidence:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
docker compose -f benchmarks/compose.yaml --profile runner build
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-m7-fixed.ps1 -Phase your-fixed -Modes baseline,nodelay,coalesced,combined -Workloads ping -Rates 1000,5000 -Repetitions 5 -DurationSeconds 3 -Clients 100 -ClientCpus 2
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-m7-closed.ps1 -Phase your-closed -Modes baseline,nodelay,coalesced,combined -CaseNames ping-c100-p16 -Repetitions 5
docker compose -f benchmarks/compose.yaml -f benchmarks/compose.combined.yaml up -d --force-recreate memrune redis
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/run-matrix.ps1 -ResultSubdirectory m7/your-final-matrix -Repetitions 3
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks/summarize.ps1 -InputSubdirectory m7/your-final-matrix -OutputSubdirectory m7/your-final-matrix
docker compose -f benchmarks/compose.yaml exec -T redis redis-cli -h memrune -p 6380 PING
docker compose -f benchmarks/compose.yaml down
```

The current source tree reproduces the **after** parser results. Repeating the parser before/after control requires the prior reader source or preserved prior server image, rebuilt separately and run with the same harness options; the earlier individual results remain in curated CSV. No historical files were overwritten. The four transport modes remain reproducible from the current source.

To reproduce the managed allocation probe without Docker: `dotnet run -c Release --project benchmarks/AllocationProbe -- 100000`. The [benchmark README](../../README.md) explains both modes and resource-counter boundaries. The original transport remains selectable locally with `--no-tcp-nodelay --no-coalesce-responses`; normal execution stays bound to loopback.
