# MEMRUNE Milestone 5A: TCP transport diagnosis

Measured on 2026-10-14. This investigation uses the same Docker host, pinned images, RESP2 benchmark client, request generator, resource limits, and workload parameters as Milestone 4. The original Milestone 4 [summary](../summary.csv) and [report](../report.md) were preserved. Raw JSON for every run is generated under the ignored `results/raw/m5a/` directory. Curated [controlled results](controlled-comparison.csv), [full-matrix comparison](full-comparison.csv), per-phase `runs.csv`, and final [full-matrix runs](final-verified/runs.csv) are retained here.

## Initial findings

- The server processes and awaits responses sequentially per connection, preserving reply order. It uses a `NetworkStream` directly; there is no `BufferedStream` or application-level flush layer. TCP can still combine or delay bytes after a stream write.
- The original `RespWriter` calls `WriteAsync` **three times** for simple and bulk replies: prefix/header, payload, CRLF. Integer and null-bulk replies use one call. A counting-stream regression test verifies the three-call behavior for simple and bulk replies.
- The benchmark client already sets `TcpClient.NoDelay=true`. The original server never set it on accepted sockets. A direct accept probe in the **pinned Linux .NET 10 SDK image** observed `NoDelay=false` and TCP_NODELAY socket option `0`; an automated .NET accept test also verifies the default. The server's default path leaves that setting untouched.
- `NetworkStream.WriteAsync` calls and TCP packets are different units. The code and tests establish API write counts, not packet or acknowledgment counts. The benchmark client writes each request batch once, validates every ordered reply, and uses monotonic `Stopwatch` timing from batch submission to full reply validation.

## Controlled experiments

The fresh baseline was captured **before** transport source changes. Modes were run in this order: baseline; accepted-socket TCP_NODELAY only; coalesced responses only; both. Each mode has six matched configurations on both Memrune and Redis, three measured repetitions per target/configuration. SET and GET use 32-byte values. Depth 1 uses 64 measured commands per client; depth 16 uses 512. All 144 controlled runs were valid, with zero failed operations. [Individual-run CSVs](baseline/runs.csv) are present in each phase directory; the table below uses medians of three runs, in microseconds for latency.

| Workload | Clients | Depth | Memrune mode | Ops/s | P50 µs | P95 µs | P99 µs |
|:--|--:|--:|:--|--:|--:|--:|--:|
| PING | 1 | 1 | Baseline | 23 | 43,998 | 44,222 | 44,437 |
| PING | 1 | 1 | TCP_NODELAY | 7,735 | 119 | 161 | 233 |
| PING | 1 | 1 | Coalesced | 8,834 | 99 | 176 | 217 |
| PING | 1 | 1 | Combined | 7,859 | 105 | 204 | 232 |
| PING | 1 | 16 | Baseline | 364 | 43,968 | 44,365 | 44,442 |
| PING | 1 | 16 | TCP_NODELAY | 16,853 | 550 | 973 | 1,286 |
| PING | 1 | 16 | Coalesced | 363 | 43,974 | 44,378 | 47,855 |
| PING | 1 | 16 | Combined | 30,710 | 313 | 527 | 563 |
| PING | 100 | 1 | Baseline | 2,252 | 44,020 | 45,500 | 48,169 |
| PING | 100 | 1 | TCP_NODELAY | 20,425 | 3,159 | 29,944 | 42,759 |
| PING | 100 | 1 | Coalesced | 57,640 | 1,180 | 1,536 | 16,578 |
| PING | 100 | 1 | Combined | 42,236 | 1,873 | 2,373 | 34,950 |
| PING | 100 | 16 | Baseline | 35,577 | 44,036 | 48,083 | 48,970 |
| PING | 100 | 16 | TCP_NODELAY | 46,905 | 22,163 | 86,535 | 97,914 |
| PING | 100 | 16 | Coalesced | 35,420 | 44,026 | 47,951 | 48,331 |
| PING | 100 | 16 | Combined | 113,186 | 4,854 | 73,886 | 76,417 |
| SET 32 B | 100 | 16 | Baseline | 36,160 | 44,045 | 48,078 | 48,682 |
| SET 32 B | 100 | 16 | TCP_NODELAY | 43,113 | 11,335 | 91,416 | 94,296 |
| SET 32 B | 100 | 16 | Coalesced | 36,164 | 44,003 | 48,256 | 50,211 |
| SET 32 B | 100 | 16 | Combined | 108,870 | 6,141 | 72,494 | 74,828 |
| GET 32 B | 100 | 16 | Baseline | 36,369 | 44,026 | 47,974 | 48,314 |
| GET 32 B | 100 | 16 | TCP_NODELAY | 43,010 | 20,298 | 90,042 | 97,195 |
| GET 32 B | 100 | 16 | Coalesced | 36,262 | 44,040 | 48,282 | 49,060 |
| GET 32 B | 100 | 16 | Combined | 98,236 | 11,863 | 54,890 | 60,084 |

