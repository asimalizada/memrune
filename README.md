# MEMRUNE

MEMRUNE is an experimental Redis-compatible in-memory database written in C# and .NET 10 for the Underbyte video *I Built My Own Redis in C#*. It is a small, inspectable engineering project and a reproducible local performance experiment. It is **not** a full Redis implementation or a production-ready service.

## Implemented subset

| Command | Behavior |
| --- | --- |
| `PING [message]` | `PONG`, or the supplied message as a bulk string |
| `ECHO message` | Bulk string reply |
| `QUIT` | `OK`, then connection close |
| `SET key value [EX seconds \| PX milliseconds]` | Store or overwrite; plain SET clears expiration |
| `GET key` | Bulk string, or null bulk string when absent or expired |
| `DEL key [key ...]` | Count keys actually removed; duplicate keys remove once |
| `EXISTS key [key ...]` | Count each occurrence of an existing key |
| `EXPIRE key seconds` | Set expiration; non-positive values remove an existing key |
| `TTL key` | Whole remaining seconds, `-1` without expiration, `-2` if absent |

Command and option names are case insensitive. Keys and values are binary safe, including empty byte strings. Only RESP2 arrays of bulk strings are accepted as input. See [architecture](docs/ARCHITECTURE.md) for the exact request path and limits.

## Requirements, build, and test

Install the .NET 10 SDK. Docker Desktop is needed for the isolated Redis comparison and the containerized official `redis-cli`; it is not needed to run MEMRUNE locally. From the repository root:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

The test project uses xUnit and the .NET test SDK. It includes storage, RESP, real TCP, transport-mode, concurrency, and benchmark-client validation tests. The Docker SIGTERM regression check is separate from `dotnet test`:

```powershell
docker compose -f benchmarks/compose.yaml build memrune
powershell -NoProfile -ExecutionPolicy Bypass -File tests/check-docker-shutdown.ps1
```

## Run locally and use official redis-cli

In one terminal:

```powershell
dotnet run --project src/Memrune.Server -c Release --no-build
```

The default bind is `127.0.0.1:6380`. `--port 6381` changes the port. Press Ctrl+C to stop. In another terminal, if `redis-cli` is installed:

```powershell
redis-cli -h 127.0.0.1 -p 6380 PING
redis-cli -h 127.0.0.1 -p 6380 ECHO "Hello Memrune"
redis-cli -h 127.0.0.1 -p 6380 SET demo value
redis-cli -h 127.0.0.1 -p 6380 GET demo
redis-cli -h 127.0.0.1 -p 6380 EXISTS demo demo
redis-cli -h 127.0.0.1 -p 6380 DEL demo
```

On a Docker Desktop setup that supports host networking, prefix those commands with `docker run --rm --network host redis:7-alpine`. Alternatively, the isolated Compose environment provides the official CLI without a published database port:

```powershell
docker compose -f benchmarks/compose.yaml up -d memrune redis
docker compose -f benchmarks/compose.yaml exec -T redis redis-cli -h memrune -p 6380 PING
```

The Compose server explicitly binds `0.0.0.0` **inside its private Docker network**. The base Compose file explicitly selects the historical original transport; add `-f benchmarks/compose.combined.yaml` to the Compose startup command for the current normal transport. Normal execution remains loopback only. Do not change the network isolation or publish these unauthenticated services to an untrusted network.

## Concurrency, reliability, and transport modes

One asynchronous loop handles each admitted client and preserves response order on that connection. The store uses content-based byte-array keys and one lock, so each storage command is atomic; operations on unrelated keys also serialize. No storage lock spans network I/O. Incoming keys and values are copied into the store, and GET returns a copy. Expiration is lazy: an expired key is logically absent, while an untouched expired entry can still occupy memory. A maximum of 512 active clients is admitted by default. Frames are limited to 1,024 arguments, 1 MiB per argument, and 4 MiB of bulk payload. Malformed framing closes the affected connection after a protocol error. Shutdown cancels active sessions and closes their sockets.

TCP_NODELAY and coalesced RESP replies are now the normal transport defaults. The default bind remains loopback. The four modes remain selectable for experiments:

