using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class BenchmarkRunner
{
    private static readonly byte[] PingReply = Encoding.ASCII.GetBytes("PONG\r\n");
    private static readonly byte[] OkReply = Encoding.ASCII.GetBytes("OK\r\n");

    public static async Task<int> RunAsync(string[] args)
    {
        if (!BenchmarkOptions.TryParse(args, out BenchmarkOptions? options, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: --benchmark --host HOST --port PORT --workload ping|set|get " +
                "--clients N --operations TOTAL --pipeline N --value-size BYTES " +
                "--warmup TOTAL --repetitions N --keys N [--output FILE]");
            return 2;
        }

        var runs = new List<BenchmarkRunResult>();
        for (int repetition = 1; repetition <= options!.Repetitions; repetition++)
            runs.Add(await RunOneAsync(options, repetition));

        BenchmarkAggregate? aggregate = runs.All(run => run.Valid)
            ? new BenchmarkAggregate(
                Median(runs.Select(run => run.SuccessfulOpsPerSecond)),
                Median(runs.Select(run => run.P50Microseconds!.Value)),
                Median(runs.Select(run => run.P95Microseconds!.Value)),
                Median(runs.Select(run => run.P99Microseconds!.Value)))
            : null;

        var report = new BenchmarkReport(
            1, DateTimeOffset.UtcNow, options.Host, options.Port,
            options.Workload.ToString().ToLowerInvariant(), options.Clients,
            options.Operations, options.PipelineDepth, options.ValueSize,
            options.WarmupOperations, options.Repetitions, options.KeyCount,
            "Client-observed time from submitting a batch to full receipt and validation of each reply; all commands in one batch share its submission timestamp.",
            Environment.Version.ToString(), RuntimeInformation.OSDescription,
            Stopwatch.Frequency, runs, aggregate);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        string json = JsonSerializer.Serialize(report, jsonOptions);
        if (options.Output is null)
            Console.WriteLine(json);
        else
        {
            string? directory = Path.GetDirectoryName(options.Output);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(options.Output, json + Environment.NewLine);
            Console.WriteLine($"Wrote {options.Output}: {runs.Count} runs, " +
                $"{runs.Count(run => run.Valid)} valid, median " +
                $"{aggregate?.SuccessfulOpsPerSecond.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} successful ops/s");
        }
        return aggregate is null ? 1 : 0;
    }

    private static async Task<BenchmarkRunResult> RunOneAsync(BenchmarkOptions options, int repetition)
    {
        WorkloadData data = BuildData(options, repetition);
        var clients = new BenchmarkClient?[options.Clients];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        PhaseOutcome? measured = null;
        string? error = null;
        long[] latencyTicks = new long[options.Operations];
        try
        {
            await Task.WhenAll(Enumerable.Range(0, clients.Length).Select(async index =>
                clients[index] = await BenchmarkClient.ConnectAsync(options.Host, options.Port,
                    options.ValueSize, timeout.Token)));
            BenchmarkClient[] connected = clients.Select(client => client!).ToArray();

            if (options.Workload != BenchmarkWorkload.Ping)
                await PrepareDatasetAsync(connected[0], data, timeout.Token);

            if (options.WarmupOperations > 0)
            {
                PhaseOutcome warmup = await RunPhaseAsync(connected, data, options,
                    options.WarmupOperations, null, timeout.Token);
                if (warmup.Successful != options.WarmupOperations)
                    throw new InvalidDataException($"Warmup failed: {warmup.Error}");
            }

            measured = await RunPhaseAsync(connected, data, options,
                options.Operations, latencyTicks, timeout.Token);
            if (measured.Successful != options.Operations)
                error = measured.Error ?? "Incomplete measured phase.";
            else
                await CheckForExtraRepliesAsync(connected, timeout.Token);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (options.Workload != BenchmarkWorkload.Ping && clients[0] is not null)
            {
                try { await CleanupDatasetAsync(clients[0]!, data, timeout.Token); }
                catch (Exception ex) { error = Append(error, $"Cleanup: {ex.GetType().Name}: {ex.Message}"); }
            }
            foreach (BenchmarkClient? client in clients)
                if (client is not null) await client.DisposeAsync();
        }

        long successful = measured?.Successful ?? 0;
        long failed = options.Operations - successful;
        bool valid = error is null && failed == 0;
        double elapsed = measured?.ElapsedSeconds ?? 0;
        double? p50 = null, p95 = null, p99 = null;
        if (valid)
        {
            Array.Sort(latencyTicks);
            p50 = Percentile(latencyTicks, 0.50);
            p95 = Percentile(latencyTicks, 0.95);
            p99 = Percentile(latencyTicks, 0.99);
        }
        return new BenchmarkRunResult(repetition, options.Operations, successful, failed,
            elapsed, elapsed > 0 ? successful / elapsed : 0, p50, p95, p99, valid, error);
    }

    private static async Task<PhaseOutcome> RunPhaseAsync(
        BenchmarkClient[] clients, WorkloadData data, BenchmarkOptions options,
        int operations, long[]? latencyTicks, CancellationToken cancellationToken)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkerOutcome>[] workers = Enumerable.Range(0, clients.Length)
            .Select(index => RunWorkerAsync(clients[index], index, clients.Length,
                operations, options.PipelineDepth, data, options.Workload,
                latencyTicks, start.Task, cancellationToken))
            .ToArray();
        long startedAt = Stopwatch.GetTimestamp();
        start.SetResult(true);
        WorkerOutcome[] outcomes = await Task.WhenAll(workers);
        double elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        return new PhaseOutcome(outcomes.Sum(result => result.Successful), elapsed,
            string.Join("; ", outcomes.Where(result => result.Error is not null)
                .Select(result => result.Error).Take(5)));
    }

    private static async Task<WorkerOutcome> RunWorkerAsync(
        BenchmarkClient client, int clientIndex, int clientCount, int operationCount,
        int pipelineDepth, WorkloadData data, BenchmarkWorkload workload,
        long[]? latencyTicks, Task start, CancellationToken cancellationToken)
    {
        long successful = 0;
        int[] indices = new int[pipelineDepth];
        byte[] batch = new byte[pipelineDepth * data.MaxRequestLength];
        try
        {
            await start;
            for (int operation = clientIndex; operation < operationCount;)
            {
                int count = 0, bytes = 0;
                while (count < pipelineDepth && operation < operationCount)
                {
                    indices[count++] = operation;
                    byte[] request = data.RequestFor(workload, operation);
                    request.CopyTo(batch.AsSpan(bytes));
                    bytes += request.Length;
                    operation += clientCount;
                }

                long submittedAt = Stopwatch.GetTimestamp();
                await client.Stream.WriteAsync(batch.AsMemory(0, bytes), cancellationToken);
                for (int i = 0; i < count; i++)
                {
                    int index = indices[i];
                    switch (workload)
                    {
                        case BenchmarkWorkload.Ping:
                            await client.Reader.ReadSimpleAsync(PingReply, cancellationToken);
                            break;
                        case BenchmarkWorkload.Set:
                            await client.Reader.ReadSimpleAsync(OkReply, cancellationToken);
                            break;
                        case BenchmarkWorkload.Get:
                            await client.Reader.ReadBulkAsync(data.Values[index % data.Values.Length],
                                cancellationToken);
                            break;
                    }
                    if (latencyTicks is not null)
                        latencyTicks[index] = Stopwatch.GetTimestamp() - submittedAt;
                    successful++;
                }
            }
            return new WorkerOutcome(successful, null);
        }
        catch (Exception ex)
        {
            return new WorkerOutcome(successful,
                $"client {clientIndex}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task PrepareDatasetAsync(BenchmarkClient client,
        WorkloadData data, CancellationToken cancellationToken)
    {
        for (int start = 0; start < data.SetFrames.Length;)
        {
            int count = BatchCount(data.SetFrames, start);
            await WriteFramesAsync(client.Stream, data.SetFrames, start, count, cancellationToken);
            for (int i = 0; i < count; i++)
                await client.Reader.ReadSimpleAsync(OkReply, cancellationToken);
            start += count;
        }
    }

    private static async Task CleanupDatasetAsync(BenchmarkClient client,
        WorkloadData data, CancellationToken cancellationToken)
    {
        for (int start = 0; start < data.DeleteFrames.Length;)
        {
            int count = BatchCount(data.DeleteFrames, start);
            await WriteFramesAsync(client.Stream, data.DeleteFrames, start, count, cancellationToken);
            for (int i = 0; i < count; i++)
                if (await client.Reader.ReadIntegerAsync(cancellationToken) != 1)
                    throw new InvalidDataException("Dataset key missing during cleanup.");
            start += count;
        }
    }

    private static int BatchCount(byte[][] frames, int start)
    {
        int count = 0, bytes = 0;
        while (start + count < frames.Length && count < 64 &&
            (count == 0 || bytes + (long)frames[start + count].Length <= 1024 * 1024))
            bytes += frames[start + count++].Length;
        return count;
    }

    private static async Task WriteFramesAsync(NetworkStream stream, byte[][] frames,
        int start, int count, CancellationToken cancellationToken)
    {
        int length = 0;
        for (int i = start; i < start + count; i++) length += frames[i].Length;
        byte[] batch = new byte[length];
        int offset = 0;
        for (int i = start; i < start + count; i++)
        {
            frames[i].CopyTo(batch.AsSpan(offset));
            offset += frames[i].Length;
        }
        await stream.WriteAsync(batch, cancellationToken);
    }

    private static async Task CheckForExtraRepliesAsync(
        BenchmarkClient[] clients, CancellationToken cancellationToken)
    {
        await Task.WhenAll(clients.Select(async (client, index) =>
        {
            byte[] marker = Encoding.ASCII.GetBytes($"marker-{index:D3}");
            await client.Stream.WriteAsync(Frame(Encoding.ASCII.GetBytes("PING"), marker),
                cancellationToken);
            await client.Reader.ReadBulkAsync(marker, cancellationToken);
        }));
    }

    private static WorkloadData BuildData(BenchmarkOptions options, int repetition)
    {
        byte[] ping = Frame(Encoding.ASCII.GetBytes("PING"));
        if (options.Workload == BenchmarkWorkload.Ping)
            return new WorkloadData([], [], [], [], ping, ping.Length);

        byte[][] keys = new byte[options.KeyCount][];
        byte[][] values = new byte[options.KeyCount][];
        byte[][] setFrames = new byte[options.KeyCount][];
        byte[][] getFrames = new byte[options.KeyCount][];
        byte[][] deleteFrames = new byte[options.KeyCount][];
        byte[] set = Encoding.ASCII.GetBytes("SET");
        byte[] get = Encoding.ASCII.GetBytes("GET");
        byte[] del = Encoding.ASCII.GetBytes("DEL");
        string prefix = $"bench:{options.Workload}:v{options.ValueSize}:c{options.Clients}:p{options.PipelineDepth}:r{repetition}:";
        int maxLength = ping.Length;
        for (int i = 0; i < options.KeyCount; i++)
        {
            keys[i] = Encoding.ASCII.GetBytes($"{prefix}{i:D5}");
            values[i] = new byte[options.ValueSize];
            for (int j = 0; j < values[i].Length; j++)
                values[i][j] = (byte)(i * 31 + j * 17);
            setFrames[i] = Frame(set, keys[i], values[i]);
            getFrames[i] = Frame(get, keys[i]);
            deleteFrames[i] = Frame(del, keys[i]);
            maxLength = Math.Max(maxLength, Math.Max(setFrames[i].Length, getFrames[i].Length));
        }
        return new WorkloadData(values, setFrames, getFrames, deleteFrames, ping, maxLength);
    }

    public static byte[] Frame(params byte[][] arguments)
    {
        using var output = new MemoryStream();
        WriteNumber(output, (byte)'*', arguments.Length);
        foreach (byte[] argument in arguments)
        {
            WriteNumber(output, (byte)'$', argument.Length);
            output.Write(argument);
            output.Write("\r\n"u8);
        }
        return output.ToArray();
    }

    private static void WriteNumber(Stream output, byte prefix, int number)
    {
        output.WriteByte(prefix);
        output.Write(Encoding.ASCII.GetBytes(number.ToString(CultureInfo.InvariantCulture)));
        output.Write("\r\n"u8);
    }

    private static string Append(string? first, string second) =>
        first is null ? second : first + "; " + second;

    private static double Percentile(long[] sortedTicks, double percentile)
    {
        int index = Math.Max(0, (int)Math.Ceiling(sortedTicks.Length * percentile) - 1);
        return sortedTicks[index] * 1_000_000.0 / Stopwatch.Frequency;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private sealed record WorkloadData(
        byte[][] Values, byte[][] SetFrames, byte[][] GetFrames,
        byte[][] DeleteFrames, byte[] PingFrame, int MaxRequestLength)
    {
        public byte[] RequestFor(BenchmarkWorkload workload, int operation) =>
            workload switch
            {
                BenchmarkWorkload.Ping => PingFrame,
                BenchmarkWorkload.Set => SetFrames[operation % SetFrames.Length],
                _ => GetFrames[operation % GetFrames.Length]
            };
    }

    private sealed class BenchmarkClient(TcpClient tcp, RespBenchmarkReader reader) : IAsyncDisposable
    {
        public NetworkStream Stream { get; } = tcp.GetStream();
        public RespBenchmarkReader Reader { get; } = reader;

        public static async Task<BenchmarkClient> ConnectAsync(
            string host, int port, int maxPayload, CancellationToken cancellationToken)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(host, port, cancellationToken);
                return new BenchmarkClient(tcp,
                    new RespBenchmarkReader(tcp.GetStream(), maxPayload));
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record WorkerOutcome(long Successful, string? Error);
    private sealed record PhaseOutcome(long Successful, double ElapsedSeconds, string Error);
}

public sealed record BenchmarkRunResult(
    int Repetition, long TotalOperations, long SuccessfulOperations, long FailedOperations,
    double ElapsedSeconds, double SuccessfulOpsPerSecond,
    double? P50Microseconds, double? P95Microseconds, double? P99Microseconds,
    bool Valid, string? Error);

public sealed record BenchmarkAggregate(
    double SuccessfulOpsPerSecond, double P50Microseconds,
    double P95Microseconds, double P99Microseconds);

public sealed record BenchmarkReport(
    int SchemaVersion, DateTimeOffset CapturedUtc, string Host, int Port,
    string Workload, int Clients, int Operations, int PipelineDepth,
    int ValueSize, int WarmupOperations, int Repetitions, int KeyCount,
    string LatencyDefinition, string ClientRuntimeVersion, string ClientOs,
    long StopwatchFrequency, IReadOnlyList<BenchmarkRunResult> Runs,
    BenchmarkAggregate? Median);
