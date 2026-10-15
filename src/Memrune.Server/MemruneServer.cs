using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Memrune.Core;
using Memrune.Protocol;

namespace Memrune.Server;

public sealed class MemruneServer(int port = 6380, KeyValueStore? store = null, int maxClients = 512, IPAddress? bindAddress = null, bool tcpNoDelay = true, bool coalesceResponses = true)
{
    private readonly TcpListener _listener = new(bindAddress ?? IPAddress.Loopback, ValidatePort(port));
    private readonly ConcurrentDictionary<TcpClient, ClientSession> _clients = new();
    private readonly CommandExecutor _commands = new(store ?? new KeyValueStore());
    private readonly SemaphoreSlim _clientSlots = new(ValidateMaxClients(maxClients), maxClients);

    private readonly bool _tcpNoDelay = tcpNoDelay;
    private readonly bool _coalesceResponses = coalesceResponses;

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IPAddress BoundAddress => ((IPEndPoint)_listener.LocalEndpoint).Address;

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

                if (_tcpNoDelay)
                {
                    try { client.NoDelay = true; }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        client.Dispose();
                        continue;
                    }
                }

                if (!_clientSlots.Wait(0))
                {
                    await RejectBusyClientAsync(client, cancellationToken);
                    continue;
                }

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
            _clientSlots.Dispose();
        }
    }

    private static async Task RejectBusyClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await RespWriter.WriteAsync(client.GetStream(),
                    RespValue.Error("ERR max number of clients reached"), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The rejected client may disconnect before receiving the error.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unexpected rejection failure: {ex}");
            }
        }
    }

    private async Task HandleClientAsync(ClientSession session, CancellationToken cancellationToken)
    {
        using TcpClient client = session.Client;
        try
        {
            await using NetworkStream stream = client.GetStream();
            var reader = new RespReader(stream);
            var responseWriter = _coalesceResponses ? new RespCoalescedWriter() : null;
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[][]? command;
                try { command = await reader.ReadCommandAsync(cancellationToken); }
                catch (RespProtocolException ex)
                {
                    await WriteResponseAsync(stream, RespValue.Error($"ERR Protocol error: {ex.Message}"), responseWriter, cancellationToken);
                    break; // A malformed frame cannot be safely resynchronized.
                }
                if (command is null) break;

                var (response, close) = _commands.Execute(command);
                await WriteResponseAsync(stream, response, responseWriter, cancellationToken);
                if (close) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { } // Disconnects and resets are local to this client.
        catch (SocketException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // This task must not fault unnoticed after it leaves the active-client map.
            Console.Error.WriteLine($"Unexpected client failure: {ex}");
        }
        finally
        {
            _clientSlots.Release();
            _clients.TryRemove(client, out _);
        }
    }

    private static Task WriteResponseAsync(Stream stream, RespValue response,
        RespCoalescedWriter? writer, CancellationToken cancellationToken) =>
        writer is null
            ? RespWriter.WriteAsync(stream, response, cancellationToken)
            : writer.WriteAsync(stream, response, cancellationToken);

    private static int ValidatePort(int port) => port is >= 0 and <= 65535
        ? port : throw new ArgumentOutOfRangeException(nameof(port));

    private static int ValidateMaxClients(int maxClients) => maxClients > 0
        ? maxClients : throw new ArgumentOutOfRangeException(nameof(maxClients));

    private sealed class ClientSession(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}