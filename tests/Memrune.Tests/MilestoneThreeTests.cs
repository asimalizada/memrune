using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Memrune.Core;
using Memrune.Server;

namespace Memrune.Tests;

public sealed class MilestoneThreeTests
{
    [Fact]
    public async Task OneHundredClientsPingConcurrently()
    {
        await WithServer(async (port, _) =>
        {
            TcpClient[] clients = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Connect(port)));
            try
            {
                await Task.WhenAll(clients.Select(async client =>
                {
                    NetworkStream stream = client.GetStream();
                    await stream.WriteAsync(Frame(Text("PING")));
                    await Expect(stream, Text("+PONG\r\n"));
                }));
            }
            finally { foreach (TcpClient client in clients) client.Dispose(); }
        });
    }

    [Fact]
    public async Task ConcurrentIndependentSetAndGet()
    {
        await WithServer(async (port, _) =>
        {
            await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
            {
                using var client = await Connect(port);
                NetworkStream stream = client.GetStream();
                byte[] key = Text($"key-{index}");
                byte[] value = Text($"value-{index}");
                await stream.WriteAsync(Frame(Text("SET"), key, value));
                await Expect(stream, Text("+OK\r\n"));
                await stream.WriteAsync(Frame(Text("GET"), key));
                Assert.Equal(value, await ReadBulk(stream));
            }));
        });
    }

    [Fact]
    public async Task SameKeyOverwritesRemainWhole()
    {
        await WithServer(async (port, _) =>
        {
            byte[] key = Text("shared");
            byte[][] values = Enumerable.Range(0, 40)
                .Select(i => Text($"{i:D3}:{new string((char)('A' + i % 26), 512)}"))
                .ToArray();
            await Task.WhenAll(values.Select(async value =>
            {
                using var client = await Connect(port);
                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(Frame(Text("SET"), key, value));
                await Expect(stream, Text("+OK\r\n"));
            }));
            using var reader = await Connect(port);
            await reader.GetStream().WriteAsync(Frame(Text("GET"), key));
            byte[] actual = Assert.IsType<byte[]>(await ReadBulk(reader.GetStream()));
            Assert.Contains(values, value => value.SequenceEqual(actual));
        });
    }

    [Fact]
    public async Task ConcurrentSameKeyReadsAndWritesReturnWholeValues()
    {
        await WithServer(async (port, store) =>
        {
            byte[] key = Text("read-write");
            byte[] seed = Enumerable.Repeat((byte)'S', 512).ToArray();
            byte[] first = Enumerable.Repeat((byte)'A', 512).ToArray();
            byte[] second = Enumerable.Repeat((byte)'B', 512).ToArray();
            store.Set(key, seed);
            TcpClient[] clients = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Connect(port)));
            try
            {
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task[] operations = clients.Select((client, index) => Task.Run(async () =>
                {
                    await start.Task;
                    NetworkStream stream = client.GetStream();
                    if (index % 2 == 0)
                    {
                        await stream.WriteAsync(Frame(Text("SET"), key, index % 4 == 0 ? first : second));
                        await Expect(stream, Text("+OK\r\n"));
                    }
                    else
                    {
                        await stream.WriteAsync(Frame(Text("GET"), key));
                        byte[] actual = Assert.IsType<byte[]>(await ReadBulk(stream));
                        Assert.True(actual.SequenceEqual(seed) || actual.SequenceEqual(first) || actual.SequenceEqual(second));
                    }
                })).ToArray();
                start.SetResult(true);
                await Task.WhenAll(operations);
                byte[] final = Assert.IsType<byte[]>(store.Get(key));
                Assert.True(final.SequenceEqual(first) || final.SequenceEqual(second));
            }
            finally { foreach (TcpClient client in clients) client.Dispose(); }
        });
    }

    [Fact]
    public async Task ConcurrentDeletesRemoveOneKeyOnce()
    {
        await WithServer(async (port, _) =>
        {
            using (var seed = await Connect(port))
            {
                await seed.GetStream().WriteAsync(Frame(Text("SET"), Text("delete-me"), Text("value")));
                await Expect(seed.GetStream(), Text("+OK\r\n"));
            }
            long[] results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
            {
                using var client = await Connect(port);
                await client.GetStream().WriteAsync(Frame(Text("DEL"), Text("delete-me")));
                return await ReadInteger(client.GetStream());
            }));
            Assert.Equal(1, results.Sum());
        });
    }

    [Fact]
    public async Task ExpirationRacesUseLogicalAbsence()
    {
        var clock = new ManualTimeProvider();
        var store = new KeyValueStore(clock);
        store.Set(Text("deadline"), Text("value"), 1000);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WithServer(async (port, _) =>
        {
            await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
            {
                using var client = await Connect(port);
                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(Concat(
                    Frame(Text("GET"), Text("deadline")),
                    Frame(Text("TTL"), Text("deadline")),
                    Frame(Text("EXISTS"), Text("deadline"))));
                Assert.Null(await ReadBulk(stream));
                Assert.Equal(-2, await ReadInteger(stream));
                Assert.Equal(0, await ReadInteger(stream));
            }));
        }, store);

        store.Set(Text("race"), Text("value"));
        await WithServer(async (port, _) =>
        {
            TcpClient[] clients = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Connect(port)));
            try
            {
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<long?>[] operations = clients.Select((client, index) => Task.Run(async () =>
                {
                    await start.Task;
                    NetworkStream stream = client.GetStream();
                    if (index % 2 == 0)
                    {
                        await stream.WriteAsync(Frame(Text("EXPIRE"), Text("race"), Text("0")));
                        return (long?)await ReadInteger(stream);
                    }
                    await stream.WriteAsync(Frame(Text("GET"), Text("race")));
                    byte[]? value = await ReadBulk(stream);
                    Assert.True(value is null || value.SequenceEqual(Text("value")));
                    return null;
                })).ToArray();
                start.SetResult(true);
                long?[] results = await Task.WhenAll(operations);
                Assert.Equal(1, results.Where(value => value.HasValue).Sum(value => value!.Value));
                Assert.Null(store.Get(Text("race")));
            }
            finally { foreach (TcpClient client in clients) client.Dispose(); }
        }, store);
    }

    [Fact]
    public async Task PipelinedRepliesStayOrderedPerClient()
    {
        await WithServer(async (port, _) =>
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async clientNumber =>
            {
                using var client = await Connect(port);
                NetworkStream stream = client.GetStream();
                byte[][] payloads = Enumerable.Range(0, 30)
                    .Select(i => Text($"{clientNumber:D2}-{i:D2}"))
                    .ToArray();
                await stream.WriteAsync(Concat(payloads.Select(value => Frame(Text("ECHO"), value)).ToArray()));
                foreach (byte[] expected in payloads)
                    Assert.Equal(expected, await ReadBulk(stream));
            }));
        });
    }

    [Fact]
    public async Task FragmentedAndCoalescedRequestsStayFramed()
    {
        await WithServer(async (port, _) =>
        {
            using var client = await Connect(port);
            NetworkStream stream = client.GetStream();
            byte[] first = Frame(Text("ECHO"), Text("fragment"));
            foreach (byte item in first)
                await stream.WriteAsync(new[] { item });
            await Expect(stream, Text("$8\r\nfragment\r\n"));
            await stream.WriteAsync(Concat(Frame(Text("PING")), Frame(Text("ECHO"), Text("two"))));
            await Expect(stream, Text("+PONG\r\n$3\r\ntwo\r\n"));
        });
    }

    [Theory]
    [InlineData("*1\r\n$1048577\r\n")]
    [InlineData("*1\r\n$abc\r\n")]
    [InlineData("*1025\r\n")]
    [InlineData("*1\r\n+PING\r\n")]
    public async Task MalformedFramesCloseOnlyTheirConnection(string frame)
    {
        await WithServer(async (port, _) =>
        {
            using var broken = await Connect(port);
            NetworkStream stream = broken.GetStream();
            await stream.WriteAsync(Text(frame));
            Assert.StartsWith("-ERR Protocol error:", await ReadLine(stream));
            await ExpectEnd(stream);

            using var healthy = await Connect(port);
            await healthy.GetStream().WriteAsync(Frame(Text("PING")));
            await Expect(healthy.GetStream(), Text("+PONG\r\n"));
        });
    }

    [Fact]
    public async Task IncompleteBulkStringAndAbruptResetAreIsolated()
    {
        var store = new KeyValueStore();
        store.Set(Text("large"), new byte[1024 * 1024]);
        await WithServer(async (port, _) =>
        {
            using (var incomplete = await Connect(port))
            {
                NetworkStream stream = incomplete.GetStream();
                await stream.WriteAsync(Text("*2\r\n$4\r\nECHO\r\n$5\r\nxy"));
                incomplete.Client.Shutdown(SocketShutdown.Send);
                Assert.StartsWith("-ERR Protocol error:", await ReadLine(stream));
                await ExpectEnd(stream);
            }

            var reset = await Connect(port);
            await reset.GetStream().WriteAsync(Frame(Text("GET"), Text("large")));
            await Expect(reset.GetStream(), Text("$1048576\r\n"));
            reset.Client.LingerState = new LingerOption(true, 0);
            reset.Dispose();

            using var healthy = await Connect(port);
            await healthy.GetStream().WriteAsync(Frame(Text("PING")));
            await Expect(healthy.GetStream(), Text("+PONG\r\n"));
        }, store);
    }

    [Fact]
    public async Task InvalidCommandErrorKeepsConnectionUsable()
    {
        await WithServer(async (port, _) =>
        {
            using var client = await Connect(port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Concat(
                Frame(Text("SET"), Text("k"), Text("v"), Text("PX"), Text("bad")),
                Frame(Text("unknown")),
                Frame(Text("PING"))));
            Assert.StartsWith("-ERR ", await ReadLine(stream));
            Assert.Equal("-ERR unknown command", await ReadLine(stream));
            await Expect(stream, Text("+PONG\r\n"));
        });
    }

    [Fact]
    public async Task CancellationClosesIdleAndPartialClients()
    {
        using var shutdown = new CancellationTokenSource();
        var server = new MemruneServer(0);
        Task serving = server.RunAsync(shutdown.Token);
        TcpClient[] clients = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Connect(server.BoundPort)));
        try
        {
            foreach (TcpClient client in clients.Take(10))
                await client.GetStream().WriteAsync(Text("*1\r\n$4\r\nPI"));
            shutdown.Cancel();
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(clients.Select(async client => await ExpectEndOrReset(client.GetStream())));
        }
        finally
        {
            shutdown.Cancel();
            foreach (TcpClient client in clients) client.Dispose();
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ShutdownWaitsForClientsDisconnectingAlongsideActiveClients()
    {
        for (int round = 0; round < 4; round++)
        {
            using var shutdown = new CancellationTokenSource();
            var server = new MemruneServer(0);
            Task serving = server.RunAsync(shutdown.Token);
            TcpClient[] clients = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Connect(server.BoundPort)));
            try
            {
                await Task.WhenAll(clients.Select(async client =>
                {
                    NetworkStream stream = client.GetStream();
                    await stream.WriteAsync(Frame(Text("PING")));
                    await Expect(stream, Text("+PONG\r\n"));
                }));

                TcpClient[] active = clients.Take(16).ToArray();
                foreach (TcpClient client in active)
                    await client.GetStream().WriteAsync(Text("*1\r\n$4\r\nPI"));

                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task[] disconnects = clients.Skip(16).Select(async client =>
                {
                    await start.Task;
                    client.Dispose();
                }).ToArray();
                Task stop = Task.Run(async () =>
                {
                    await start.Task;
                    shutdown.Cancel();
                });
                start.SetResult();

                await Task.WhenAll(disconnects.Append(stop)).WaitAsync(TimeSpan.FromSeconds(10));
                await serving.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.WhenAll(active.Select(client => ExpectEndOrReset(client.GetStream())));
            }
            finally
            {
                shutdown.Cancel();
                foreach (TcpClient client in clients) client.Dispose();
                await serving.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
    [Fact]
    public async Task AdmissionLimitReturnsExplicitError()
    {
        await WithServer(async (port, _) =>
        {
            using var first = await Connect(port);
            await first.GetStream().WriteAsync(Frame(Text("PING")));
            await Expect(first.GetStream(), Text("+PONG\r\n"));

            using var second = await Connect(port);
            Assert.Equal("-ERR max number of clients reached", await ReadLine(second.GetStream()));
            await ExpectEnd(second.GetStream());
            await first.GetStream().WriteAsync(Frame(Text("PING")));
            await Expect(first.GetStream(), Text("+PONG\r\n"));
        }, maxClients: 1);
    }

    private static async Task WithServer(Func<int, KeyValueStore, Task> action,
        KeyValueStore? store = null, int maxClients = 512)
    {
        using var shutdown = new CancellationTokenSource();
        store ??= new KeyValueStore();
        var server = new MemruneServer(0, store, maxClients);
        Task serving = server.RunAsync(shutdown.Token);
        try { await action(server.BoundPort, store).WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            shutdown.Cancel();
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<TcpClient> Connect(int port)
    {
        var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task Expect(Stream stream, byte[] expected)
    {
        byte[] actual = new byte[expected.Length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(expected, actual);
    }

    private static async Task<string> ReadLine(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bytes = new List<byte>(32);
        byte[] item = new byte[1];
        while (bytes.Count < 256)
        {
            await stream.ReadExactlyAsync(item, timeout.Token);
            bytes.Add(item[0]);
            if (bytes.Count >= 2 && bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new Exception("Response line exceeded test limit");
    }

    private static async Task<byte[]?> ReadBulk(Stream stream)
    {
        string header = await ReadLine(stream);
        Assert.StartsWith("$", header);
        int length = int.Parse(header.AsSpan(1), CultureInfo.InvariantCulture);
        if (length == -1) return null;
        byte[] data = new byte[length + 2];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(data, timeout.Token);
        Assert.Equal((byte)'\r', data[^2]);
        Assert.Equal((byte)'\n', data[^1]);
        return data.AsSpan(0, length).ToArray();
    }

    private static async Task<long> ReadInteger(Stream stream)
    {
        string response = await ReadLine(stream);
        Assert.StartsWith(":", response);
        return long.Parse(response.AsSpan(1), CultureInfo.InvariantCulture);
    }

    private static async Task ExpectEnd(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
    }

    private static async Task ExpectEndOrReset(Stream stream)
    {
        try { await ExpectEnd(stream); }
        catch (IOException) { }
        catch (SocketException) { }
    }

    private static byte[] Frame(params byte[][] arguments)
    {
        using var output = new MemoryStream();
        output.Write(Text($"*{arguments.Length}\r\n"));
        foreach (byte[] argument in arguments)
        {
            output.Write(Text($"${argument.Length}\r\n"));
            output.Write(argument);
            output.Write(Text("\r\n"));
        }
        return output.ToArray();
    }

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}