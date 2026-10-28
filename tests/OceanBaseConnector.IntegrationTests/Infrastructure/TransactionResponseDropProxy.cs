using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

internal enum CommandDropPoint { BeforeForward, PartialRequest, WholeResponse, PartialResponse }

// Test-only plaintext relay: two sequential sessions, one exact command fault.
internal sealed class TransactionResponseDropProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _host;
    private readonly int _upstreamPort;
    private readonly string _target;
    private readonly CommandDropPoint _point;
    private int _armed;
    private int _awaitingResponse;
    private int _targetRequests;
    private int _connections;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal int TargetRequests => Volatile.Read(ref _targetRequests);
    internal int Connections => Volatile.Read(ref _connections);
    internal byte? ObservedResponseMarker { get; private set; }
    internal Task Completion { get; }

    internal TransactionResponseDropProxy(string host, int port, string target, bool partial)
        : this(host, port, RequireTransactionEnd(target), partial ? CommandDropPoint.PartialResponse : CommandDropPoint.WholeResponse) { }

    private static string RequireTransactionEnd(string target) => target is "COMMIT" or "ROLLBACK" ? target
        : throw new ArgumentException("A transaction end is required.", nameof(target));

    internal TransactionResponseDropProxy(string host, int port, string target, CommandDropPoint point)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("An exact command is required.", nameof(target));
        if (!Enum.IsDefined(point)) throw new ArgumentOutOfRangeException(nameof(point));
        _host = host; _upstreamPort = port; _target = target; _point = point;
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
                Interlocked.Increment(ref _connections);
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
            if (request && MatchesCommand(frame))
            {
                Interlocked.Increment(ref _targetRequests);
                if (Interlocked.Exchange(ref _armed, 0) != 0)
                {
                    // The provider has already sent to this relay; neither path proves local pre-send.
                    if (_point == CommandDropPoint.BeforeForward) return;
                    if (_point == CommandDropPoint.PartialRequest)
                    {
                        await output.WriteAsync(frame.AsMemory(0, 5), token);
                        return;
                    }
                    Volatile.Write(ref _awaitingResponse, 1);
                }
            }
            if (!request && Interlocked.Exchange(ref _awaitingResponse, 0) != 0)
            {
                if (frame.Length <= 5) throw new InvalidDataException("A complete command response payload is required.");
                ObservedResponseMarker = frame[4];
                // Keep the original length, but deliver at most one payload byte to the driver.
                if (_point == CommandDropPoint.PartialResponse) await output.WriteAsync(frame.AsMemory(0, 5), token);
                return;
            }
            await output.WriteAsync(frame, token);
        }
    }

    private bool MatchesCommand(byte[] frame)
    {
        if (frame.Length <= 5 || frame[3] != 0) return false;
        if (frame[4] == 3) return Encoding.UTF8.GetString(frame, 5, frame.Length - 5) == _target;
        if (frame[4] != 0xa1) return false;
        using var payload = new MemoryStream(frame, 4, frame.Length - 4, writable: false);
        using var reader = new BinaryReader(payload);
        reader.ReadByte(); reader.ReadUInt32(); reader.ReadByte(); reader.ReadUInt32();
        ulong length = reader.ReadByte();
        length = length switch
        {
            0xfb or 0xff => throw new InvalidDataException("A non-null SQL length is required."),
            0xfc => reader.ReadUInt16(),
            0xfd => (uint)(reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16),
            0xfe => reader.ReadUInt64(),
            _ => length,
        };
        if (length > (ulong)(payload.Length - payload.Position)) throw new InvalidDataException("The SQL exceeds its request frame.");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length)) == _target;
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
