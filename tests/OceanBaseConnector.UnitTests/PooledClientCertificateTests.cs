using System;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PooledClientCertificateTests
    {
        private static readonly RemoteCertificateValidationCallback ValidateServer = (_, certificate, _, errors) =>
            certificate != null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0;
        private static MutualTlsServer Server() => new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
            channel => TextCommandTests.ReadClientCommand(channel));

        [Theory]
        [InlineData(false, 0, false)]
        [InlineData(true, 0, false)]
        [InlineData(false, 2, false)]
        [InlineData(true, 2, false)]
        [InlineData(false, 0, true)]
        [InlineData(true, 0, true)]
        public async Task OrdinaryPoolsIsolateCertificatesAndNullIdentity(bool asynchronous, int minimum, bool equal)
        {
            await using var server = Server();
            using var first = TestCertificateFactory.CreateSelfSigned("client-a");
            using var second = equal ? new X509Certificate2(first) : TestCertificateFactory.CreateSelfSigned("client-b");
            if (equal) { Assert.Equal(first, second); Assert.NotSame(first, second); }
            var configuration = Configuration(server.Port, minimum: minimum);
            using var a = Connection(configuration, first);
            using var b = Connection(configuration, second);
            using var none = Connection(configuration, null);
            PhysicalSessionPool? oldPool = null, otherPool = null;
            try
            {
                await Open(a, asynchronous); var original = a.GetOpenSession(); oldPool = Pool(a);
                Assert.Throws<InvalidOperationException>(() => a.ClientCertificate = null);
                await Close(a, asynchronous);
                await Open(b, asynchronous); var other = b.GetOpenSession(); otherPool = Pool(b);
                Assert.NotSame(original, other);
                await Close(b, asynchronous);
                await Open(a, asynchronous); Assert.Same(original, a.GetOpenSession()); await Close(a, asynchronous);
                a.ClientCertificate = second;
                await Open(a, asynchronous); Assert.Same(other, a.GetOpenSession()); await Close(a, asynchronous);
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(none, asynchronous));
                Assert.Equal(minimum == 0 ? 2 : 4, server.AuthenticatedCertificates.Count);
                Assert.Contains(first.Thumbprint, server.AuthenticatedCertificates);
                Assert.Contains(second.Thumbprint, server.AuthenticatedCertificates);
            }
            finally
            {
                a.Close(); b.Close(); none.Close();
                Clear(configuration, oldPool); Clear(configuration, otherPool); Clear(configuration, Pool(none));
            }
            Assert.NotNull(oldPool!.TlsClientCertificate);
            Assert.ThrowsAny<CryptographicException>(() => oldPool.TlsClientCertificate!.GetCertHashString());
            Assert.True(first.HasPrivateKey); Assert.True(second.HasPrivateKey);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SourceSnapshotsCertificateAndDoesNotOwnCallerObject(bool asynchronous, bool pooling)
        {
            await using var server = Server();
            using var first = TestCertificateFactory.CreateSelfSigned("client-a");
            using var second = TestCertificateFactory.CreateSelfSigned("client-b");
            var firstHash = first.Thumbprint;
            var builder = new OceanBaseDataSourceBuilder(Configuration(server.Port, pooling: pooling)).UseRemoteCertificateValidationCallback(ValidateServer);
            var use = typeof(OceanBaseDataSourceBuilder).GetMethod("UseClientCertificate"); Assert.NotNull(use);
            Assert.Same(builder, use!.Invoke(builder, new object?[] { first }));
            using var originalSource = builder.Build();
            var ownedCopy = originalSource.Pool.TlsClientCertificate;
            Assert.NotSame(first, ownedCopy);
            use.Invoke(builder, new object?[] { second });
            using var replacementSource = builder.Build();
            first.Dispose();
            using var a = (OceanBaseConnection)originalSource.CreateConnection();
            using var b = (OceanBaseConnection)replacementSource.CreateConnection();
            Assert.Same(first, a.ClientCertificate);
            Assert.Throws<InvalidOperationException>(() => a.ClientCertificate = second);
            await Open(a, asynchronous); await Open(b, asynchronous);
            originalSource.Dispose();
            Assert.ThrowsAny<CryptographicException>(() => ownedCopy!.GetCertHashString());
            using (var command = a.CreateCommand()) { command.CommandText = "selected"; Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); }
            await Close(a, asynchronous); await Close(b, asynchronous);
            replacementSource.Dispose();
            Assert.Contains(firstHash, server.AuthenticatedCertificates);
            Assert.Contains(second.Thumbprint, server.AuthenticatedCertificates);
            Assert.True(second.HasPrivateKey);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SourceBuildRejectsInvalidClientCertificate(bool disabled)
        {
            using var full = TestCertificateFactory.CreateSelfSigned("client");
            using var publicOnly = X509CertificateLoader.LoadCertificate(full.RawData);
            var builder = new OceanBaseDataSourceBuilder("Server=127.0.0.1;User ID=u");
            var use = typeof(OceanBaseDataSourceBuilder).GetMethod("UseClientCertificate")!;
            use.Invoke(builder, new object?[] { disabled ? full : publicOnly });
            if (disabled)
            {
                builder.ConnectionStringBuilder.TlsMode = OceanBaseTlsMode.Disabled;
                Assert.Throws<NotSupportedException>(builder.Build);
            }
            else Assert.Throws<ArgumentException>(builder.Build);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task InvalidCertificateConfigurationFailsBeforeNetwork(bool asynchronous, bool disabled)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var full = TestCertificateFactory.CreateSelfSigned("client");
            using var publicOnly = X509CertificateLoader.LoadCertificate(full.RawData);
            var builder = new OceanBaseConnectionStringBuilder(Configuration(((IPEndPoint)listener.LocalEndpoint).Port));
            if (disabled) builder.TlsMode = OceanBaseTlsMode.Disabled;
            using var connection = new OceanBaseConnection(builder.ConnectionString) { ClientCertificate = disabled ? full : publicOnly };
            if (disabled) await Assert.ThrowsAsync<NotSupportedException>(() => Open(connection, asynchronous));
            else await Assert.ThrowsAsync<ArgumentException>(() => Open(connection, asynchronous));
            Assert.Equal(ConnectionState.Closed, connection.State); Assert.False(listener.Pending());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ResetCallerCertificateCannotBorrowAuthenticatedIdleSession(bool asynchronous)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = Configuration(server.Port);
            using var connection = Connection(configuration, certificate);
            PhysicalSessionPool? pool = null;
            try
            {
                await Open(connection, asynchronous); pool = Pool(connection); await Close(connection, asynchronous);
                certificate.Reset();
                await Assert.ThrowsAnyAsync<CryptographicException>(() => Open(connection, asynchronous));
                Assert.Equal(1, server.Accepted);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); Clear(configuration, pool); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedCertificateWarmupRollsBackAndRemovesItsPool(bool asynchronous)
        {
            await using var server = Server();
            var validations = 0;
            server.AcceptClientCertificate = _ => Interlocked.Increment(ref validations) == 1;
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = Configuration(server.Port, minimum: 2);
            using var connection = Connection(configuration, certificate);
            var pool = PhysicalSessionPoolRegistry.Get(configuration, null, ValidateServer, certificate);
            PhysicalSessionPool? next = null;
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.True(pool.IsEmpty);
                Assert.Single(server.AuthenticatedCertificates);
                var get = typeof(PhysicalSessionPoolRegistry).GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!;
                Assert.Equal(4, get.GetParameters().Length);
                next = (PhysicalSessionPool)get.Invoke(null, new object?[] { configuration, null, ValidateServer, certificate })!;
                Assert.NotSame(pool, next);
            }
            finally { connection.Close(); Clear(configuration, pool); Clear(configuration, next); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClosedCallbackCertificateChangeReturnsSessionToOriginalIdentity(bool asynchronous)
        {
            await using var server = Server();
            using var first = TestCertificateFactory.CreateSelfSigned("client-a");
            using var second = TestCertificateFactory.CreateSelfSigned("client-b");
            var configuration = Configuration(server.Port);
            using var connection = Connection(configuration, first);
            using var next = Connection(configuration, first);
            PhysicalSessionPool? originalPool = null, replacementPool = null;
            try
            {
                await Open(connection, asynchronous); var old = connection.GetOpenSession(); originalPool = Pool(connection);
                StateChangeEventHandler? reopen = null;
                reopen = (_, args) =>
                {
                    if (args.CurrentState != ConnectionState.Closed) return;
                    connection.StateChange -= reopen; connection.ClientCertificate = second; connection.Open();
                };
                connection.StateChange += reopen;
                await Close(connection, asynchronous); replacementPool = Pool(connection);
                Assert.NotSame(old, connection.GetOpenSession());
                await Open(next, asynchronous); Assert.Same(old, next.GetOpenSession());
                await Close(next, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(2, server.AuthenticatedCertificates.Count);
            }
            finally { connection.Close(); next.Close(); Clear(configuration, originalPool); Clear(configuration, replacementPool); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SourceDisposalDuringTlsValidationCannotPublishOrRetainSession(bool asynchronous, bool pooling)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = new OceanBaseDataSourceBuilder(Configuration(server.Port, pooling))
                .UseClientCertificate(certificate)
                .UseRemoteCertificateValidationCallback((_, _, _, _) => { entered.TrySetResult(); return release.Wait(TimeSpan.FromSeconds(5)); });
            using var source = builder.Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            var opening = Open(connection, asynchronous);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                source.Dispose();
            }
            finally { release.Set(); }
            Assert.NotNull(await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.True(source.Pool.IsEmpty);
            Assert.True(certificate.HasPrivateKey);
        }

        private static OceanBaseConnection Connection(string configuration, X509Certificate2? certificate) =>
            new OceanBaseConnection(configuration) { ClientCertificate = certificate, RemoteCertificateValidationCallback = ValidateServer };
        private static string Configuration(int port, bool pooling = true, int minimum = 0) => new OceanBaseConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = port,
            UserId = "u",
            TlsServerName = "ob-loopback",
            ConnectionTimeout = 5,
            Pooling = pooling,
            MinPoolSize = minimum,
            MaxPoolSize = Math.Max(1, minimum),
        }.ConnectionString;
        private static Task Open(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
        private static PhysicalSessionPool? Pool(OceanBaseConnection connection) =>
            (PhysicalSessionPool?)typeof(OceanBaseConnection).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection);
        private static void Clear(string configuration, PhysicalSessionPool? pool)
        { if (pool != null) { pool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool); } }
    }
}
