# MEMRUNE

MEMRUNE is an experimental Redis-compatible in-memory database engine written from scratch in C# and .NET 10 for the Underbyte technical video project. It is **not** a full Redis implementation.

## Status and architecture

Milestone 2 adds binary-safe in-memory storage and lazy expiration to the Milestone 1 TCP/RESP2 server.

- `src/Memrune.Protocol` parses RESP2 arrays of bulk strings and writes simple strings, bulk strings (including null), integers, and errors.
- `src/Memrune.Core` validates commands and owns the key-value store. It does not depend on TCP.
- `src/Memrune.Server` accepts clients asynchronously on `127.0.0.1` and shares one store across connections.
- `tests/Memrune.Tests` contains parser, storage, and real TCP tests. TCP tests use ephemeral ports.

The store uses a `Dictionary<byte[], Entry>` with content-based key equality and one lock. Each command's storage operation is atomic; no lock is held during network I/O. This simple lock limits parallel throughput but keeps multi-key counts and same-key mutations correct. Keys and values are copied when stored; values are copied when returned.

Expiration uses `TimeProvider` and UTC deadlines. A key is logically absent at its deadline. Accessing it removes it physically from the dictionary; an untouched expired key can occupy memory until it is accessed. There is no background cleanup in this milestone.

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

It listens on `127.0.0.1:6380`. Use `dotnet run --project src/Memrune.Server -- --port 6381` to select another port. Press Ctrl+C to stop it.

With the official Redis CLI installed, run these in another terminal:

```powershell
redis-cli -h 127.0.0.1 -p 6380 PING
redis-cli -h 127.0.0.1 -p 6380 SET demo "Hello Memrune"
redis-cli -h 127.0.0.1 -p 6380 GET demo
redis-cli -h 127.0.0.1 -p 6380 EXISTS demo demo
redis-cli -h 127.0.0.1 -p 6380 EXPIRE demo 60
redis-cli -h 127.0.0.1 -p 6380 TTL demo
redis-cli -h 127.0.0.1 -p 6380 DEL demo
```

If you use Docker Desktop and the local host-network pattern works on your machine, prefix each `redis-cli` command with `docker run --rm --network host redis:7-alpine`. For example:

```powershell
docker run --rm --network host redis:7-alpine redis-cli -h 127.0.0.1 -p 6380 SET demo "Hello Memrune"
docker run --rm --network host redis:7-alpine redis-cli -h 127.0.0.1 -p 6380 GET demo
docker run --rm --network host redis:7-alpine redis-cli -h 127.0.0.1 -p 6380 EXISTS demo demo
docker run --rm --network host redis:7-alpine redis-cli -h 127.0.0.1 -p 6380 EXPIRE demo 60
docker run --rm --network host redis:7-alpine redis-cli -h 127.0.0.1 -p 6380 TTL demo
docker run --rm --network host redis:7-alpine redis-cli -h 127.0.0.1 -p 6380 DEL demo
```

## Commands

- `PING` returns `PONG`; `PING message` returns the message as a bulk string.
- `ECHO message` returns a bulk string.
- `QUIT` returns `OK` and closes the connection.
- `SET key value [EX seconds | PX milliseconds]` stores or overwrites a value. Plain SET clears an existing expiration. EX and PX require positive integers.
- `GET key` returns a bulk string or a null bulk string.
- `DEL key [key ...]` returns the number of distinct keys removed.
- `EXISTS key [key ...]` counts every occurrence of an existing key, including repeated arguments.
- `EXPIRE key seconds` returns 1 if the key exists and 0 otherwise. Zero or negative seconds remove an existing key immediately.
- `TTL key` returns whole remaining seconds, -1 for a key without expiration, or -2 for a missing or expired key.

Command and option names are case insensitive. Invalid arguments return RESP errors and leave stored data unchanged.

Only RESP2 arrays of bulk strings are accepted as input. A command may have at most 1,024 arguments, each bulk string at most 1 MiB, and at most 4 MiB of bulk string payload in total. Inline commands, RESP3, authentication, NX/XX, GETEX, PERSIST, persistence, replication, clustering, Pub/Sub, and benchmarking are outside Milestone 2.