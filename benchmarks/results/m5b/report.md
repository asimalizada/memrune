# MEMRUNE Milestone 5B: tail latency and resource bottlenecks

Measured on 2026-10-14. All 222 individual PING runs included in the [aggregate summary](summary.csv) validated every expected response and the final marker: **0 failed operations**. The per-run CSVs are retained here; detailed JSON is under ignored `results/raw/m5b/`. An initial three-run harness pilot is retained separately and excluded from the summary. The original [Milestone 4](../report.md) and [Milestone 5A](../m5a/report.md) measurements were preserved. No Memrune engine or transport behavior was changed in this milestone.

## Setup and questions

Docker Desktop 29.8.2 on Windows 11 supplied a WSL2 Linux 6.18.54.1 host reporting 16 CPUs and about 16.46 GB memory. Memrune and the benchmark client ran .NET 10.0.13 Release; Redis was official Redis 7.4.11 from the pinned Redis 7 Alpine image. Both database containers had the same 2, 4, or 8 CPU quota and 1 GiB memory limit. The benchmark client was separately limited to 2 CPUs except where noted. The containers used the same private Docker network; database ports were not published. Redis persistence was disabled. Compose overrides change only CPU quotas and the existing Memrune transport flags.

The hypotheses were: (1) the faster transport exposes server CPU quota pressure and cgroup throttling, (2) the RESP benchmark client itself runs out of CPU and contributes to client-observed tail latency, and (3) allocation/GC, ThreadPool scheduling, or socket work contributes to the remaining server cost. PING bypasses storage, so a storage lock cannot explain these PING results. The client timestamps a batch before `WriteAsync` and each reply after complete read and validation. All commands in a pipeline share their batch start time, so P99 includes queueing behind earlier replies and client work. All tests used the same request generator and validator for Memrune and Redis.

## Matched quota matrix

The table gives medians of three **separate** measured repetitions. Latencies are microseconds. `Throttle` is the total server cgroup `nr_throttled` delta across those three invocations, including setup and cleanup. Individual throughput, P50/P95/P99, CPU, pressure, and throttling deltas are in [quota.csv](quota.csv). The short optimized 100-client repetitions often lasted only 0.2-0.5 seconds, so the longer runs below carry more weight for tail conclusions.

| Server CPUs | Target | Mode | Clients | Depth | Ops/s | P50 us | P95 us | P99 us | Throttle |
|--:|:--|:--|--:|--:|--:|--:|--:|--:|--:|
| 2 | Memrune | baseline | 1 | 1 | 23 | 43,992 | 44,256 | 47,926 | 0 |
| 2 | Memrune | nodelay | 1 | 1 | 7,198 | 121 | 192 | 240 | 0 |
| 2 | Memrune | combined | 1 | 1 | 8,039 | 105 | 182 | 223 | 0 |
| 2 | Redis | control | 1 | 1 | 12,092 | 79 | 106 | 152 | 0 |
| 2 | Memrune | baseline | 50 | 16 | 17,766 | 44,044 | 48,215 | 48,777 | 0 |
| 2 | Memrune | nodelay | 50 | 16 | 47,235 | 13,533 | 37,252 | 50,093 | 13 |
| 2 | Memrune | combined | 50 | 16 | 117,058 | 5,193 | 9,587 | 42,127 | 4 |
| 2 | Redis | control | 50 | 16 | 613,456 | 1,175 | 1,988 | 2,802 | 0 |
| 2 | Memrune | baseline | 100 | 16 | 35,610 | 44,038 | 47,922 | 48,303 | 0 |
| 2 | Memrune | nodelay | 100 | 16 | 45,003 | 24,995 | 89,471 | 98,744 | 30 |
| 2 | Memrune | combined | 100 | 16 | 117,929 | 8,316 | 74,162 | 78,551 | 12 |
| 2 | Redis | control | 100 | 16 | 693,859 | 2,258 | 3,107 | 4,091 | 0 |
| 4 | Memrune | baseline | 1 | 1 | 23 | 43,996 | 44,227 | 47,676 | 0 |
| 4 | Memrune | nodelay | 1 | 1 | 7,781 | 117 | 194 | 238 | 0 |
| 4 | Memrune | combined | 1 | 1 | 8,638 | 97 | 159 | 269 | 0 |
| 4 | Redis | control | 1 | 1 | 12,509 | 78 | 114 | 140 | 0 |
| 4 | Memrune | baseline | 50 | 16 | 18,164 | 44,020 | 45,367 | 45,890 | 0 |
| 4 | Memrune | nodelay | 50 | 16 | 78,524 | 7,408 | 42,235 | 47,459 | 2 |
| 4 | Memrune | combined | 50 | 16 | 232,529 | 3,229 | 4,428 | 9,467 | 0 |
| 4 | Redis | control | 50 | 16 | 694,763 | 1,083 | 1,407 | 2,248 | 0 |
| 4 | Memrune | baseline | 100 | 16 | 35,376 | 44,039 | 48,039 | 48,480 | 0 |
| 4 | Memrune | nodelay | 100 | 16 | 95,314 | 7,973 | 72,067 | 75,099 | 14 |
| 4 | Memrune | combined | 100 | 16 | 228,125 | 2,793 | 28,675 | 33,475 | 4 |
| 4 | Redis | control | 100 | 16 | 699,465 | 2,223 | 3,175 | 4,138 | 0 |
| 8 | Memrune | baseline | 1 | 1 | 23 | 44,010 | 44,435 | 47,730 | 0 |
| 8 | Memrune | nodelay | 1 | 1 | 7,285 | 117 | 175 | 242 | 0 |
| 8 | Memrune | combined | 1 | 1 | 8,904 | 99 | 170 | 204 | 0 |
| 8 | Redis | control | 1 | 1 | 11,524 | 79 | 123 | 185 | 0 |
| 8 | Memrune | baseline | 50 | 16 | 18,165 | 44,015 | 45,270 | 45,679 | 0 |
| 8 | Memrune | nodelay | 50 | 16 | 125,270 | 4,803 | 9,232 | 36,339 | 1 |
| 8 | Memrune | combined | 50 | 16 | 217,309 | 358 | 13,237 | 24,559 | 0 |
| 8 | Redis | control | 50 | 16 | 687,377 | 1,098 | 1,415 | 2,348 | 0 |
| 8 | Memrune | baseline | 100 | 16 | 35,452 | 44,057 | 47,945 | 48,335 | 0 |
| 8 | Memrune | nodelay | 100 | 16 | 166,899 | 7,564 | 21,531 | 32,690 | 7 |
| 8 | Memrune | combined | 100 | 16 | 264,993 | 1,856 | 20,373 | 38,159 | 0 |
| 8 | Redis | control | 100 | 16 | 651,985 | 2,271 | 3,324 | 4,536 | 0 |

