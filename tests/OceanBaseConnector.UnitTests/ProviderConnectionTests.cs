using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ProviderConnectionTests
    {
        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;Password=hidden-secret;TLS Mode=Disabled;Connection Timeout=5";

        private static LoopbackServer Server(Task? beforeOk = null, TaskCompletionSource? authenticated = null)
        {
            return LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                authenticated?.TrySetResult();
                if (beforeOk != null) await beforeOk;
                try
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                    await ServerIdentity.ReplyAsync(channel);
                }
                catch (System.IO.IOException) when (beforeOk != null) { return; }
                catch (ProtocolException) when (beforeOk != null) { return; }
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
        }

        [Fact]
        public void ClientCertificateWithDisabledTlsIsRejectedBeforeNetwork()
        {
            using var connection = new OceanBaseConnection(
                "Server=127.0.0.1;Port=1;User ID=u;Password=p;TLS Mode=Disabled;Pooling=true");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            connection.ClientCertificate = certificate;

            var error = Assert.Throws<NotSupportedException>(() => connection.Open());
            Assert.Contains("TLS Mode=Disabled", error.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PublicConnectionUsesAuthenticatedSessionAndReopensWithNewSession(bool asynchronous)
        {
            var first = Server();
            var second = Server();
            var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(first.Port), log);
            var states = new List<ConnectionState>();
            connection.StateChange += (_, args) => states.Add(args.CurrentState);
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, connection.CompatibilityMode);
                Assert.NotEmpty(connection.ServerVersion);
                Assert.DoesNotContain("hidden-secret", connection.ConnectionString);
                Assert.Throws<InvalidOperationException>(() => connection.Open());
                Assert.Throws<InvalidOperationException>(() => connection.ConnectionString = Configuration(second.Port));
                await connection.CloseAsync();
                Assert.Null(connection.CompatibilityMode);
                Assert.DoesNotContain("hidden-secret", connection.ConnectionString);
                connection.ConnectionString = Configuration(second.Port);
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                await connection.DisposeAsync();
                Assert.Equal(new[] { ConnectionState.Open, ConnectionState.Closed, ConnectionState.Open, ConnectionState.Closed }, states);
                Assert.Throws<ObjectDisposedException>(() => connection.Open());
                Assert.Equal(0, log.DisposeCount);
                Assert.Equal(2, log.Entries.Count(e => e.EventId.Id == 1001));
                Assert.All(log.Entries, e => Assert.DoesNotContain("hidden-secret", e.Message));
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task SourceDisposalKeepsActiveConnectionButRejectsReopen()
        {
            var server = Server();
            var log = new RecordingLoggerFactory();
            await using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseLoggerFactory(log).Build();
            await using var connection = await source.OpenConnectionAsync();
            try
            {
                await source.DisposeAsync();
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, ((OceanBaseConnection)connection).CompatibilityMode);
                await connection.CloseAsync();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.OpenAsync());
                Assert.Equal(0, log.DisposeCount);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DisposalWhileOpeningDoesNotPublishOrLeakSession(bool disposeSource)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(release.Task, ready);
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).Build();
            using var connection = source.CreateConnection();
            try
            {
                var opening = connection.OpenAsync();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(ConnectionState.Connecting, connection.State);
                if (disposeSource) source.Dispose(); else connection.Close();
                release.TrySetResult();
                if (disposeSource) await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task CloseCancelsOpenWhileWaitingForPooledSession()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel);
                await client.GetStream().ReadUpToAsync(new byte[1], 1);
            });
            var configuration = Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=30";
            using var first = new OceanBaseConnection(configuration);
            using var second = new OceanBaseConnection(configuration);
            try
            {
                await first.OpenAsync();
                var opening = second.OpenAsync();
                await Task.Delay(100);
                second.Close();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                Assert.Equal(ConnectionState.Closed, second.State);
            }
            finally { second.Close(); first.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task BrokenLoggerDoesNotChangeConnectionOutcome(int failure)
        {
            var server = Server();
            var log = new RecordingLoggerFactory { ThrowOnCreate = failure == 0, ThrowOnIsEnabled = failure == 1, ThrowOnLog = failure == 2 };
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try { await connection.OpenAsync(); Assert.Equal(ConnectionState.Open, connection.State); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, log.DisposeCount);
        }

        [Fact]
        public async Task MaliciousServerErrorIsNotForwardedToLogBackend()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                var error = new byte[] { 255, 21, 4, (byte)'#', (byte)'2', (byte)'8', (byte)'0', (byte)'0', (byte)'0' }
                    .Concat(System.Text.Encoding.UTF8.GetBytes("hidden-secret SELECT sensitive")).ToArray();
                await channel.WritePacketAsync(error);
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
            var log = new RecordingLoggerFactory();
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseLoggerFactory(log).Build();
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(async () => await source.OpenConnectionAsync());
                Assert.Contains(log.Entries, e => e.EventId.Id == 1002 && e.Message.Contains("1045"));
                Assert.All(log.Entries, e => { Assert.Null(e.Error); Assert.DoesNotContain("hidden-secret", e.Message); Assert.DoesNotContain("SELECT sensitive", e.Message); });
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task OpenedLoggerClosingConnectionPreservesStateEventOrder()
        {
            var server = Server();
            var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            var states = new List<(ConnectionState EventState, ConnectionState ActualState)>();
            connection.StateChange += (_, args) => states.Add((args.CurrentState, connection.State));
            log.OnLog = id => { if (id.Id == 1001) connection.Close(); };
            try
            {
                await connection.OpenAsync();
                Assert.Equal(new[] { (ConnectionState.Open, ConnectionState.Open), (ConnectionState.Closed, ConnectionState.Closed) }, states);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ClosedLoggerReopeningConnectionPreservesStateEventOrder()
        {
            var first = Server();
            var second = Server();
            var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(first.Port), log);
            var states = new List<(ConnectionState, ConnectionState)>();
            connection.StateChange += (_, args) => states.Add((args.CurrentState, connection.State));
            log.OnLog = id =>
            {
                if (id.Id != 1003) return;
                log.OnLog = null;
                connection.ConnectionString = Configuration(second.Port);
                connection.Open();
            };
            try
            {
                await connection.OpenAsync();
                connection.Close();
                Assert.Equal(new[] { (ConnectionState.Open, ConnectionState.Open),
                    (ConnectionState.Closed, ConnectionState.Closed), (ConnectionState.Open, ConnectionState.Open) }, states);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { log.OnLog = null; connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void LoggerCallbackClosingConnectionCannotResurrectOrCorruptOpening()
        {
            var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection("Server=unreachable.invalid;User ID=u;Password=hidden-secret", log);
            var duringCallback = ConnectionState.Closed;
            log.OnLog = id =>
            {
                if (id.Id != 1000) return;
                duringCallback = connection.State;
                connection.Close();
                connection.ConnectionString = string.Empty;
            };
            Assert.ThrowsAny<OperationCanceledException>(() => connection.Open());
            Assert.Equal(ConnectionState.Connecting, duringCallback);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.All(log.Entries, e => Assert.DoesNotContain("hidden-secret", e.Message));
        }

        [Fact]
        public async Task ConcurrentDataSourceOpensOwnIndependentSessions()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            async Task ServeOne()
            {
                using var client = await listener.AcceptTcpClientAsync();
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel);
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            }
            var serverTasks = Task.WhenAll(ServeOne(), ServeOne());
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            using var source = new OceanBaseDataSourceBuilder(Configuration(port)).Build();
            using var first = source.CreateConnection();
            using var second = source.CreateConnection();
            try
            {
                await Task.WhenAll(first.OpenAsync(), second.OpenAsync());
                Assert.NotSame(first, second);
                first.Close();
                Assert.Equal(ConnectionState.Open, second.State);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, ((OceanBaseConnection)second).CompatibilityMode);
            }
            finally { first.Close(); second.Close(); listener.Stop(); }
            await serverTasks.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PublicCancellationWithBrokenLoggerClosesSocketAndPreservesCancellation()
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                ready.TrySetResult();
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
            var log = new RecordingLoggerFactory { ThrowOnLog = true };
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseLoggerFactory(log).Build();
            using var connection = source.CreateConnection();
            using var cancellation = new CancellationTokenSource();
            try
            {
                var opening = connection.OpenAsync(cancellation.Token);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                Assert.Equal(cancellation.Token, error.CancellationToken);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PersistSecurityInfoIsExplicitForBothConnectionEntries(bool fromSource)
        {
            var server = Server();
            var configuration = Configuration(server.Port) + ";Persist Security Info=true";
            using var source = new OceanBaseDataSourceBuilder(configuration).Build();
            using var connection = fromSource ? source.CreateConnection() : new OceanBaseConnection(configuration);
            try
            {
                Assert.Contains("hidden-secret", source.ConnectionString);
                await connection.OpenAsync();
                Assert.Contains("hidden-secret", connection.ConnectionString);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task AmbientTransactionDoesNotRejectBeforeConnectionAttempt()
        {
            using var connection = new OceanBaseConnection("Server=unreachable.invalid;User ID=u");
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            Assert.Throws<OceanBaseException>(() => connection.Open());
            await Assert.ThrowsAsync<OceanBaseException>(() => connection.OpenAsync());
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
    }
}
