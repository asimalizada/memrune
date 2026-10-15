using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Memrune.Server;

int port = 6380;
IPAddress bindAddress = IPAddress.Loopback;
bool tcpNoDelay = true;
bool coalesceResponses = true;

for (int i = 0; i < args.Length;)
{
    if (args[i] == "--coalesce-responses")
    {
        coalesceResponses = true;
        i++;
        continue;
    }
    if (args[i] == "--tcp-nodelay")
    {
        tcpNoDelay = true;
        i++;
        continue;
    }
    if (args[i] == "--no-coalesce-responses")
    {
        coalesceResponses = false;
        i++;
        continue;
    }
    if (args[i] == "--no-tcp-nodelay")
    {
        tcpNoDelay = false;
        i++;
        continue;
    }
    if (i + 1 >= args.Length) return Usage();
    switch (args[i])
    {
        case "--port" when int.TryParse(args[i + 1], out int parsedPort) && parsedPort is >= 1 and <= 65535:
            port = parsedPort;
            break;
        case "--bind" when IPAddress.TryParse(args[i + 1], out IPAddress? parsedAddress)
                           && parsedAddress.AddressFamily == AddressFamily.InterNetwork:
            bindAddress = parsedAddress;
            break;
        default:
            return Usage();
    }
    i += 2;
}

using var shutdown = new CancellationTokenSource();
using PosixSignalRegistration? terminate = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
    {
        signal.Cancel = true;
        shutdown.Cancel();
    })
    : null;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    var server = new MemruneServer(port, bindAddress: bindAddress, tcpNoDelay: tcpNoDelay, coalesceResponses: coalesceResponses);
    Task running = server.RunAsync(shutdown.Token);
    Console.WriteLine($"MEMRUNE listening on {server.BoundAddress}:{server.BoundPort}");
    await running;
    Console.WriteLine("MEMRUNE stopped");
    return 0;
}
catch (Exception ex) when (ex is SocketException or IOException)
{
    Console.Error.WriteLine($"Server error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: Memrune.Server [--port 1-65535] [--bind IPv4-address] [--tcp-nodelay|--no-tcp-nodelay] [--coalesce-responses|--no-coalesce-responses]");
    return 2;
}