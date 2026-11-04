using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

// After Arm, preparatory traffic passes through until the first Execute; only two DML replies are gated.
internal sealed class MultiSendResponseGate : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _secondSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed, _started, _executions;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal int Executions => Volatile.Read(ref _executions);
    internal Task SecondResponseHeld => _held.Task;
    internal Task Completion { get; }
    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal MultiSendResponseGate(string host, int port)
    {
        _listener.Start(); Completion = Run(host, port);
    }

    private async Task Run(string host, int port)
    {
        try
        {
            using var client = await TransactionResponseDropProxy.AcceptAsync(_listener, _stop.Token);
            if (client == null) return;
            using var upstream = new TcpClient(); await upstream.ConnectAsync(host, port, _stop.Token);
            using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var sending = ForwardRequests(client.GetStream(), upstream.GetStream(), sessionStop.Token);
            var receiving = ForwardResponses(upstream.GetStream(), client.GetStream(), sessionStop.Token);
            var first = await Task.WhenAny(sending, receiving);
            var errors = new List<Exception>();
            try { await first; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { errors.Add(error); }
            sessionStop.Cancel(); client.Dispose(); upstream.Dispose();
            var other = ReferenceEquals(first, sending) ? receiving : sending;
            try { await other; }
            catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (sessionStop.IsCancellationRequested) { }
            catch (IOException error) when (error.InnerException is SocketException socket &&
                socket.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.ConnectionAborted or SocketError.ConnectionReset)
            { }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException(errors);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ForwardRequests(Stream client, Stream upstream, CancellationToken token)
    {
        while (await TransactionResponseDropProxy.ReadFrame(client, token) is { } frame)
        {
            var execute = Volatile.Read(ref _armed) != 0 && frame.Length >= 5 && frame[3] == 0 && frame[4] == 23;
            if (Volatile.Read(ref _started) != 0 && !execute)
                throw new InvalidDataException("An armed multi-send gate accepts only COM_STMT_EXECUTE frames.");
            if (execute) Volatile.Write(ref _started, 1);
            await upstream.WriteAsync(frame, token);
            if (execute && Interlocked.Increment(ref _executions) == 2) _secondSent.TrySetResult();
        }
    }

    private async Task ForwardResponses(Stream upstream, Stream client, CancellationToken token)
    {
        var responses = 0;
        while (await TransactionResponseDropProxy.ReadFrame(upstream, token) is { } frame)
        {
            if (Volatile.Read(ref _started) != 0)
            {
                if (frame.Length < 11 || frame[3] != 1 || frame[4] != 0)
                    throw new InvalidDataException("The multi-send gate requires a complete single-packet DML OK response.");
                await _secondSent.Task.WaitAsync(token);
                if (++responses == 2)
                {
                    _held.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
            }
            await client.WriteAsync(frame, token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await Completion; }
        finally { _stop.Dispose(); }
    }
}
