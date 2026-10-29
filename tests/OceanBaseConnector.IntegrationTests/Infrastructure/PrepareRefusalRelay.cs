using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

// Refuse the exact Prepare before it reaches upstream; all execution remains real OceanBase.
internal sealed class PrepareRefusalRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _prepare;
    private readonly Task _completion;
    private int _armed, _refusals, _prepares, _forwarded;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Refusals => Volatile.Read(ref _refusals);
    public int PrepareRequests => Volatile.Read(ref _prepares);
    public int ForwardedPrepares => Volatile.Read(ref _forwarded);
    public void Arm() => Volatile.Write(ref _armed, 1);
    public PrepareRefusalRelay(string host, int port, string sql)
    {
        _prepare = new byte[] { 22 }.Concat(Encoding.UTF8.GetBytes(sql)).ToArray();
        _listener.Start(); _completion = Run(host, port);
    }
    private async Task Run(string host, int port)
    {
        using var client = await TransactionResponseDropProxy.AcceptAsync(_listener, _stop.Token); if (client == null) return;
        using var upstream = new TcpClient(); await upstream.ConnectAsync(host, port, _stop.Token);
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var sending = Forward(client.GetStream(), upstream.GetStream(), sessionStop.Token);
        var receiving = upstream.GetStream().CopyToAsync(client.GetStream(), sessionStop.Token);
        try { await await Task.WhenAny(sending, receiving); }
        finally
        {
            sessionStop.Cancel(); client.Dispose(); upstream.Dispose();
            try { await Task.WhenAll(sending, receiving); }
            catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
        }
    }
    private async Task Forward(Stream client, Stream upstream, CancellationToken token)
    {
        while (true)
        {
            var header = new byte[4]; if (await client.ReadAsync(header.AsMemory(0, 1), token) == 0) return;
            await client.ReadExactlyAsync(header.AsMemory(1), token);
            var length = header[0] | header[1] << 8 | header[2] << 16;
            if (length > 1024 * 1024) throw new InvalidDataException("The fallback test relay accepts frames up to one MiB.");
            var body = new byte[length]; await client.ReadExactlyAsync(body, token);
            if (header[3] == 0 && body.AsSpan().SequenceEqual(_prepare))
            {
                Interlocked.Increment(ref _prepares);
                if (Interlocked.Exchange(ref _armed, 0) != 0)
                {
                    await client.WriteAsync(new byte[] { 10, 0, 0, 1, 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 }, token);
                    Interlocked.Increment(ref _refusals); continue;
                }
                Interlocked.Increment(ref _forwarded);
            }
            await upstream.WriteAsync(header, token); await upstream.WriteAsync(body, token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await _completion; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _stop.Dispose(); }
    }
}
