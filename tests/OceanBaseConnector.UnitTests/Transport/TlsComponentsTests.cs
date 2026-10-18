using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Transport
{
    public sealed class TlsComponentsTests
    {
        [Fact]
        public void SslRequestHasExactWireFormat()
        {
            var capabilities = CapabilityFlags.ClientSsl |
                CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection;
            var packet = SslRequestPacket.Build(capabilities);

            Assert.Equal(32, packet.Length);
            Assert.Equal((uint)capabilities, BitConverter.ToUInt32(packet, 0));
            Assert.Equal(0x1000000, BitConverter.ToInt32(packet, 4));
            Assert.Equal(45, packet[8]);
            for (var i = 9; i < 32; i++)
            {
                Assert.Equal(0, packet[i]);
            }
        }

        [Fact]
        public void SslRequestRejectsOutOfRangeMaxPacketSize()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SslRequestPacket.Build(CapabilityFlags.ClientSsl, maxPacketSize: int.MinValue));
        }

        [Theory]
        [InlineData("ob1.example.com", "", "ob1.example.com")]
        [InlineData("10.0.0.5", "obproxy.example.com", "obproxy.example.com")]
        [InlineData("10.0.0.5", "", "10.0.0.5")]
        public void TargetHostResolutionPrefersExplicitTlsServerName(
            string endpointHost,
            string tlsServerName,
            string expected)
        {
            Assert.Equal(expected, TlsUpgrade.ResolveTargetHost(endpointHost, tlsServerName));
        }

        [Fact]
        public void TargetHostResolutionRejectsNullOverride()
        {
            Assert.Throws<ArgumentNullException>(() => TlsUpgrade.ResolveTargetHost("host", null!));
        }

        [Fact]
        public async Task ConnectAsyncSucceedsAgainstListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var acceptTask = listener.AcceptTcpClientAsync();
                using var transport = await TcpTransport.ConnectAsync(
                    "127.0.0.1", port, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert.True(transport.Stream.CanWrite);
                (await acceptTask).Dispose();
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task ConnectAsyncWrapsRefusedConnection()
        {
            // Grab a port that is guaranteed not to listen.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            await Assert.ThrowsAsync<IOException>(
                () => TcpTransport.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        [Fact]
        public void ConnectSyncHonorsPriorCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => TcpTransport.Connect(
                "127.0.0.1", 1, Timeout.InfiniteTimeSpan, cts.Token));
        }

        [Fact]
        public async Task ConnectAsyncHonorsPriorCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => TcpTransport.ConnectAsync("127.0.0.1", 1, TimeSpan.FromSeconds(5), cts.Token));
        }

        [Fact]
        public async Task ConnectSyncRepeatedSuccessDoesNotPoisonLaterConnections()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var stop = new CancellationTokenSource();
            var serve = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                        // Close only after the byte was consumed; avoid filling Windows TIME_WAIT ports.
                        peer.Client.LingerState = new LingerOption(true, 0);
                        peer.GetStream().WriteByte(42);
                        await peer.GetStream().ReadExactlyAsync(new byte[1], stop.Token);
                        peer.Client.Close(0);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            var workersCompleted = false;
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var workers = new Task[8];
                for (var worker = 0; worker < workers.Length; worker++)
                    workers[worker] = Task.Run(() =>
                    {
                        for (var i = 0; i < 1024; i++)
                        {
                            using var transport = TcpTransport.Connect("127.0.0.1", port, TimeSpan.FromSeconds(5));
                            transport.Stream.ReadTimeout = 5000;
                            Assert.Equal(42, transport.Stream.ReadByte());
                            transport.Stream.WriteByte(1);
                        }
                    });
                await Task.WhenAll(workers);
                workersCompleted = true;
            }
            finally
            {
                stop.Cancel();
                try { await serve; }
                catch (Exception) when (!workersCompleted) { /* Preserve the original client failure. */ }
                finally { listener.Stop(); }
            }
        }

        [Fact]
        public async Task ConnectSyncSucceedsAndRefusedWraps()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var acceptTask = listener.AcceptTcpClientAsync();
                using (var transport = TcpTransport.Connect("127.0.0.1", port, TimeSpan.FromSeconds(5)))
                {
                    Assert.True(transport.Stream.CanWrite);
                }

                (await acceptTask).Dispose();
            }
            finally
            {
                listener.Stop();
            }

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Assert.Throws<IOException>(() => TcpTransport.Connect("127.0.0.1", freePort, TimeSpan.FromSeconds(5)));
        }
    }
}
