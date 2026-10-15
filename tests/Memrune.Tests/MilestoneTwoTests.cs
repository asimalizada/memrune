using System.Net;
using System.Net.Sockets;
using System.Text;
using Memrune.Core;
using Memrune.Protocol;
using Memrune.Server;

namespace Memrune.Tests;

public sealed class MilestoneTwoTests
{
    [Fact]
    public void SetGetOverwriteAndMissing()
    {
        var commands = NewCommands();
        Assert.Equal(RespKind.NullBulkString, Run(commands, "GET", "key").Kind);
        AssertSimple(Run(commands, "SET", "key", "first"), "OK");
        AssertBulk(Run(commands, "GET", "key"), Bytes("first"));
        AssertSimple(Run(commands, "SET", "key", "second"), "OK");
        AssertBulk(Run(commands, "GET", "key"), Bytes("second"));
    }

    [Fact]
    public void BinaryKeysAndValuesAreCopied()
    {
        var store = new KeyValueStore();
        byte[] key = [0, 255, 13, 10];
        byte[] value = [0, 255, 42, 13, 10];
        Assert.True(store.Set(key, value));
        key[0] = 9;
        value[0] = 9;
        byte[] lookup = [0, 255, 13, 10];
        byte[] result = Assert.IsType<byte[]>(store.Get(lookup));
        Assert.Equal((byte)0, result[0]);
        result[0] = 8;
        Assert.Equal((byte)0, store.Get(lookup)![0]);
        Assert.Null(store.Get(key));
    }

    [Fact]
    public void DelAndExistsCountRepeatedKeysLikeRedis()
    {
        var commands = NewCommands();
        Run(commands, "SET", "a", "1");
        Run(commands, "SET", "b", "2");
        AssertInteger(Run(commands, "EXISTS", "a", "a", "b", "missing"), 3);
        AssertInteger(Run(commands, "DEL", "a", "a", "b", "missing"), 2);
        AssertInteger(Run(commands, "EXISTS", "a", "b"), 0);
        AssertInteger(Run(commands, "DEL", "a"), 0);
    }

    [Fact]
    public void ExpirationBoundariesAndExpiredKeyBehavior()
    {
        var clock = new ManualTimeProvider();
        var commands = NewCommands(clock);
        Run(commands, "SET", "key", "value");
        AssertInteger(Run(commands, "TTL", "key"), -1);
        AssertInteger(Run(commands, "EXPIRE", "key", "2"), 1);
        AssertInteger(Run(commands, "TTL", "key"), 2);
        clock.Advance(TimeSpan.FromMilliseconds(1001));
        AssertInteger(Run(commands, "TTL", "key"), 0);
        AssertBulk(Run(commands, "GET", "key"), Bytes("value"));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        AssertInteger(Run(commands, "TTL", "key"), -2);
        Assert.Equal(RespKind.NullBulkString, Run(commands, "GET", "key").Kind);
        AssertInteger(Run(commands, "EXISTS", "key", "key"), 0);
        AssertInteger(Run(commands, "DEL", "key"), 0);
        AssertInteger(Run(commands, "EXPIRE", "key", "1"), 0);
        AssertSimple(Run(commands, "SET", "key", "new"), "OK");
        AssertInteger(Run(commands, "TTL", "key"), -1);
    }

    [Fact]
    public void NonPositiveExpireRemovesExistingKey()
    {
        var commands = NewCommands();
        Run(commands, "SET", "a", "1");
        AssertInteger(Run(commands, "EXPIRE", "a", "0"), 1);
        AssertInteger(Run(commands, "TTL", "a"), -2);
        Run(commands, "SET", "a", "1");
        AssertInteger(Run(commands, "EXPIRE", "a", "-100"), 1);
        AssertInteger(Run(commands, "EXPIRE", "a", "0"), 0);
    }

