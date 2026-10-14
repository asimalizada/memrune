using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

int port = 6380;
int clients = 100;
int operations = 100;

if (args.Length % 2 != 0)
    return Usage();
for (int i = 0; i < args.Length; i += 2)
{
    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        return Usage();
    switch (args[i])
    {
        case "--port" when value is >= 1 and <= 65535:
            port = value;
            break;
        case "--clients" when value > 0:
            clients = value;
            break;
        case "--operations" when value > 0:
            operations = value;
            break;
        default:
            return Usage();
    }
}

byte[] request = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
byte[] expected = Encoding.ASCII.GetBytes("+PONG\r\n");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
long successful = 0;
var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
var stopwatch = Stopwatch.StartNew();

await Task.WhenAll(Enumerable.Range(0, clients).Select(async index =>
{
    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        NetworkStream stream = client.GetStream();
        var response = new byte[expected.Length];
        for (int operation = 0; operation < operations; operation++)
        {
            await stream.WriteAsync(request, timeout.Token);
            await stream.ReadExactlyAsync(response, timeout.Token);
            if (!response.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Unexpected RESP response");
            Interlocked.Increment(ref successful);
        }
    }
    catch (Exception ex)
    {
        failures.Add($"client {index}: {ex.GetType().Name}: {ex.Message}");
    }
}));

stopwatch.Stop();
long total = (long)clients * operations;
long errors = total - successful;
Console.WriteLine($"Clients: {clients}");
Console.WriteLine($"Operations per client: {operations}");
Console.WriteLine($"Successful responses: {successful}");
Console.WriteLine($"Errors or incomplete operations: {errors}");
Console.WriteLine($"Elapsed: {stopwatch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} s");
Console.WriteLine($"Throughput: {(successful / stopwatch.Elapsed.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)} successful ops/s");
foreach (string failure in failures.OrderBy(x => x).Take(10))
    Console.Error.WriteLine(failure);
if (failures.Count > 10)
    Console.Error.WriteLine($"{failures.Count - 10} further client failures omitted");
return errors == 0 ? 0 : 1;

static int Usage()
{
    Console.Error.WriteLine("Usage: Memrune.LoadTest [--port 1-65535] [--clients count] [--operations per-client-count]");
    return 2;
}