The original roughly 44 ms P50 persists at every CPU quota, with no server throttling; it is a transport floor, consistent with Milestone 5A. Combined mode removes that floor and raises 100-client throughput at 2 CPUs by about 3.3 times, but P99 rises from 48.3 to 78.6 ms. Raising server quota reduces combined-mode P99 in the short matrix, but those subsecond runs understate long-run tails.

## Longer high-concurrency confirmation

Each row below represents three repetitions of **500,000 validated PINGs** at 100 clients/depth 16. Ops/s and latency are medians of individual run metrics. Throttled periods and throttled seconds are totals across three server counter-delta windows. [long2.csv](long2.csv), [long4.csv](long4.csv), [long8client2.csv](long8client2.csv), [long8client8.csv](long8client8.csv), and [long4client8.csv](long4client8.csv) retain individual runs.

| Server CPUs | Client CPUs | Target/mode | Ops/s | P50 us | P95 us | P99 us | Server throttle periods / seconds |
|--:|--:|:--|--:|--:|--:|--:|:--|
| 2 | 2 | Memrune baseline | 35,985 | 44,041 | 47,900 | 48,477 | 0 / 0 |
| 2 | 2 | Memrune nodelay | 47,910 | 24,848 | 79,668 | 88,917 | 269 / 66.39 |
| 2 | 2 | Memrune combined | 118,470 | 5,958 | 71,933 | 76,346 | 106 / 34.87 |
| 2 | 2 | Redis control | 630,278 | 2,257 | 2,757 | 10,339 | 0 / 0 |
| 4 | 2 | Memrune baseline | 35,960 | 44,049 | 47,840 | 48,404 | 0 / 0 |
| 4 | 2 | Memrune nodelay | 76,788 | 14,075 | 72,017 | 80,291 | 120 / 47.69 |
| 4 | 2 | Memrune combined | 201,715 | 5,312 | 33,506 | 58,500 | 48 / 11.12 |
| 4 | 2 | Redis control | 603,886 | 2,280 | 2,862 | 17,327 | 0 / 0 |
| 4 | 8 | Memrune combined | 213,594 | 6,784 | 16,181 | 58,952 | 69 / 17.82 |
| 4 | 8 | Redis control | 669,284 | 2,295 | 2,957 | 3,743 | 0 / 0 |
| 8 | 2 | Memrune combined | 277,475 | 3,045 | 15,378 | 58,930 | 5 / 0.16 |
| 8 | 2 | Redis control | 630,744 | 2,298 | 2,723 | 10,048 | 0 / 0 |
| 8 | 8 | Memrune combined | 423,859 | 3,634 | 6,280 | 8,429 | 20 / 0.63 |
| 8 | 8 | Redis control | 665,207 | 2,299 | 3,087 | 3,829 | 0 / 0 |

