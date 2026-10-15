using System.Globalization;

public enum BenchmarkWorkload { Ping, Set, Get }

public sealed record BenchmarkOptions(
    string Host,
    int Port,
    BenchmarkWorkload Workload,
    int Clients,
    int Operations,
    int PipelineDepth,
    int ValueSize,
    int WarmupOperations,
    int Repetitions,
    int KeyCount,
    string? Output,
    int? OfferedRate = null,
    int MaxLagMilliseconds = 20,
    int RequestTimeoutMilliseconds = 5000)
{
    public static bool TryParse(string[] args, out BenchmarkOptions? options, out string error)
    {
        string host = "127.0.0.1";
        int port = 6380, clients = 1, operations = 0, pipeline = 1;
        int valueSize = 32, warmup = 0, repetitions = 3, keys = 1024;
        int? offeredRate = null;
        int maxLagMilliseconds = 20, requestTimeoutMilliseconds = 5000;
        bool operationsSpecified = false, warmupSpecified = false;
        string? output = null;
        BenchmarkWorkload workload = BenchmarkWorkload.Ping;

        options = null;
        error = "";
        if (args.Length % 2 != 0)
        {
            error = "Options must be name/value pairs.";
            return false;
        }
        for (int i = 0; i < args.Length; i += 2)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--host": host = value; break;
                case "--port" when ParseInt(value, out int parsedPort): port = parsedPort; break;
                case "--clients" when ParseInt(value, out int parsedClients): clients = parsedClients; break;
                case "--operations" when ParseInt(value, out int parsedOperations):
                    operations = parsedOperations; operationsSpecified = true; break;
                case "--pipeline" when ParseInt(value, out int parsedPipeline): pipeline = parsedPipeline; break;
                case "--value-size" when ParseInt(value, out int parsedSize): valueSize = parsedSize; break;
                case "--warmup" when ParseInt(value, out int parsedWarmup):
                    warmup = parsedWarmup; warmupSpecified = true; break;
                case "--repetitions" when ParseInt(value, out int parsedRepetitions): repetitions = parsedRepetitions; break;
                case "--keys" when ParseInt(value, out int parsedKeys): keys = parsedKeys; break;
                case "--output": output = value; break;
                case "--rate" when ParseInt(value, out int parsedRate): offeredRate = parsedRate; break;
                case "--max-lag-ms" when ParseInt(value, out int parsedLag): maxLagMilliseconds = parsedLag; break;
                case "--request-timeout-ms" when ParseInt(value, out int parsedTimeout): requestTimeoutMilliseconds = parsedTimeout; break;
                case "--workload" when value.Equals("ping", StringComparison.OrdinalIgnoreCase):
                    workload = BenchmarkWorkload.Ping; break;
                case "--workload" when value.Equals("set", StringComparison.OrdinalIgnoreCase):
                    workload = BenchmarkWorkload.Set; break;
                case "--workload" when value.Equals("get", StringComparison.OrdinalIgnoreCase):
                    workload = BenchmarkWorkload.Get; break;
                default:
                    error = $"Unknown or invalid option: {args[i]} {value}";
                    return false;
            }
        }

        if (!operationsSpecified)
            operations = offeredRate is int rate ? (int)Math.Min((long)rate * 3, 1_000_000) :
                Math.Max(128, clients * (pipeline == 1 ? 64 : 512));
        if (!warmupSpecified)
            warmup = offeredRate is int fixedRate
                ? (int)Math.Min(1_000_000L, Math.Max((long)clients * 8, (long)fixedRate * 2))
                : clients * 8;

        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 ||
            clients is < 1 or > 512 || operations is < 1 or > 1_000_000 ||
            operations < clients || pipeline is < 1 or > 1024 ||
            valueSize is < 0 or > 1024 * 1024 || warmup is < 0 or > 1_000_000 ||
            repetitions is < 1 or > 20 || keys is < 1 or > 100_000 ||
            (workload != BenchmarkWorkload.Ping && valueSize == 0) ||
            (offeredRate is int requestedRate && (requestedRate is < 1 or > 100_000 || pipeline != 1)) ||
            maxLagMilliseconds is < 1 or > 10_000 ||
            requestTimeoutMilliseconds is < 1 or > 60_000 ||
            (output is not null && string.IsNullOrWhiteSpace(output)) ||
            (long)pipeline * (valueSize + 256L) > 4L * 1024 * 1024 ||
            (long)clients * pipeline * (valueSize + 256L) > 256L * 1024 * 1024 ||
            (workload != BenchmarkWorkload.Ping &&
             (long)keys * (2L * valueSize + 256L) > 256L * 1024 * 1024))
        {
            error = "Out-of-range benchmark parameters.";
            return false;
        }
        options = new(host, port, workload, clients, operations, pipeline,
            valueSize, warmup, repetitions, keys, output, offeredRate,
            maxLagMilliseconds, requestTimeoutMilliseconds);
        return true;
    }

    private static bool ParseInt(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}