using System.Net;
using System.Net.Sockets;
using System.Text;
using Memrune.Protocol;
using Memrune.Server;

namespace Memrune.Tests;

public sealed class MilestoneOneTests
{
[Fact]
public async Task DecodeBinary()
{
    byte[] payload = [0, 13, 10, 255, 42, 36];
    using var input = new MemoryStream(Frame(Ascii("ECHO"), payload));
    var reader = new RespReader(input);
    byte[][]? command = await reader.ReadCommandAsync();
    Check(command is { Length: 2 }, "Expected two arguments");
    Equal(Ascii("ECHO"), command![0]);
    Equal(payload, command[1]);
    Check(await reader.ReadCommandAsync() is null, "Expected clean EOF");
}

[Fact]
public async Task DecodeFragmented()
{
    using var input = new OneByteStream(Concat(Frame(Ascii("PING")), Frame(Ascii("ECHO"), Ascii("abc"))));
    var reader = new RespReader(input);
    Equal(Ascii("PING"), (await reader.ReadCommandAsync())![0]);
    Equal(Ascii("abc"), (await reader.ReadCommandAsync())![1]);
    Check(await reader.ReadCommandAsync() is null, "Expected clean EOF");
}

[Fact]
public async Task FrameAtLimitIsAccepted()
{
    byte[] frame = BoundaryFrame();
    Assert.Equal(RespReader.MaxFrameLength, frame.Length);
    using var input = new MemoryStream(frame);
    var reader = new RespReader(input);
    byte[][]? command = await reader.ReadCommandAsync();
    Assert.NotNull(command);
    Assert.Equal(4, command.Length);
    Assert.All(command, argument => Assert.Equal(RespReader.MaxBulkLength - 13, argument.Length));
}

[Fact]
public async Task FrameOverLimitCountsEncodedLengthHeaders()
{
    byte[] frame = BoundaryFrame();
    foreach ((string prefix, int offset) in new[] { ("*04\r\n", 4), ("*4\r\n$0", 5) })
    {
        using var input = new MemoryStream();
        input.Write(Ascii(prefix));
        input.Write(frame.AsSpan(offset));
        Assert.Equal(RespReader.MaxFrameLength + 1, input.Length);
        input.Position = 0;
        var error = await Assert.ThrowsAsync<RespProtocolException>(() => new RespReader(input).ReadCommandAsync());
        Assert.Contains("command frame exceeds", error.Message);
    }
}

[Fact]
public async Task PipelinedFramesHaveSeparateLimits()
{
    using var input = new MemoryStream();
    input.Write(BoundaryFrame());
    input.Write(Frame(Ascii("PING")));
    input.Position = 0;
    var reader = new RespReader(input);
    Assert.Equal(4, (await reader.ReadCommandAsync())!.Length);
    Equal(Ascii("PING"), (await reader.ReadCommandAsync())![0]);
    Assert.Null(await reader.ReadCommandAsync());
}
[Fact]
public async Task RejectMalformed()
{
    foreach (string malformed in new[]
    {
        "PING\r\n", "*0\r\n", "*1\n", "*1\r\n+PING\r\n",
        "*1\r\n$-1\r\n", "*1\r\n$4\r\nPING\n",
        "*1\r\n$4\r\nPI", "*1025\r\n", "*1\r\n$1048577\r\n",
        "*2147483648\r\n", "*1\r\n$2147483648\r\n", "*000000000000000000001\r\n"
    })
    {
        using var input = new MemoryStream(Ascii(malformed));
        await ThrowsProtocol(() => new RespReader(input).ReadCommandAsync());
    }
    byte[] megabyte = new byte[RespReader.MaxBulkLength];
    using var hugeFrame = new MemoryStream(Frame(Ascii("ECHO"), megabyte, megabyte, megabyte, megabyte));
    await ThrowsProtocol(() => new RespReader(hugeFrame).ReadCommandAsync());
}

[Fact]
public async Task EncodeResponses()
{
    using var output = new MemoryStream();
    await RespWriter.WriteAsync(output, RespValue.Simple("PONG"));
    await RespWriter.WriteAsync(output, RespValue.Bulk([0, 255]));
    await RespWriter.WriteAsync(output, RespValue.Integer(-42));
    await RespWriter.WriteAsync(output, RespValue.Error("ERR bad"));
    Equal(Concat(Ascii("+PONG\r\n$2\r\n"), [0, 255], Ascii("\r\n:-42\r\n-ERR bad\r\n")), output.ToArray());
}

[Fact]
public async Task CommandsOverTcp() => await WithServer(async port =>
{
    using var client = await Connect(port);
    NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Frame(Ascii("pInG")));
    await Expect(stream, Ascii("+PONG\r\n"));
    await stream.WriteAsync(Frame(Ascii("PING"), Ascii("message")));
    await Expect(stream, Ascii("$7\r\nmessage\r\n"));
    byte[] binary = [0, 255, 13, 10];
    await stream.WriteAsync(Frame(Ascii("eChO"), binary));
    await Expect(stream, Concat(Ascii("$4\r\n"), binary, Ascii("\r\n")));
});

