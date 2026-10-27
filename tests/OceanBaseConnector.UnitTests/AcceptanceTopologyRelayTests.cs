using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceTopologyRelayTests
{
    [Fact]
    public async Task ConcurrentUpstreamFailuresRemainInCleanupEvidence()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var relay = new TopologyRelay("127.0.0.1", ((IPEndPoint)upstream.LocalEndpoint).Port);
        using var a = new TcpClient(); using var b = new TcpClient();
        var disposed = false;
        try
        {
            await a.ConnectAsync(IPAddress.Loopback, relay.Port, stop.Token);
            using var first = await upstream.AcceptTcpClientAsync(stop.Token);
            await b.ConnectAsync(IPAddress.Loopback, relay.Port, stop.Token);
            using var second = await upstream.AcceptTcpClientAsync(stop.Token);
            await first.GetStream().WriteAsync(new byte[] { 7 }, stop.Token);
            await second.GetStream().WriteAsync(new byte[] { 7 }, stop.Token);
            var observed = new byte[1];
            Assert.Equal(1, await a.GetStream().ReadAsync(observed, stop.Token)); Assert.Equal(7, observed[0]);
            Assert.Equal(1, await b.GetStream().ReadAsync(observed, stop.Token)); Assert.Equal(7, observed[0]);
            first.Client.LingerState = new LingerOption(true, 0);
            second.Client.LingerState = new LingerOption(true, 0);
            // TcpClient.Dispose performs shutdown first; close the linger-zero sockets directly for RST.
            first.Client.Dispose(); second.Client.Dispose();
            Assert.Equal(0, await a.GetStream().ReadAsync(new byte[1], stop.Token));
            Assert.Equal(0, await b.GetStream().ReadAsync(new byte[1], stop.Token));
            await relay.FirstClosed.WaitAsync(stop.Token);
            disposed = true;
            var error = await Assert.ThrowsAsync<AggregateException>(() => relay.DisposeAsync().AsTask());
            var failures = error.Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.All(failures, failure => Assert.IsType<IOException>(failure));
            Assert.Equal(2, relay.Connections);
        }
        finally
        {
            a.Dispose(); b.Dispose();
            if (!disposed) await relay.DisposeAsync();
        }
    }
}
