using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;

namespace OceanBaseConnector.Acceptance;

// Test-only transparent relay: no protocol parsing, buffering to disk or traffic logging.
internal sealed class TopologyRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = new();
    private readonly string _host;
    private readonly int _upstreamPort;
    private readonly Task _accepting;
    private readonly TaskCompletionSource _firstClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connections;
    internal int Port { get; }
    internal int Connections => Volatile.Read(ref _connections);
    internal Task FirstClosed => _firstClosed.Task;

    internal TopologyRelay(string host, int port)
    {
        _host = host; _upstreamPort = port;
        _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await TransactionResponseDropProxy.AcceptAsync(_listener, _stop.Token);
                if (client == null) return;
                var number = Interlocked.Increment(ref _connections);
                _sessions.Add(Forward(client, number));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task Forward(TcpClient client, int number)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            try
            {
                await upstream.ConnectAsync(_host, _upstreamPort, _stop.Token);
                client.NoDelay = true; upstream.NoDelay = true;
                var sending = client.GetStream().CopyToAsync(upstream.GetStream(), sessionStop.Token);
                var receiving = upstream.GetStream().CopyToAsync(client.GetStream(), sessionStop.Token);
                var first = await Task.WhenAny(sending, receiving);
                try { await first; }
                finally
                {
                    sessionStop.Cancel(); client.Dispose(); upstream.Dispose();
                    try { await (ReferenceEquals(first, sending) ? receiving : sending); }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                    catch (ObjectDisposedException) when (sessionStop.IsCancellationRequested) { }
                    catch (IOException error) when (sessionStop.IsCancellationRequested && error.InnerException is SocketException socket &&
                        socket.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.ConnectionAborted or SocketError.ConnectionReset)
                    { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { if (number == 1) _firstClosed.TrySetResult(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        var errors = new List<Exception>();
        try { await _accepting; } catch (Exception error) { errors.Add(error); }
        var completion = Task.WhenAll(_sessions);
        try { await completion; }
        catch (Exception error)
        {
            if (completion.Exception is { } aggregate) errors.AddRange(aggregate.InnerExceptions);
            else errors.Add(error);
        }
        _stop.Dispose();
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