[Fact]
public async Task MultipleCommands() => await WithServer(async port =>
{
    using var client = await Connect(port);
    NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Frame(Ascii("PING")));
    await Expect(stream, Ascii("+PONG\r\n"));
    await stream.WriteAsync(Frame(Ascii("ECHO"), Ascii("second")));
    await Expect(stream, Ascii("$6\r\nsecond\r\n"));
});

[Fact]
public async Task PipelinedCommands() => await WithServer(async port =>
{
    using var client = await Connect(port);
    NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Concat(Frame(Ascii("PING")), Frame(Ascii("ECHO"), Ascii("hi")), Frame(Ascii("PING"))));
    await Expect(stream, Ascii("+PONG\r\n$2\r\nhi\r\n+PONG\r\n"));
});

[Fact]
public async Task FragmentedTcp() => await WithServer(async port =>
{
    using var client = await Connect(port);
    NetworkStream stream = client.GetStream();
    foreach (byte value in Frame(Ascii("ECHO"), Ascii("split")))
    {
        await stream.WriteAsync(new[] { value });
        await Task.Delay(1);
    }
    await Expect(stream, Ascii("$5\r\nsplit\r\n"));
});

[Fact]
public async Task ConcurrentClients() => await WithServer(async port =>
{
    await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
    {
        using var client = await Connect(port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Frame(Ascii("PING")));
        await Expect(stream, Ascii("+PONG\r\n"));
    }));
});

[Fact]
public async Task CommandErrors() => await WithServer(async port =>
{
    using var client = await Connect(port);
    NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Frame(Ascii("NOTACOMMAND")));
    await Expect(stream, Ascii("-ERR unknown command\r\n"));
    await stream.WriteAsync(Frame(Ascii("ECHO")));
    await Expect(stream, Ascii("-ERR wrong number of arguments for 'echo' command\r\n"));
    await stream.WriteAsync(Frame(Ascii("PING")));
    await Expect(stream, Ascii("+PONG\r\n"));
});

[Fact]
public async Task QuitCloses() => await WithServer(async port =>
{
    using var client = await Connect(port);
    NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Frame(Ascii("QUIT")));
    await Expect(stream, Ascii("+OK\r\n"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Check(await stream.ReadAsync(new byte[1], timeout.Token) == 0, "QUIT did not close connection");
});

[Fact]
public async Task DisconnectsAndProtocolErrors() => await WithServer(async port =>
{
    using (var disconnected = await Connect(port))
    {
        await disconnected.GetStream().WriteAsync(Ascii("*1\r\n$4\r\nPI"));
    }
    using var malformed = await Connect(port);
    NetworkStream stream = malformed.GetStream();
    await stream.WriteAsync(Ascii("*1\r\n+PING\r\n"));
    await Expect(stream, Ascii("-ERR Protocol error: expected a bulk string argument\r\n"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Check(await stream.ReadAsync(new byte[1], timeout.Token) == 0, "Malformed connection stayed open");
    using var healthy = await Connect(port);
    await healthy.GetStream().WriteAsync(Frame(Ascii("PING")));
    await Expect(healthy.GetStream(), Ascii("+PONG\r\n"));
});

[Fact]
public async Task ShutdownClosesClients()
{
    using var shutdown = new CancellationTokenSource();
    var server = new MemruneServer(0);
    Task serving = server.RunAsync(shutdown.Token);
    using var client = await Connect(server.BoundPort);
    shutdown.Cancel();
    await serving.WaitAsync(TimeSpan.FromSeconds(5));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Check(await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0, "Shutdown did not close client");
}

static async Task WithServer(Func<int, Task> action)
{
    using var shutdown = new CancellationTokenSource();
    var server = new MemruneServer(0);
    Task serving = server.RunAsync(shutdown.Token);
    try { await action(server.BoundPort); }
    finally
    {
        shutdown.Cancel();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

static async Task<TcpClient> Connect(int port)
{
    var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    return client;
}

static async Task Expect(Stream stream, byte[] expected)
{
    var actual = new byte[expected.Length];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await stream.ReadExactlyAsync(actual, timeout.Token);
    Equal(expected, actual);
}

static byte[] BoundaryFrame()
{
    // Four seven-digit bulk lengths: 4 bytes for the array header and 12 per bulk frame.
    byte[] payload = new byte[RespReader.MaxBulkLength - 13];
    return Frame(payload, payload, payload, payload);
}
static byte[] Frame(params byte[][] arguments)
{
    using var output = new MemoryStream();
    WriteAscii(output, $"*{arguments.Length}\r\n");
    foreach (byte[] argument in arguments)
    {
        WriteAscii(output, $"${argument.Length}\r\n");
        output.Write(argument);
        WriteAscii(output, "\r\n");
    }
    return output.ToArray();
}

static void WriteAscii(Stream output, string value) => output.Write(Ascii(value));
static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Equal(byte[] expected, byte[] actual)
{
    if (!expected.SequenceEqual(actual))
        throw new Exception($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
}
static async Task ThrowsProtocol(Func<Task> action)
{
    try { await action(); }
    catch (RespProtocolException) { return; }
    throw new Exception("Expected RespProtocolException");
}

sealed class OneByteStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}

}