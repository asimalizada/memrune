using System.Net;
using System.Net.Sockets;
using System.Text;
using Memrune.Protocol;
using Memrune.Server;

namespace Memrune.Tests;

public sealed class TransportExperimentTests
{
    [Fact]
    public async Task TcpListenerAcceptedSocketDefaultsToNoDelayDisabled()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            Assert.False(accepted.NoDelay);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task OriginalWriterSubmitsThreeWritesForSimpleAndBulkReplies()
    {
        using var output = new CountingStream();
        await RespWriter.WriteAsync(output, RespValue.Simple("PONG"));
        await RespWriter.WriteAsync(output, RespValue.Bulk([0, 255]));
        Assert.Equal(6, output.Writes);
        Assert.Equal(Concat(Ascii("+PONG\r\n$2\r\n"), [0, 255], Ascii("\r\n")), output.ToArray());
    }

    [Fact]
    public async Task CoalescedWriterPreservesAllRespKindsInOneWriteEach()
    {
        using var output = new CountingStream();
        var writer = new RespCoalescedWriter();
        await writer.WriteAsync(output, RespValue.Simple("PONG"));
        await writer.WriteAsync(output, RespValue.Bulk([0, 13, 10, 255]));
        await writer.WriteAsync(output, RespValue.Integer(-42));
        await writer.WriteAsync(output, RespValue.NullBulk());
        await writer.WriteAsync(output, RespValue.Error("ERR bad"));

        byte[] expected = Concat(Ascii("+PONG\r\n$4\r\n"), [0, 13, 10, 255],
            Ascii("\r\n:-42\r\n$-1\r\n-ERR bad\r\n"));
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(5, output.Writes);
    }

    [Fact]
    public async Task CoalescedWriterHandlesLargeBinaryBulkWithoutRetainingIt()
    {
        byte[] payload = new byte[70_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        using var output = new CountingStream();
        var writer = new RespCoalescedWriter();
        await writer.WriteAsync(output, RespValue.Bulk(payload));
        Assert.Equal(1, output.Writes);
        Assert.Equal(Concat(Ascii("$70000\r\n"), payload, Ascii("\r\n")), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescedServerKeepsPipelinedRepliesOrdered(bool noDelay)
    {
        using var shutdown = new CancellationTokenSource();
        var server = new MemruneServer(0, tcpNoDelay: noDelay, coalesceResponses: true);
        Task running = server.RunAsync(shutdown.Token);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
            NetworkStream stream = client.GetStream();

            byte[] ping = Frame(Ascii("PING"));
            foreach (byte item in ping)
                await stream.WriteAsync(new[] { item }, timeout.Token);
            byte[] pong = new byte[7];
            await stream.ReadExactlyAsync(pong, timeout.Token);
            Assert.Equal(Ascii("+PONG\r\n"), pong);

            byte[] key = [0, 255, 13, 10];
            byte[] value = [0, 13, 10, 255, 42];
            byte[] requests = Concat(
                Frame(Ascii("ECHO"), value),
                Frame(Ascii("SET"), key, value),
                Frame(Ascii("GET"), key),
                Frame(Ascii("EXISTS"), key),
                Frame(Ascii("DEL"), key),
                Frame(Ascii("GET"), key),
                Frame(Ascii("QUIT")));
            await stream.WriteAsync(requests, timeout.Token);

            byte[] expected = Concat(Ascii("$5\r\n"), value, Ascii("\r\n+OK\r\n$5\r\n"),
                value, Ascii("\r\n:1\r\n:1\r\n$-1\r\n+OK\r\n"));
            byte[] actual = new byte[expected.Length];
            await stream.ReadExactlyAsync(actual, timeout.Token);
            Assert.Equal(expected, actual);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        }
        finally
        {
            shutdown.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CombinedServerSurvivesPartialDisconnectsAndCancelsIdleClients()
    {
        using var shutdown = new CancellationTokenSource();
        var server = new MemruneServer(0, tcpNoDelay: true, coalesceResponses: true);
        Task running = server.RunAsync(shutdown.Token);
        var idle = new List<TcpClient>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 20; i++)
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
                if (i % 2 == 0)
                {
                    await client.GetStream().WriteAsync(Ascii("*2\r\n$4\r\nECHO\r\n$10\r\nabc"), timeout.Token);
                    client.Dispose();
                }
                else idle.Add(client);
            }

            using var healthy = new TcpClient { NoDelay = true };
            await healthy.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
            await healthy.GetStream().WriteAsync(Frame(Ascii("PING")), timeout.Token);
            byte[] reply = new byte[7];
            await healthy.GetStream().ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal(Ascii("+PONG\r\n"), reply);

            shutdown.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            shutdown.Cancel();
            foreach (TcpClient client in idle) client.Dispose();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Frame(params byte[][] arguments)
    {
        using var stream = new MemoryStream();
        stream.Write(Ascii("*" + arguments.Length + "\r\n"));
        foreach (byte[] argument in arguments)
        {
            stream.Write(Ascii("$" + argument.Length + "\r\n"));
            stream.Write(argument);
            stream.Write(Ascii("\r\n"));
        }
        return stream.ToArray();
    }

    private static byte[] Concat(params byte[][] arrays) =>
        arrays.SelectMany(array => array).ToArray();

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
