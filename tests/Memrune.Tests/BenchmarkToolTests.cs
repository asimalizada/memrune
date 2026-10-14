using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Memrune.Server;

namespace Memrune.Tests;

public sealed class BenchmarkToolTests
{
    [Fact]
    public void OptionsValidateFrameAndClientLimits()
    {
        Assert.True(BenchmarkOptions.TryParse(
            ["--workload", "get", "--clients", "100", "--pipeline", "16",
             "--operations", "5000", "--value-size", "1024"],
            out BenchmarkOptions? valid, out _));
        Assert.Equal(BenchmarkWorkload.Get, valid!.Workload);
        Assert.False(BenchmarkOptions.TryParse(
            ["--clients", "100", "--operations", "50"], out _, out _));
        Assert.False(BenchmarkOptions.TryParse(
            ["--pipeline", "1024", "--value-size", "1048576"], out _, out _));
        Assert.False(BenchmarkOptions.TryParse(
            ["--workload", "set", "--keys", "100000", "--value-size", "1048576"],
            out _, out _));
    }

    [Fact]
    public async Task ResponseReaderHandlesFragmentationAndRejectsWrongValues()
    {
        byte[] frames = Encoding.ASCII.GetBytes("+OK\r\n$3\r\nabc\r\n");
        using var fragmented = new OneByteStream(frames);
        var reader = new RespBenchmarkReader(fragmented, 32);
        await reader.ReadSimpleAsync(Encoding.ASCII.GetBytes("OK\r\n"), CancellationToken.None);
        await reader.ReadBulkAsync(Encoding.ASCII.GetBytes("abc"), CancellationToken.None);

        using var wrong = new MemoryStream(Encoding.ASCII.GetBytes("$3\r\nxyz\r\n"));
        var wrongReader = new RespBenchmarkReader(wrong, 32);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            wrongReader.ReadBulkAsync(Encoding.ASCII.GetBytes("abc"), CancellationToken.None));
    }

    [Fact]
    public void FrameBuilderPreservesBinaryPayload()
    {
        byte[] frame = BenchmarkRunner.Frame(Encoding.ASCII.GetBytes("ECHO"), [0, 13, 10, 255]);
        Assert.Equal(Encoding.ASCII.GetBytes("*2\r\n$4\r\nECHO\r\n$4\r\n")
            .Concat(new byte[] { 0, 13, 10, 255 })
            .Concat(Encoding.ASCII.GetBytes("\r\n")), frame);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("set")]
    [InlineData("get")]
    public async Task BenchmarkModeValidatesRealTcpReplies(string workload)
    {
        string output = Path.GetTempFileName();
        using var shutdown = new CancellationTokenSource();
        var server = new MemruneServer(0);
        Task running = server.RunAsync(shutdown.Token);
        try
        {
            int exitCode = await BenchmarkRunner.RunAsync(
                ["--host", "127.0.0.1", "--port", server.BoundPort.ToString(),
                 "--workload", workload, "--clients", "2", "--operations", "32",
                 "--pipeline", "4", "--value-size", "32", "--warmup", "8",
                 "--repetitions", "3", "--keys", "8", "--output", output])
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, exitCode);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            JsonElement runs = json.RootElement.GetProperty("runs");
            Assert.Equal(3, runs.GetArrayLength());
            foreach (JsonElement run in runs.EnumerateArray())
            {
                Assert.True(run.GetProperty("valid").GetBoolean());
                Assert.Equal(32, run.GetProperty("successfulOperations").GetInt64());
                Assert.Equal(0, run.GetProperty("failedOperations").GetInt64());
                Assert.True(run.GetProperty("p99Microseconds").GetDouble() >= 0);
                Assert.True(run.GetProperty("clientCpuSeconds").GetDouble() >= 0);
                Assert.True(run.GetProperty("clientAllocatedBytes").GetInt64() >= 0);
                Assert.True(run.GetProperty("clientGen0Collections").GetInt32() >= 0);
                Assert.True(run.TryGetProperty("clientCgroupThrottledPeriods", out _));
            }
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("median").ValueKind);
        }
        finally
        {
            shutdown.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(output);
        }
    }

    [Fact]
    public async Task DuplicateReplyInvalidatesMeasuredRun()
    {
        string output = Path.GetTempFileName();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task fakeServer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            byte[] request = new byte[BenchmarkRunner.Frame(Encoding.ASCII.GetBytes("PING")).Length];
            await client.GetStream().ReadExactlyAsync(request);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("+PONG\r\n+PONG\r\n"));
        });
        try
        {
            int exitCode = await BenchmarkRunner.RunAsync(
                ["--host", "127.0.0.1", "--port", port.ToString(),
                 "--workload", "ping", "--clients", "1", "--operations", "1",
                 "--pipeline", "1", "--warmup", "0", "--repetitions", "1",
                 "--output", output]).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, exitCode);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.False(json.RootElement.GetProperty("runs")[0].GetProperty("valid").GetBoolean());
            await fakeServer.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
            File.Delete(output);
        }
    }

    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}