The long 2-CPU combined P99 values were 78.0, 76.3, and 75.6 ms; baseline was 48.5, 48.5, and 48.4 ms. At 4 CPUs, combined P99 was 58.5, 60.5, and 53.4 ms: better than 2 CPUs but still above the 48.4 ms baseline. Server CPU counter totals over those three long runs were 15.09 seconds for 2-CPU baseline, 61.46 seconds for 2-CPU nodelay, and 24.85 seconds for 2-CPU combined; the higher-work modes did considerably more CPU work in less wall time. These server CPU totals include setup/warmup/cleanup. The cgroup throttling reduction under higher quotas and corresponding tail improvement support server quota pressure as a contributor. They do not prove each outlier was caused by a throttle event.

Server cgroup CPU pressure `some` deltas across the three 2-CPU long runs were 0.03 seconds for baseline, 6.81 for nodelay, and 3.15 for combined. At 4 CPUs they were 0.04, 3.44, and 0.85 seconds. These counters likewise span whole benchmark invocations and reinforce the resource-pressure comparison without identifying an individual delayed response.

## Independent benchmark-client limit

With the server fixed at 4 CPUs, the short combined-mode 100-client medians were 228k/231k/221k ops/s with a 2/4/8 CPU client. Increasing client quota did not reliably raise throughput there; [client4.csv](client4.csv) and [client8.csv](client8.csv) retain the runs. Long 4-CPU-server runs likewise remained about 202k versus 214k ops/s at 2 versus 8 client CPUs, both with about 59 ms P99. Server quota pressure remained.

At **8 server CPUs**, client quota mattered substantially. A second long experiment added client cgroup deltas measured precisely around the operation phase:

| Server CPUs | Client CPUs | Ops/s | P50 us | P95 us | P99 us | Client throttle periods / seconds | Server throttle periods / seconds |
|--:|--:|--:|--:|--:|--:|--:|:--|
| 2 | 2 | 116,531 | 4,150 | 76,098 | 79,602 | 52 / 5.22 | 105 / 40.01 |
| 8 | 2 | 272,642 | 3,050 | 18,160 | 49,533 | 54 / 4.59 | 3 / 0.32 |
| 8 | 8 | 388,678 | 4,006 | 6,119 | 9,369 | 0 / 0 | 23 / 1.10 |

The 8-server/2-client individual client throttling counts were 19, 20, and 15, while the 8-server/8-client counts were 0, 0, and 0. The corresponding P99 values were 49.5/60.8/47.6 ms versus 14.3/7.5/9.4 ms. The 8-server/8-client server still had occasional throttled periods but much lower P99. This directly rejects **server throttling as the sole explanation**. Client scheduling and cgroup pressure enter the reported client-observed latency. These additional runs are in [clientcgroup2.csv](clientcgroup2.csv), [clientcgroup8.csv](clientcgroup8.csv), and [clientcgroup2server2.csv](clientcgroup2server2.csv). The two database targets had matched server quotas in every run. Redis controls also varied with run length and client quota; the study does not identify a universal throughput ratio.

## Profiling evidence and limits

A separate 4-CPU-server, combined-mode run validated one million PINGs at about 221k ops/s in 4.52 seconds, with 539 MB of **client** allocations recorded during the measured phase (about 539 bytes per operation). A `dotnet-counters` System.Runtime capture overlapping that run recorded about 1.77 GB server allocations, 210 Gen0, 2 Gen1, 1 Gen2 collections, and 0.313 seconds of reported GC pause time across its 19 one-second samples. During the busy seconds, server process CPU time was roughly 3.5 seconds system plus 0.5 seconds user per second. The ThreadPool queue stayed near zero and reported monitor lock contentions were zero. These samples support allocation/GC and kernel-side work as areas for further investigation, but do not assign a particular reply delay to GC or identify a server method as the CPU hotspot. The server stack trace was dominated by idle/wait frames and had missing symbols; sampling lowered measured throughput, so it was excluded from the primary comparisons. `/proc/1/io` syscall counters were unavailable. No packet capture or privileged kernel profiling was done.

## Decision

**No server optimization was implemented.** The existing transport options already expose a large throughput/latency tradeoff. The current evidence identifies resource pressure on both sides and substantial server CPU/allocations, but does not isolate a safe, narrow server change with a proven tail benefit. The original transport remains the normal default; TCP_NODELAY and coalescing remain opt-in. This is a conservative default decision, not an endorsement of the original 44 ms latency. Coalesced-only mode was measured in Milestone 5A but was outside this three-mode quota matrix; it warrants a longer tail study before any default switch.

The benchmark tool was changed only to expose measured-phase client process and cgroup resource counters. It still validates every response identically for both targets. The harness and Compose overrides are diagnostic files. No storage, expiration, RESP reader/writer, TCP server, or command semantics changed. A useful next approved experiment is a CPU-attributed profile with kernel/symbol support, plus response write/syscall counts and matched long workloads at fixed server/client quotas. Allocation and GC events should then be aligned with response-latency spikes before choosing a code-level optimization.

These Docker Desktop/WSL2 local microbenchmarks use PING only, a fixed pipeline depth, short or moderate run lengths, and sequential targets. Container cgroup counters are not hardware utilization measurements. Host scheduling, JIT/GC state, virtualized networking, and benchmark-client CPU affect the observations. Results do not describe general production behavior.