The Redis control medians changed across phases too: 100-client depth-16 PING ranged from 681,255 to 699,454 ops/s; SET 32 B from 490,838 to 589,754; GET 32 B from 501,975 to 608,556. Individual repetitions also vary. For example, combined 100-client depth-16 PING was 113,186 / 134,134 / 106,750 ops/s; baseline was 35,322 / 35,737 / 35,577. Thus small differences between modes should not be treated as precise speedups. The large single-client latency shift and the large combined-versus-baseline pipelined gap exceed this observed variation.

## Full matrix against Redis

The full matrix was run again against the exact final server source, with both transport flags enabled. It contains PING plus 32- and 1,024-byte SET/GET, 1/50/100 clients, depths 1/16, and three repetitions per configuration. **All 180 final runs were valid, with zero failed operations.** The [final summary](final-verified/summary.csv) includes throughput and P50/P95/P99 for all 60 target/configuration pairs; [full-comparison.csv](full-comparison.csv) joins it to the original Milestone 4 medians for every workload. An earlier combined-mode matrix remains in `final/`; `final-verified/` is the run against the final connection-handling source and is used below.

| Workload | Clients | Depth | Memrune ops/s, old → final | Memrune P50 µs, old → final | P95 µs, old → final | P99 µs, old → final | Final Redis ops/s |
|:--|--:|--:|--:|--:|--:|--:|--:|
| PING | 1 | 1 | 23 → 10,677 | 43,983 → 84 | 44,337 → 127 | 44,553 → 194 | 12,501 |
| PING | 100 | 16 | 35,737 → 109,456 | 44,055 → 7,283 | 47,828 → 62,796 | 48,552 → 80,211 | 713,354 |
| SET 32 B | 100 | 16 | 36,225 → 112,440 | 44,058 → 6,343 | 48,095 → 73,928 | 48,520 → 81,368 | 572,900 |
| SET 1,024 B | 100 | 16 | 36,005 → 97,328 | 44,061 → 6,539 | 48,273 → 73,074 | 50,389 → 77,586 | 498,225 |
| GET 32 B | 100 | 16 | 35,942 → 117,324 | 44,025 → 4,444 | 47,866 → 76,099 | 48,245 → 78,425 | 599,238 |
| GET 1,024 B | 100 | 16 | 40,126 → 105,262 | 788 → 6,607 | 6,302 → 65,436 | 47,743 → 73,535 | 421,516 |

Memrune throughput rose in **all 30** configurations, by 2.62× to 493.78× against the historical Milestone 4 result. Yet final P50 was higher in 2 configurations, P95 in 8, and P99 in **10**. Nine P99 regressions were at depth 16; one was 1,024-byte SET with 100 clients at depth 1 (48,257 → 49,442 µs). The worst selected depth-16 P99 values were roughly 73–81 ms, compared with roughly 44–50 ms originally. The [full comparison](full-comparison.csv) lists every improvement and regression. Redis still led the 100-client depth-16 throughput cases shown above; these results do not establish general Redis-level performance.

