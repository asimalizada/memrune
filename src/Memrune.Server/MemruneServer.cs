using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Memrune.Core;
using Memrune.Protocol;

namespace Memrune.Server;

public sealed class MemruneServer(int port = 6380)
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, ValidatePort(port));
    private readonly ConcurrentDictionary<TcpClient, ClientSession> _clients = new();

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }

                var session = new ClientSession(client);
                _clients.TryAdd(client, session);
                session.Task = HandleClientAsync(session, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
            ClientSession[] remaining = _clients.Values.ToArray();
            foreach (ClientSession session in remaining) session.Client.Dispose();
            await Task.WhenAll(remaining.Select(session => session.Task));
        }
    }

    private async Task HandleClientAsync(ClientSession session, CancellationToken cancellationToken)
    {
        using TcpClient client = session.Client;
        try
        {
            await using NetworkStream stream = client.GetStream();
            var reader = new RespReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[][]? command;
                try { command = await reader.ReadCommandAsync(cancellationToken); }
                catch (RespProtocolException ex)
                {
                    await RespWriter.WriteAsync(stream, RespValue.Error($"ERR Protocol error: {ex.Message}"), cancellationToken);
                    break;
                }
                if (command is null) break;

                var (response, close) = CommandExecutor.Execute(command);
                await RespWriter.WriteAsync(stream, response, cancellationToken);
                if (close) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally { _clients.TryRemove(client, out _); }
    }

    private static int ValidatePort(int port) => port is >= 0 and <= 65535
        ? port : throw new ArgumentOutOfRangeException(nameof(port));

    private sealed class ClientSession(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}