    [Fact]
    public void SetExPxAndPlainSetClearsExpiration()
    {
        var clock = new ManualTimeProvider();
        var commands = NewCommands(clock);
        AssertSimple(Run(commands, "SET", "a", "x", "eX", "2"), "OK");
        AssertInteger(Run(commands, "TTL", "a"), 2);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        AssertInteger(Run(commands, "TTL", "a"), 0);
        AssertSimple(Run(commands, "SET", "a", "plain"), "OK");
        clock.Advance(TimeSpan.FromSeconds(10));
        AssertInteger(Run(commands, "TTL", "a"), -1);
        AssertBulk(Run(commands, "GET", "a"), Bytes("plain"));
        AssertSimple(Run(commands, "SET", "b", "short", "pX", "500"), "OK");
        clock.Advance(TimeSpan.FromMilliseconds(499));
        AssertBulk(Run(commands, "GET", "b"), Bytes("short"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(RespKind.NullBulkString, Run(commands, "GET", "b").Kind);
    }

    [Fact]
    public void InvalidArgumentsAndOverflowDoNotMutateData()
    {
        var commands = NewCommands();
        Run(commands, "SET", "key", "safe");
        byte[][][] invalid =
        [
            Args("SET", "key", "changed", "EX", "0"),
            Args("SET", "key", "changed", "EX", "-1"),
            Args("SET", "key", "changed", "PX", "abc"),
            Args("SET", "key", "changed", "EX", "9223372036854775808"),
            Args("SET", "key", "changed", "EX", "9223372036854775807"),
            Args("SET", "key", "changed", "PX", "9223372036854775807"),
            Args("SET", "key", "changed", "NX", "1"),
            Args("SET", "key", "changed", "EX", "1", "PX", "1"),
            Args("SET", "key"),
            Args("GET"),
            Args("DEL"),
            Args("EXISTS"),
            Args("EXPIRE", "key"),
            Args("TTL"),
            Args("EXPIRE", "key", "9223372036854775808"),
            Args("EXPIRE", "key", "9223372036854775807")
        ];
        foreach (byte[][] command in invalid)
        {
            Assert.Equal(RespKind.Error, commands.Execute(command).Response.Kind);
            AssertBulk(Run(commands, "GET", "key"), Bytes("safe"));
        }
        AssertInteger(Run(commands, "TTL", "key"), -1);
    }

    [Fact]
    public void ConcurrentReadsAndWritesPreserveWholeValues()
    {
        var store = new KeyValueStore();
        byte[] key = Bytes("shared");
        byte[] a = Enumerable.Repeat((byte)0xAA, 512).ToArray();
        byte[] b = Enumerable.Repeat((byte)0xBB, 512).ToArray();
        Parallel.For(0, 2000, i =>
        {
            store.Set(key, i % 2 == 0 ? a : b);
            byte[]? value = store.Get(key);
            Assert.NotNull(value);
            Assert.True(value.SequenceEqual(a) || value.SequenceEqual(b));
        });
        Assert.Equal(1, store.Exists([key]));
    }

    [Fact]
    public async Task SimultaneousMutationsOfOneKeyAreAtomic()
    {
        var store = new KeyValueStore();
        byte[] key = Bytes("same");
        store.Set(key, Bytes("value"));
        long[] removals = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => store.Delete([key]))));
        Assert.Equal(1, removals.Sum());
        Assert.Null(store.Get(key));
    }

    [Fact]
    public async Task TcpStorageCommandsAndPipelining()
    {
        await WithServer(async (port, _) =>
        {
            using var client = await Connect(port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Concat(
                Frame(Bytes("SET"), Bytes("k"), Bytes("v")),
                Frame(Bytes("GET"), Bytes("k")),
                Frame(Bytes("EXISTS"), Bytes("k"), Bytes("k")),
                Frame(Bytes("DEL"), Bytes("k"), Bytes("k")),
                Frame(Bytes("GET"), Bytes("k"))));
            await Expect(stream, Bytes("+OK\r\n$1\r\nv\r\n:2\r\n:1\r\n$-1\r\n"));
        });
    }

    [Fact]
    public async Task TcpBinaryDataAndSharedStoreAcrossClients()
    {
        await WithServer(async (port, _) =>
        {
            byte[] key = [0, 255, 13, 10];
            byte[] value = [0, 1, 255, 13, 10];
            using (var writer = await Connect(port))
            {
                await writer.GetStream().WriteAsync(Frame(Bytes("SET"), key, value));
                await Expect(writer.GetStream(), Bytes("+OK\r\n"));
            }
            using var reader = await Connect(port);
            await reader.GetStream().WriteAsync(Frame(Bytes("GET"), key));
            await Expect(reader.GetStream(), Concat(Bytes("$5\r\n"), value, Bytes("\r\n")));
        });
    }

    [Fact]
    public async Task TcpEmptyKeyAndValueRemainDistinctFromMissing()
    {
        await WithServer(async (port, _) =>
        {
            using var client = await Connect(port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Concat(
                Frame(Bytes("SET"), [], []),
                Frame(Bytes("GET"), []),
                Frame(Bytes("GET"), Bytes("missing")),
                Frame(Bytes("EXISTS"), [], []),
                Frame(Bytes("DEL"), []),
                Frame(Bytes("GET"), [])));
            await Expect(stream, Bytes("+OK\r\n$0\r\n\r\n$-1\r\n:2\r\n:1\r\n$-1\r\n"));
        });
    }

    [Fact]
    public async Task TcpExpirationWithInjectedClock()
    {
        var clock = new ManualTimeProvider();
        await WithServer(async (port, _) =>
        {
            using var client = await Connect(port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Frame(Bytes("SET"), Bytes("key"), Bytes("value"), Bytes("PX"), Bytes("500")));
            await Expect(stream, Bytes("+OK\r\n"));
            await stream.WriteAsync(Frame(Bytes("TTL"), Bytes("key")));
            await Expect(stream, Bytes(":0\r\n"));
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await stream.WriteAsync(Concat(Frame(Bytes("GET"), Bytes("key")), Frame(Bytes("TTL"), Bytes("key"))));
            await Expect(stream, Bytes("$-1\r\n:-2\r\n"));
        }, new KeyValueStore(clock));
    }

    [Fact]
    public async Task TcpInvalidCommandDoesNotKillConnection()
    {
        await WithServer(async (port, _) =>
        {
            using var client = await Connect(port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Frame(Bytes("SET"), Bytes("k"), Bytes("v"), Bytes("PX"), Bytes("bad")));
            await Expect(stream, Bytes("-ERR value is not an integer or out of range\r\n"));
            await stream.WriteAsync(Frame(Bytes("GET"), Bytes("k")));
            await Expect(stream, Bytes("$-1\r\n"));
            await stream.WriteAsync(Frame(Bytes("PING")));
            await Expect(stream, Bytes("+PONG\r\n"));
        });
    }

    private static CommandExecutor NewCommands(TimeProvider? clock = null) => new(new KeyValueStore(clock));
    private static byte[][] Args(params string[] values) => values.Select(Bytes).ToArray();
    private static RespValue Run(CommandExecutor commands, params string[] values) => commands.Execute(Args(values)).Response;
    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
    private static void AssertSimple(RespValue value, string expected)
    {
        Assert.Equal(RespKind.SimpleString, value.Kind);
        Assert.Equal(Bytes(expected), value.Data);
    }
    private static void AssertBulk(RespValue value, byte[] expected)
    {
        Assert.Equal(RespKind.BulkString, value.Kind);
        Assert.Equal(expected, value.Data);
    }
    private static void AssertInteger(RespValue value, long expected)
    {
        Assert.Equal(RespKind.Integer, value.Kind);
        Assert.Equal(expected, value.Number);
    }

    private static async Task WithServer(Func<int, KeyValueStore, Task> action, KeyValueStore? store = null)
    {
        using var shutdown = new CancellationTokenSource();
        store ??= new KeyValueStore();
        var server = new MemruneServer(0, store);
        Task running = server.RunAsync(shutdown.Token);
        try { await action(server.BoundPort, store); }
        finally
        {
            shutdown.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static async Task<TcpClient> Connect(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }
    private static async Task Expect(Stream stream, byte[] expected)
    {
        var actual = new byte[expected.Length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(expected, actual);
    }
    private static byte[] Frame(params byte[][] args)
    {
        using var output = new MemoryStream();
        Write(output, Bytes($"*{args.Length}\r\n"));
        foreach (byte[] arg in args)
        {
            Write(output, Bytes($"${arg.Length}\r\n"));
            Write(output, arg);
            Write(output, Bytes("\r\n"));
        }
        return output.ToArray();
    }
    private static void Write(Stream output, byte[] bytes) => output.Write(bytes);
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}