```powershell
# Normal default: TCP_NODELAY and coalesced replies
dotnet run --project src/Memrune.Server -c Release --no-build
# Original transport
dotnet run --project src/Memrune.Server -c Release --no-build -- --no-tcp-nodelay --no-coalesce-responses
# TCP_NODELAY only
dotnet run --project src/Memrune.Server -c Release --no-build -- --tcp-nodelay --no-coalesce-responses
# Coalescing only
dotnet run --project src/Memrune.Server -c Release --no-build -- --no-tcp-nodelay --coalesce-responses
```

The Milestone 7 controlled comparison found a roughly 44 ms continuous single-client PING stall with the original transport. At 100 clients and a fixed offer of 5,000 PINGs/s, all five original-mode runs failed the submission-lag validity check; the other three modes completed. Combined mode improved saturated throughput but had higher saturated P99 under a two-CPU quota. At a valid 1,000 PINGs/s offer, the four modes had similar client-observed tails. These local results support the new default for an interactive experimental server; they do not prove lower latency at every load. The prior [transport](benchmarks/TRANSPORT.md) and [tail](benchmarks/TAIL.md) reports describe historical defaults. See the [Milestone 7 report](benchmarks/results/m7/report.md) for individual runs, matched-load limits, and parser optimization results. Flags do not alter command semantics.

## Load and benchmark reproduction

The diagnostic PING utility is separate from the Memrune-versus-Redis benchmark. With a local server running on port 6380:

```powershell
dotnet run --project tools/Memrune.LoadTest -c Release --no-build -- --port 6380 --clients 100 --operations 1000
```

`--operations` here is per client. The tool validates replies and reports success, errors, elapsed time, and throughput; it is not a Redis comparison.

For an isolated comparison, build and start both Docker targets, then run one matched configuration on each:

```powershell
docker compose -f benchmarks/compose.yaml --profile runner build
docker compose -f benchmarks/compose.yaml -f benchmarks/compose.combined.yaml up -d memrune redis
docker compose -f benchmarks/compose.yaml run --rm benchmark --benchmark --host memrune --port 6380 --workload ping --clients 50 --operations 1600 --pipeline 16 --value-size 0 --warmup 400 --repetitions 3 --keys 1024 --output /results/m7-demo-memrune.json
docker compose -f benchmarks/compose.yaml run --rm benchmark --benchmark --host redis --port 6379 --workload ping --clients 50 --operations 1600 --pipeline 16 --value-size 0 --warmup 400 --repetitions 3 --keys 1024 --output /results/m7-demo-redis.json
docker compose -f benchmarks/compose.yaml down
```

The benchmark tool also has a bounded fixed-rate mode for matched offered-load latency. It schedules each request with a monotonic clock, supports pipeline depth 1, validates every reply, and invalidates runs that miss the rate or submission-lag bound. The closed-loop mode remains useful for maximum-throughput measurements. Run the [Milestone 7 fixed-rate harness](benchmarks/run-m7-fixed.ps1) or see [benchmark methods](benchmarks/README.md) for matching Docker commands and caveats.
Detailed methodology, full matrix commands, resource-counter limitations, and historical curated results are in [benchmarks/README.md](benchmarks/README.md). JSON goes to ignored `benchmarks/results/raw/`. Setup, warmup, cleanup, and connection establishment are outside measured intervals. Every response is validated; invalid runs have no aggregate median. Reported pipeline latencies are client-observed time from batch submission through each reply's validation, including queueing behind earlier requests. Local microbenchmarks do not predict production performance. The [recording guide](docs/VIDEO_DEMO.md) gives shorter scenarios and expected behavior.

## Known limitations and release decisions

There is no authentication, TLS, persistence, total storage memory cap, idle timeout, or background expiration cleanup. Slow clients can hold admitted slots; up to 512 can be active, and the OS backlog can reject overload before an error reply. The protocol subset excludes RESP3, inline commands, and most Redis commands. The service is experimental and must not be exposed to the public Internet. Results depend on Docker Desktop, host load, CPU limits, and runtime versions.

The repository owner must choose and add a license before advertising the repository as open source. No license or authorship has been assumed here.
