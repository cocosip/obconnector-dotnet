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
    public sealed class TlsServerValidationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CustomPolicyReceivesCertificateChainAndPlatformNameErrors(bool asynchronous)
        {
            await using var server = new TlsServer();
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            var calls = 0;
            SetPolicy(connection, (_, certificate, chain, errors) =>
            {
                Interlocked.Increment(ref calls);
                Assert.NotNull(certificate);
                Assert.NotNull(chain);
                Assert.Equal(server.Certificate.Thumbprint, certificate!.GetCertHashString());
                Assert.NotEqual(SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateChainErrors);
                Assert.Equal(SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateNameMismatch);
                return true;
            });
            await Open(connection, asynchronous);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(1, calls);
            Assert.Equal(1, server.Authenticated);
            Assert.Throws<InvalidOperationException>(() => SetPolicy(connection, null));
            await Close(connection, asynchronous);
            SetPolicy(connection, null);
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.IsAssignableFrom<AuthenticationException>(error.InnerException);
            Assert.Equal(1, server.Authenticated);
        }

        [Theory]
        [InlineData(false, "reject")]
        [InlineData(true, "reject")]
        [InlineData(false, "throw")]
        [InlineData(true, "throw")]
        [InlineData(false, "hostname")]
        [InlineData(true, "hostname")]
        public async Task PolicyRejectionOrExceptionNeverSendsAuthenticationOrRetries(bool asynchronous, string kind)
        {
            await using var server = new TlsServer();
            using var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port));
            builder.Server = $"127.0.0.1:{server.Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}";
            if (kind == "hostname") builder.TlsServerName = "wrong-host.invalid";
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            var calls = 0;
            SetPolicy(connection, (_, _, _, errors) =>
            {
                calls++;
                if (kind == "throw") throw new InvalidOperationException("policy rejected");
                if (kind == "hostname") Assert.NotEqual(SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateNameMismatch);
                return false;
            });
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(1, calls);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.Authenticated);
            Assert.False(unused.Pending());
        }

        [Theory]
        [InlineData(false, OceanBaseTlsMode.Required)]
        [InlineData(true, OceanBaseTlsMode.Required)]
        [InlineData(false, OceanBaseTlsMode.Disabled)]
        [InlineData(true, OceanBaseTlsMode.Disabled)]
        public async Task ExplicitPolicyCannotBeSilentlyIgnoredByTlsMode(bool asynchronous, OceanBaseTlsMode mode)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(((IPEndPoint)listener.LocalEndpoint).Port)) { TlsMode = mode };
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            SetPolicy(connection, (_, _, _, _) => true);
            var error = await Assert.ThrowsAsync<NotSupportedException>(() => Open(connection, asynchronous));
            Assert.Contains("VerifyFull", error.Message);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.False(listener.Pending());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EqualDelegatesHaveDifferentPoolsAndReassignmentUsesMatchingPolicy(bool asynchronous)
        {
            await using var server = new TlsServer();
            var policy = new Policy();
            var first = new RemoteCertificateValidationCallback(policy.Validate);
            var equal = new RemoteCertificateValidationCallback(policy.Validate);
            Assert.Equal(first, equal); Assert.NotSame(first, equal);
            var configuration = Configuration(server.Port, pooling: true, minimum: 2);
            using var a = new OceanBaseConnection(configuration);
            using var b = new OceanBaseConnection(configuration);
            SetPolicy(a, first); SetPolicy(b, equal);
            var firstPool = PhysicalSessionPoolRegistry.Get(configuration, null, first);
            var secondPool = PhysicalSessionPoolRegistry.Get(configuration, null, equal);
            try
            {
                await Open(a, asynchronous);
                var original = a.GetOpenSession();
                await Close(a, asynchronous);
                await Open(b, asynchronous);
                var other = b.GetOpenSession();
                Assert.NotSame(original, other);
                await Close(b, asynchronous);
                Assert.Equal(4, policy.Calls);
                SetPolicy(a, equal);
                await Open(a, asynchronous);
                Assert.Same(other, a.GetOpenSession());
                await Close(a, asynchronous);
                SetPolicy(b, null);
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(b, asynchronous));
                Assert.Equal(4, server.Authenticated);
                Assert.Equal(4, policy.Calls);
                SetPolicy(a, first);
                await Open(a, asynchronous);
                Assert.Same(original, a.GetOpenSession());
                await Close(a, asynchronous);
            }
            finally
            {
                a.Close(); b.Close();
                ClearPool(b, configuration);
                firstPool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, firstPool);
                secondPool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, secondPool);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SourcePoliciesAreFixedAndBuildersSnapshotAtBuild(bool asynchronous)
        {
            await using var server = new TlsServer();
            var builder = new OceanBaseDataSourceBuilder(Configuration(server.Port, pooling: true));
            var policy = new Policy();
            var callback = new RemoteCertificateValidationCallback(policy.Validate);
            var method = typeof(OceanBaseDataSourceBuilder).GetMethod("UseRemoteCertificateValidationCallback");
            Assert.NotNull(method);
            Assert.Same(builder, method!.Invoke(builder, new object?[] { callback }));
            using var source = builder.Build();
            method.Invoke(builder, new object?[] { new RemoteCertificateValidationCallback((_, _, _, _) => false) });
            using var rejectedSource = builder.Build();
            using var a = (OceanBaseConnection)source.CreateConnection();
            using var b = (OceanBaseConnection)source.CreateConnection();
            using var rejected = (OceanBaseConnection)rejectedSource.CreateConnection();
            Assert.Same(callback, GetPolicy(a));
            Assert.Throws<InvalidOperationException>(() => SetPolicy(a, callback));
            await Open(a, asynchronous);
            var session = a.GetOpenSession();
            await Close(a, asynchronous);
            await Open(b, asynchronous);
            Assert.Same(session, b.GetOpenSession());
            await Close(b, asynchronous);
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(rejected, asynchronous));
            Assert.Equal(1, policy.Calls);
            Assert.Equal(1, server.Authenticated);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task FailedWarmupRemovesOriginalPolicyPoolAndClosesEarlierSessions(bool asynchronous, bool throws)
        {
            await using var server = new TlsServer();
            var configuration = Configuration(server.Port, pooling: true, minimum: 2);
            using var connection = new OceanBaseConnection(configuration);
            var calls = 0;
            RemoteCertificateValidationCallback callback = (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1) return true;
                if (throws) throw new InvalidOperationException("rejected second warmup");
                return false;
            };
            SetPolicy(connection, callback);
            var original = PhysicalSessionPoolRegistry.Get(configuration, null, callback);
            PhysicalSessionPool? replacement = null;
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Equal(2, calls);
                Assert.Equal(1, server.Authenticated);
                Assert.True(original.IsEmpty);
                replacement = PhysicalSessionPoolRegistry.Get(configuration, null, callback);
                Assert.NotSame(original, replacement);
            }
            finally
            {
                connection.Close(); original.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, original);
                if (replacement != null) { replacement.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, replacement); }
            }
        }

        [Theory]
        [InlineData(OceanBaseTlsMode.Disabled)]
        [InlineData(OceanBaseTlsMode.Required)]
        public void SourceBuildRejectsIgnoredValidationPolicy(OceanBaseTlsMode mode)
        {
            var builder = new OceanBaseDataSourceBuilder("Server=127.0.0.1;User ID=u")
                .UseRemoteCertificateValidationCallback((_, _, _, _) => true);
            builder.ConnectionStringBuilder.TlsMode = mode;
            Assert.Throws<NotSupportedException>(builder.Build);
        }

        [Fact]
        public void PolicyCannotBeChangedAfterDisposal()
        {
            var connection = new OceanBaseConnection();
            SetPolicy(connection, null);
            connection.Dispose();
            Assert.Throws<ObjectDisposedException>(() => SetPolicy(connection, null));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task OpeningPolicyIsFrozenAndCanceledValidationCannotPublishSession(bool asynchronous, bool callerCancel)
        {
            await using var server = new TlsServer();
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var caller = new CancellationTokenSource();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPolicy(connection, (_, _, _, _) => { entered.TrySetResult(); return release.Wait(TimeSpan.FromSeconds(5)); });
            var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ConnectionState.Connecting, connection.State);
                Assert.Throws<InvalidOperationException>(() => SetPolicy(connection, null));
                if (callerCancel) caller.Cancel(); else await Close(connection, asynchronous);
            }
            finally { release.Set(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.Authenticated);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClosedCallbackPolicyChangeKeepsDetachedSessionInOriginalPool(bool asynchronous)
        {
            await using var server = new TlsServer();
            var configuration = Configuration(server.Port, pooling: true);
            var policy = new Policy();
            var original = new RemoteCertificateValidationCallback(policy.Validate);
            var replacement = new RemoteCertificateValidationCallback(policy.Validate);
            using var connection = new OceanBaseConnection(configuration);
            using var next = new OceanBaseConnection(configuration);
            SetPolicy(connection, original); SetPolicy(next, original);
            var firstPool = PhysicalSessionPoolRegistry.Get(configuration, null, original);
            var secondPool = PhysicalSessionPoolRegistry.Get(configuration, null, replacement);
            try
            {
                await Open(connection, asynchronous);
                var oldSession = connection.GetOpenSession();
                StateChangeEventHandler? reenter = null;
                reenter = (_, args) =>
                {
                    if (args.CurrentState != ConnectionState.Closed) return;
                    connection.StateChange -= reenter;
                    SetPolicy(connection, replacement);
                    connection.Open();
                };
                connection.StateChange += reenter;
                await Close(connection, asynchronous);
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.NotSame(oldSession, connection.GetOpenSession());
                await Open(next, asynchronous);
                Assert.Same(oldSession, next.GetOpenSession());
                await Close(next, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(2, policy.Calls);
            }
            finally
            {
                connection.Close(); next.Close(); firstPool.Dispose(); secondPool.Dispose();
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, firstPool);
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, secondPool);
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

        private static RemoteCertificateValidationCallback? GetPolicy(OceanBaseConnection connection) =>
            (RemoteCertificateValidationCallback?)typeof(OceanBaseConnection).GetProperty("RemoteCertificateValidationCallback")!.GetValue(connection);

        private static void SetPolicy(OceanBaseConnection connection, RemoteCertificateValidationCallback? callback)
        {
            var property = typeof(OceanBaseConnection).GetProperty("RemoteCertificateValidationCallback");
            Assert.NotNull(property);
            Assert.Equal(typeof(RemoteCertificateValidationCallback), property!.PropertyType);
            try { property.SetValue(connection, callback); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }

        private static void ClearPool(OceanBaseConnection connection, string configuration)
        {
            var pool = (PhysicalSessionPool?)typeof(OceanBaseConnection).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection);
            if (pool == null) return;
            pool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool);
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
    }
}
