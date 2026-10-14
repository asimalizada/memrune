# MEMRUNE

MEMRUNE is an experimental Redis-compatible in-memory database engine written from scratch in C# and .NET 10 for the Underbyte technical video project.

## Status

Milestone 1 is implemented: a loopback TCP server with the subset of RESP2 needed for `redis-cli` command arrays. This is **not** a full Redis implementation.

## Architecture

- `src/Memrune.Protocol` reads RESP2 arrays of bulk strings and writes simple strings, bulk strings, integers, and errors. It owns framing and size limits.
- `src/Memrune.Core` dispatches the three supported commands. It has no storage.
- `src/Memrune.Server` accepts TCP clients on loopback and runs an asynchronous command loop per client.
- `tests/Memrune.Tests` contains xUnit parser, writer, and real TCP integration tests. TCP tests use ephemeral ports.

The reader buffers bytes across reads and commands. It checks lengths before allocating argument data, and it keeps bulk string payloads as bytes so binary data survives unchanged. A malformed frame receives a RESP error and closes that connection.

## Build and test

Install the .NET 10 SDK, then from the repository root run:

```powershell
dotnet restore Memrune.slnx
dotnet build Memrune.slnx
dotnet test Memrune.slnx
```

Test dependencies are xUnit and the .NET test SDK. Restoring them normally requires access to NuGet unless already cached.

## Run and connect

Start the server in one terminal:

```powershell
dotnet run --project src/Memrune.Server
```

It listens on `127.0.0.1:6380`. To select a port, use `dotnet run --project src/Memrune.Server -- --port 6381`. Press Ctrl+C to stop it.

With the official `redis-cli` installed, run in another terminal:

```powershell
redis-cli -h 127.0.0.1 -p 6380 PING
redis-cli -h 127.0.0.1 -p 6380 ECHO hello
redis-cli -h 127.0.0.1 -p 6380 QUIT
```

The PING response is `PONG`. In interactive mode, use `redis-cli -h 127.0.0.1 -p 6380` and enter `PING`, `PING hello`, `ECHO hello`, or `QUIT`.

## Supported commands and limits

- `PING` returns a simple string `PONG`; `PING message` returns the message as a bulk string.
- `ECHO message` returns the message as a bulk string.
- `QUIT` returns `OK` and closes the connection.
- Command names are case insensitive. Unknown commands and wrong argument counts return RESP errors.

Only RESP2 arrays of bulk strings are accepted as input. A command may have at most 1,024 arguments, each bulk string at most 1 MiB, and at most 4 MiB of bulk string payload in total. Inline commands, RESP3, authentication, SET/GET, TTL, persistence, and benchmarking are outside Milestone 1.