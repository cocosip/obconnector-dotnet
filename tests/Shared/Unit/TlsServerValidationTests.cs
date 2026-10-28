using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TlsServerValidationTests
    {

        private static void SetPolicy(OceanBaseConnection connection, RemoteCertificateValidationCallback? callback)
        {
            var property = typeof(OceanBaseConnection).GetProperty("RemoteCertificateValidationCallback");
            Assert.NotNull(property);
            Assert.Equal(typeof(RemoteCertificateValidationCallback), property!.PropertyType);
            try { property.SetValue(connection, callback); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }

        private static Task Open(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
        private static string Configuration(int port, bool pooling = false, int minimum = 0) => new OceanBaseConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = port,
            UserId = "u",
            Password = "hidden-secret",
            TlsServerName = "ob-loopback",
            Pooling = pooling,
            MinPoolSize = minimum,
            MaxPoolSize = Math.Max(1, minimum),
            ConnectionTimeout = 5,
        }.ConnectionString;

        private sealed class TlsServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            private readonly ConcurrentBag<Task> _clients = new ConcurrentBag<Task>();
            private readonly Task _accepting;
            private int _authenticated;
            internal TlsServer() { Certificate = TestCertificateFactory.CreateSelfSigned(); _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _accepting = Accept(); }
            internal X509Certificate2 Certificate { get; }
            internal int Port { get; }
            internal int Authenticated => Volatile.Read(ref _authenticated);
            private async Task Accept()
            {
                try { while (true) _clients.Add(Serve(await _listener.AcceptTcpClientAsync(_stop.Token))); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            private async Task Serve(TcpClient peer)
            {
                using (peer)
                using (var tls = new SslStream(peer.GetStream()))
                {
                    var channel = new PacketChannel(peer.GetStream());
                    try
                    {
                        await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, _stop.Token);
                        Assert.Equal(32, (await channel.ReadPacketAsync(_stop.Token)).Length);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, _stop.Token);
                        channel.ReplaceUnderlyingStream(tls);
                        await channel.ReadPacketAsync(_stop.Token);
                        Interlocked.Increment(ref _authenticated);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, _stop.Token);
                        await ServerIdentity.ReplyAsync(channel, token: _stop.Token);
                        while (true)
                        {
                            var request = await channel.ReadPacketAsync(_stop.Token);
                            Assert.Single(request); Assert.True(request[0] == 0x1f || request[0] == 0x0e);
                            await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, _stop.Token);
                            if (request[0] == 0x1f) await ServerIdentity.ReplyOracleAutocommitAsync(channel, _stop.Token);
                            channel.BeginCommand();
                        }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                    catch (AuthenticationException) { }
                    catch (IOException) { }
                    catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                }
            }
            public async ValueTask DisposeAsync()
            {
                _stop.Cancel(); await _accepting; _listener.Stop();
                await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(5));
                Certificate.Dispose(); _stop.Dispose();
            }
        }

        private sealed class Policy
        {
            internal int Calls;
            internal bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
            {
                Interlocked.Increment(ref Calls);
                return certificate != null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0;
            }
        }

        private static void ClearPool(OceanBaseConnection connection, string configuration)
        {
            var pool = (PhysicalSessionPool?)typeof(OceanBaseConnection).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection);
            if (pool == null) return;
            pool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool);
        }

        private static RemoteCertificateValidationCallback? GetPolicy(OceanBaseConnection connection) =>
            (RemoteCertificateValidationCallback?)typeof(OceanBaseConnection).GetProperty("RemoteCertificateValidationCallback")!.GetValue(connection);
    }
}
