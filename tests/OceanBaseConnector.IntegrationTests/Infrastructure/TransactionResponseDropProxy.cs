using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

// Test-only plaintext relay: two sequential sessions, one exact transaction-end response fault.
internal sealed class TransactionResponseDropProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _host;
    private readonly int _upstreamPort;
    private readonly string _target;
    private readonly bool _partial;
    private int _armed;
    private int _awaitingResponse;
    private int _targetRequests;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal int TargetRequests => Volatile.Read(ref _targetRequests);
    internal byte? ObservedResponseMarker { get; private set; }
    internal Task Completion { get; }

    internal TransactionResponseDropProxy(string host, int port, string target, bool partial)
    {
        if (target != "COMMIT" && target != "ROLLBACK") throw new ArgumentException("A transaction end is required.", nameof(target));
        _host = host; _upstreamPort = port; _target = target; _partial = partial;
        _listener.Start(); Completion = Run();
    }

    internal void Arm() => Volatile.Write(ref _armed, 1);

    private async Task Run()
    {
        try
        {
            for (var session = 0; session < 2; session++)
            {
                using var client = await AcceptAsync(_listener, _stop.Token);
                if (client == null) return;
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(_host, _upstreamPort, _stop.Token);
                using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var sending = Forward(client.GetStream(), upstream.GetStream(), true, sessionStop.Token);
                var receiving = Forward(upstream.GetStream(), client.GetStream(), false, sessionStop.Token);
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
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal static async Task<TcpClient?> AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try { return await listener.AcceptTcpClientAsync(token); }
        catch (InvalidOperationException) when (token.IsCancellationRequested) { return null; }
        catch (SocketException error) when (token.IsCancellationRequested &&
            error.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        { return null; }
    }

    private async Task Forward(Stream input, Stream output, bool request, CancellationToken token)
    {
        while (true)
        {
            var frame = await ReadFrame(input, token);
            if (frame == null) return;
            if (request && frame.Length > 5 && frame[3] == 0 && frame[4] == 3 &&
                Encoding.UTF8.GetString(frame, 5, frame.Length - 5) == _target)
            {
                Interlocked.Increment(ref _targetRequests);
                if (Interlocked.Exchange(ref _armed, 0) != 0) Volatile.Write(ref _awaitingResponse, 1);
            }
            if (!request && Interlocked.Exchange(ref _awaitingResponse, 0) != 0)
            {
                if (frame.Length <= 5) throw new InvalidDataException("A complete transaction response payload is required.");
                ObservedResponseMarker = frame[4];
                // Keep the original length, but deliver at most one payload byte to the driver.
                if (_partial) await output.WriteAsync(frame.AsMemory(0, 5), token);
                return;
            }
            await output.WriteAsync(frame, token);
        }
    }

    private static async Task<byte[]?> ReadFrame(Stream input, CancellationToken token)
    {
        var header = new byte[4];
        if (await input.ReadAsync(header.AsMemory(0, 1), token) == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1), token);
        var length = header[0] | header[1] << 8 | header[2] << 16;
        if (length > 1024 * 1024) throw new InvalidDataException("The fault fixture accepts frames up to one MiB.");
        var frame = new byte[4 + length]; header.CopyTo(frame, 0);
        await input.ReadExactlyAsync(frame.AsMemory(4), token);
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await Completion; }
        finally { _stop.Dispose(); }
    }
}
