# MEMRUNE

MEMRUNE is an experimental Redis-compatible in-memory database engine written from scratch in C# and .NET 10 for the Underbyte technical video project. It is not production-ready or a full Redis implementation.

## Current capabilities

- A loopback-only asynchronous TCP server with RESP2 arrays of bulk strings, partial-read handling, pipelined requests, and per-connection ordered responses.
- Binary-safe PING, ECHO, SET, GET, DEL, EXISTS, EXPIRE, TTL, QUIT, and SET with EX or PX.
- A thread-safe in-memory store with lazy expiration and an injectable `TimeProvider`.
- A bounded active-client count, connection-local protocol errors, cancellation of active clients, and a separate diagnostic load utility.
- A Docker-based RESP2 benchmark client for local comparisons with Redis 7. See [benchmark instructions](benchmarks/README.md).
- Opt-in TCP transport experiments for accepted-socket TCP_NODELAY and one-write RESP responses. See [Milestone 5A methods and results](benchmarks/TRANSPORT.md).
- A controlled Milestone 5B investigation of server/client CPU quotas and pipelined PING tail latency. See [methods](benchmarks/TAIL.md) and [actual results](benchmarks/results/m5b/report.md).

`src/Memrune.Protocol` handles framing, `src/Memrune.Core` handles commands and storage, and `src/Memrune.Server` owns the listener and connection lifecycle. `tests/Memrune.Tests` contains deterministic storage tests and real TCP tests on ephemeral ports. `tools/Memrune.LoadTest` has the original raw-TCP PING diagnostic mode and a separate `--benchmark` mode that uses the same RESP2 client for both Memrune and Redis.

## Concurrency and reliability

The store uses a `Dictionary<byte[], Entry>` with content-based key equality and one lock. Each storage operation, including multi-key DEL and EXISTS, is atomic. Keys and values are copied when stored; values are copied when returned. This is simple and correct for the current scope, but unrelated keys still serialize on the same lock. No storage lock is held during network I/O.

Each client has its own RESP reader buffer and sequential command loop. Up to 512 active clients are admitted by default, which is above the 100-client test target. A client accepted beyond that limit receives `-ERR max number of clients reached` and is closed. The OS TCP backlog may refuse connections before the server can send that error under heavier overload. Completed client tasks leave the active-client map; shutdown closes active sockets and awaits their tasks. Expected disconnects stay local; unexpected client failures are logged without stored values.

RESP frame limits remain 1,024 arguments, 1 MiB per bulk string, and 4 MiB of bulk payload per command. Malformed frames receive a RESP protocol error and close only that connection because resynchronization is unsafe. Invalid command arguments return errors while keeping the connection usable.

Expiration uses UTC deadlines from `TimeProvider`. At a deadline, a key is logically absent. Accessing it physically removes it from the dictionary; untouched expired entries can continue occupying memory. There is no background cleanup or total memory cap for stored data.

## Build and test

Install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet restore Memrune.slnx
dotnet build Memrune.slnx
dotnet test Memrune.slnx
```

The test project uses xUnit and the .NET test SDK from NuGet.

## Run and connect

Start the server in one terminal:

```powershell
dotnet run --project src/Memrune.Server
```

It listens on `127.0.0.1:6380`. Use `dotnet run --project src/Memrune.Server -- --port 6381` for another port. `--bind IPv4-address` explicitly selects another interface for isolated container experiments. Press Ctrl+C to stop it.

For transport experiments, add `--tcp-nodelay`, `--coalesce-responses`, or both after `--`. The original TCP and response-write behavior remains the default: Milestone 5B measured a high-concurrency P99 regression at the original two-CPU benchmark quota and found both server and benchmark-client resource pressure. These flags do not change command semantics.

With the official Redis CLI installed, run in another terminal:

```powershell
redis-cli -h 127.0.0.1 -p 6380 PING
redis-cli -h 127.0.0.1 -p 6380 SET demo "Hello Memrune"
redis-cli -h 127.0.0.1 -p 6380 GET demo
redis-cli -h 127.0.0.1 -p 6380 EXISTS demo demo
redis-cli -h 127.0.0.1 -p 6380 EXPIRE demo 60
redis-cli -h 127.0.0.1 -p 6380 TTL demo
redis-cli -h 127.0.0.1 -p 6380 DEL demo
```

The same commands can use Docker Desktop by prefixing each command with `docker run --rm --network host redis:7-alpine` if that host-network pattern works on your machine.

## Reproduce the 100-client load run

Build in Release mode and start a separate server on port 6381 in one terminal:

```powershell
dotnet build Memrune.slnx -c Release
dotnet run --project src/Memrune.Server -c Release --no-build -- --port 6381
```

Run the diagnostic utility in another terminal:

```powershell
dotnet run --project tools/Memrune.LoadTest -c Release --no-build -- --port 6381 --clients 100 --operations 1000
```

`--operations` is the number of sequential PING requests per client. The utility reports validated responses, incomplete or failed operations, elapsed time, and successful operations per second. It starts clients concurrently and uses a two-minute overall timeout. Results depend on the machine, OS, build configuration, and other local work; they are not a Redis comparison.

## Supported commands and limitations

- `PING` returns `PONG`; `PING message` and `ECHO message` return bulk strings.
- `QUIT` returns `OK` and closes the connection.
- `SET key value [EX seconds | PX milliseconds]` stores or overwrites a value. Plain SET clears expiration.
- `GET key` returns a bulk string or a null bulk string.
- `DEL key [key ...]` counts distinct keys removed.
- `EXISTS key [key ...]` counts every occurrence of an existing key.
- `EXPIRE key seconds` returns 1 for an existing key and 0 otherwise; non-positive seconds remove it immediately.
- `TTL key` returns whole remaining seconds, -1 without expiration, or -2 for missing or expired keys.

Command and option names are case insensitive. Only RESP2 arrays of bulk strings are accepted as input. Inline commands, RESP3, authentication, NX/XX, GETEX, PERSIST, persistence, replication, clustering, Pub/Sub, and background expiration cleanup are outside the current scope. The Docker benchmark is a local microbenchmark, not a production performance claim.
