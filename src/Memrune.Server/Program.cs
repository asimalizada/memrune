using Memrune.Server;

int port = 6380;
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--port" ||
        !int.TryParse(args[1], out port) || port is < 1 or > 65535)
    {
        Console.Error.WriteLine("Usage: Memrune.Server [--port 1-65535]");
        return 2;
    }
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    var server = new MemruneServer(port);
    Task running = server.RunAsync(shutdown.Token);
    Console.WriteLine($"MEMRUNE listening on 127.0.0.1:{server.BoundPort}");
    await running;
    return 0;
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
{
    Console.Error.WriteLine($"Server error: {ex.Message}");
    return 1;
}