## Longer matched PING checks

Because the original operation counts now produce short optimized runs, a separate check used 10,000 commands per run for one client/depth 1 and 500,000 for 100 clients/depth 16. Both targets used identical arguments and three repetitions; these numbers are **not** mixed into the A/B medians. All responses validated.

| Configuration | Memrune individual ops/s; median P50/P95/P99 µs | Redis individual ops/s; median P50/P95/P99 µs |
|:--|:--|:--|
| PING, 1 client, depth 1 | 12,978 / 15,098 / 15,009; 57 / 106 / 144 | 13,322 / 15,933 / 15,885; 58 / 85 / 119 |
| PING, 100 clients, depth 16 | 112,842 / 121,617 / 115,144; 7,415 / 61,768 / 68,633 | 622,988 / 649,926 / 644,713; 2,309 / 2,719 / 3,650 |

## Resource and mechanism evidence

A separate, matched 100-client depth-16 PING diagnostic used 51,200 measured commands per repetition. Process CPU and cgroup counters include setup, warmup, and cleanup. Each Memrune mode used a freshly created container under the same 2 CPU / 1 GiB limits. The cgroup memory peak is since container start, not a per-command allocation measure.

| Mode | Memrune process CPU s | Container CPU s | Throttled periods | Cgroup memory peak MiB |
|:--|--:|--:|--:|--:|
| Baseline | 1.77 | 1.83 | 0 | 46.6 |
| TCP_NODELAY | 7.07 | 7.13 | 32 | 32.9 |
| Coalesced | 1.47 | 1.53 | 0 | 31.4 |
| Combined | 3.81 | 3.87 | 11 | 31.7 |

The coalesced writer submits each complete RESP reply in **one API write**, verified by tests, versus three calls in the original simple/bulk writer. It uses a per-connection reusable buffer up to 64 KiB and a temporary array for larger replies. No buffer is shared across clients. TCP_NODELAY alone makes the single-client 44 ms stall disappear, and coalescing alone does the same at depth 1. Coalescing alone leaves depth-16 PING near 44 ms; the combined mode removes that median stall and raises throughput substantially. Together these controlled changes strongly support a **transport-layer bottleneck involving response segmentation and TCP send behavior**. They are consistent with Nagle/delayed-ACK interaction, but **packet and ACK timing were not captured**, so that exact mechanism remains inferred. The minimal runtime image had no `tcpdump`, `ss`, `strace`, `dotnet-counters`, or `dotnet-trace`; no elevated container privileges or diagnostic packages were added.

The increased P95/P99 under load coincides with higher throughput and, in matched runs, 11 combined-mode and 32 TCP_NODELAY-only CPU-throttled periods versus zero in baseline/coalesced-only. CPU quota throttling and queueing are plausible contributors, not a per-reply causal proof. No storage code was changed; PING has no storage access. Parsing allocation, GC, scheduler behavior, per-connection task overhead, and client-side saturation remain unprofiled potential bottlenecks.

## Decision and limitations

Both transport switches remain **opt-in**. The default server behavior stays as it was while the depth-16 P99 regressions and quota effects are investigated. No storage, expiration, command, or protocol parsing change was made. The original RESP writer is retained for a clean control. The new writer preserves binary framing, response order, cancellation, and connection-local failure behavior; automated tests and official `redis-cli` checks passed.

All numbers are local Docker Desktop/WSL2 microbenchmark results. The pipeline latency clock starts before batch submission; later replies include wait behind earlier replies and client validation. Results depend on the client, Docker CPU quota, host load, and very short original repetition lengths. They are not production throughput claims. The next useful evidence would be packet/ACK timing and CPU/GC profiles under longer, matched loads before making either switch a default or changing unrelated engine code.
