using System;
using System.Data;
using System.Diagnostics.Metrics;
using System.Net.Security;
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
    [Collection("Driver diagnostics")]
    public sealed class PoolClearingTests
    {
        private static readonly RemoteCertificateValidationCallback Validate = (_, _, _, _) => true;
        private static void Clear(OceanBaseConnection connection)
        {
            var method = typeof(OceanBaseConnection).GetMethod("ClearPool", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            try { method!.Invoke(null, new object?[] { connection }); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); }
        }
        private static void ClearAll()
        {
            var method = typeof(OceanBaseConnection).GetMethod("ClearAllPools", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method); method!.Invoke(null, null);
        }
        private static void ClearSource(OceanBaseDataSource source)
        {
            var method = typeof(OceanBaseDataSource).GetMethod("Clear");
            Assert.NotNull(method); method!.Invoke(source, null);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task IdleClearClosesSessionAndKeepsSourceUsable(bool asynchronous, bool dataSource)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = Configuration(server.Port);
            using var source = dataSource ? Source(configuration, certificate) : null;
            using var connection = Connection(configuration, certificate, source);
            using var unopened = Connection(configuration, certificate, source);
            if (dataSource) certificate.Dispose();
            PhysicalSessionPool? pool = null;
            try
            {
                await Open(connection, asynchronous); pool = Pool(connection);
                var original = connection.GetOpenSession();
                await Close(connection, asynchronous);
                Clear(unopened);
                Assert.Equal(SessionState.Closed, original.State);
                await Open(connection, asynchronous);
                Assert.NotSame(original, connection.GetOpenSession());
                await Query(connection); await Close(connection, asynchronous);
                Assert.Equal(2, server.Accepted);
            }
            finally { connection.Close(); Cleanup(configuration, pool); Cleanup(configuration, Pool(connection)); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BorrowedClearPreservesCommandsAndWakesCapacityWaiter(bool asynchronous, bool dataSource)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = Configuration(server.Port);
            using var source = dataSource ? Source(configuration, certificate) : null;
            using var first = Connection(configuration, certificate, source);
            using var second = Connection(configuration, certificate, source);
            PhysicalSessionPool? pool = null;
            try
            {
                await Open(first, asynchronous); pool = Pool(first);
                var original = first.GetOpenSession();
                var waiting = Open(second, asynchronous);
                await WaitForWaiter(pool!);
                if (source == null) Clear(first); else ClearSource(source);
                Assert.False(waiting.IsCompleted);
                await Query(first);
                await Close(first, asynchronous);
                await waiting.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(SessionState.Closed, original.State);
                Assert.Equal(0, server.Resets);
                await Query(second); await Close(second, asynchronous);
                Assert.Equal(2, server.Accepted);
                Assert.Equal(1, server.Resets);
            }
            finally { first.Close(); second.Close(); Cleanup(configuration, pool); Cleanup(configuration, Pool(second)); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ClearDuringResetCannotRepublishOldSession(bool asynchronous, bool dataSource)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = Configuration(server.Port);
            using var source = dataSource ? Source(configuration, certificate) : null;
            using var connection = Connection(configuration, certificate, source);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeCommandReply = async command => { if (command == 0x1f) { entered.TrySetResult(); await release.Task; } };
            PhysicalSessionPool? pool = null;
            try
            {
                await Open(connection, asynchronous); pool = Pool(connection);
                var original = connection.GetOpenSession();
                var closing = Close(connection, asynchronous);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Clear(connection);
                }
                finally { release.TrySetResult(); }
                await closing.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(SessionState.Closed, original.State);
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(2, server.Accepted);
            }
            finally { release.TrySetResult(); connection.Close(); Cleanup(configuration, pool); Cleanup(configuration, Pool(connection)); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClearDuringWarmupCannotPublishPreClearIdleSession(bool asynchronous)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var validations = 0;
            RemoteCertificateValidationCallback validation = (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref validations) == 2) { entered.TrySetResult(); return release.Wait(TimeSpan.FromSeconds(5)); }
                return true;
            };
            var configuration = Configuration(server.Port, minimum: 2);
            using var connection = Connection(configuration, certificate);
            connection.RemoteCertificateValidationCallback = validation;
            PhysicalSessionPool? pool = null;
            var opening = Open(connection, asynchronous);
            try
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    pool = Pool(connection); Clear(connection);
                }
                finally { release.Set(); }
                await opening.WaitAsync(TimeSpan.FromSeconds(5));
                await Query(connection); await Close(connection, asynchronous);
                Assert.Equal(4, server.Accepted);
                Assert.True(pool!.TryRent(out var warmed));
                Assert.Equal(SessionState.Open, warmed!.State); pool.Discard(warmed);
            }
            finally { release.Set(); connection.Close(); Cleanup(configuration, pool); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClearAllRetiresOrdinaryPoolsAndLeavesPrivateSources(bool asynchronous)
        {
            await using var server = Server();
            using var first = TestCertificateFactory.CreateSelfSigned("client-a");
            using var second = TestCertificateFactory.CreateSelfSigned("client-b");
            var configuration = Configuration(server.Port);
            using var source = Source(configuration, first);
            using var a = Connection(configuration, first);
            using var b = Connection(configuration, second);
            using var own = Connection(configuration, first, source);
            PhysicalSessionPool? aPool = null, bPool = null;
            try
            {
                await Open(a, asynchronous); aPool = Pool(a); var aSession = a.GetOpenSession(); await Close(a, asynchronous);
                await Open(b, asynchronous); bPool = Pool(b); var bSession = b.GetOpenSession(); await Close(b, asynchronous);
                await Open(own, asynchronous); var privateSession = own.GetOpenSession(); await Close(own, asynchronous);
                ClearAll();
                Assert.Equal(SessionState.Closed, aSession.State); Assert.Equal(SessionState.Closed, bSession.State);
                Assert.Equal(SessionState.Open, privateSession.State);
                Assert.ThrowsAny<CryptographicException>(() => aPool!.TlsClientCertificate!.GetCertHashString());
                Assert.True(source.Pool.TlsClientCertificate!.HasPrivateKey); Assert.True(first.HasPrivateKey);
                await Open(own, asynchronous); Assert.Same(privateSession, own.GetOpenSession()); await Close(own, asynchronous);
                ClearSource(source); Assert.Equal(SessionState.Closed, privateSession.State);
                await Open(a, asynchronous); await Close(a, asynchronous); Assert.Equal(4, server.Accepted);
            }
            finally { a.Close(); b.Close(); own.Close(); Cleanup(configuration, aPool); Cleanup(configuration, bPool); Cleanup(configuration, Pool(a)); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClearMatchesReferenceIdentityWithoutReadingResetCertificate(bool reset)
        {
            await using var server = Server();
            using var first = TestCertificateFactory.CreateSelfSigned("client");
            using var second = new X509Certificate2(first);
            var configuration = Configuration(server.Port);
            using var a = Connection(configuration, first);
            using var b = Connection(configuration, second);
            PhysicalSessionPool? aPool = null, bPool = null;
            try
            {
                await a.OpenAsync(); aPool = Pool(a); var original = a.GetOpenSession(); await a.CloseAsync();
                await b.OpenAsync(); bPool = Pool(b); var other = b.GetOpenSession(); await b.CloseAsync();
                if (reset) first.Reset();
                Clear(a);
                Assert.Equal(SessionState.Closed, original.State); Assert.Equal(SessionState.Open, other.State);
            }
            finally { a.Close(); b.Close(); Cleanup(configuration, aPool); Cleanup(configuration, bPool); }
        }

        [Fact]
        public void ClearRejectsInvalidObjectsWithoutCreatingPoolsOrReadingCertificates()
        {
            Assert.Throws<ArgumentNullException>(() => Clear(null!));
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            certificate.Dispose();
            using var connection = Connection(Configuration(1), certificate);
            var registry = typeof(PhysicalSessionPoolRegistry).GetField("Pools", BindingFlags.NonPublic | BindingFlags.Static)!;
            var pools = (System.Collections.IDictionary)registry.GetValue(null)!;
            var count = pools.Count;
            Clear(connection); Assert.Equal(count, pools.Count);
            connection.Dispose(); Assert.Throws<ObjectDisposedException>(() => Clear(connection));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CanceledStaleWarmupReleasesCapacityForExistingWaiter(bool asynchronous)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rewaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var validations = 0;
            var waits = 0;
            var cancelOnDiscard = 0;
            var configuration = Configuration(server.Port, minimum: 1);
            using var source = new OceanBaseDataSourceBuilder(configuration).UseClientCertificate(certificate)
                .UseRemoteCertificateValidationCallback((_, _, _, _) =>
                {
                    if (Interlocked.Increment(ref validations) == 1) { entered.TrySetResult(); return release.Wait(TimeSpan.FromSeconds(5)); }
                    return true;
                }).Build();
            using var first = (OceanBaseConnection)source.CreateConnection();
            using var second = (OceanBaseConnection)source.CreateConnection();
            using var listener = Metrics((name, value) =>
            {
                if (name == "oceanbase.client.pool.waiters" && value > 0 && Interlocked.Increment(ref waits) == 2) rewaiting.TrySetResult();
                if (name == "oceanbase.client.pool.discarded" && Interlocked.Exchange(ref cancelOnDiscard, 0) == 1) first.Close();
            });
            var opening = Open(first, asynchronous);
            Task? waiting = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                waiting = Open(second, asynchronous);
                await WaitForWaiter(source.Pool);
                source.Clear();
                await rewaiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Interlocked.Exchange(ref cancelOnDiscard, 1);
                release.Set();
                Assert.NotNull(await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(3))));
                await waiting.WaitAsync(TimeSpan.FromSeconds(3));
                await Query(second);
            }
            finally
            {
                release.Set(); first.Close(); second.Close();
                await Record.ExceptionAsync(() => opening);
                if (waiting != null) await Record.ExceptionAsync(() => waiting);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedPrivateWarmupThenClearKeepsSessionMetricsBalanced(bool asynchronous)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var accepted = 0;
            server.AcceptClientCertificate = _ => Interlocked.Increment(ref accepted) == 1;
            long sessions = 0, discards = 0;
            using var listener = Metrics((name, value) =>
            {
                if (name == "oceanbase.client.pool.sessions") Interlocked.Add(ref sessions, value);
                if (name == "oceanbase.client.pool.discarded") Interlocked.Add(ref discards, value);
            });
            using var source = Source(Configuration(server.Port, minimum: 2), certificate);
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(0, Interlocked.Read(ref sessions));
            var removed = Interlocked.Read(ref discards);
            source.Clear(); source.Dispose();
            Assert.Equal(0, Interlocked.Read(ref sessions));
            Assert.Equal(removed, Interlocked.Read(ref discards));
        }

        private static MeterListener Metrics(Action<string, long> measurement)
        {
            var listener = new MeterListener { InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument); } };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurement(instrument.Name, value));
            listener.Start(); return listener;
        }

        private static OceanBaseDataSource Source(string configuration, X509Certificate2 certificate) =>
            new OceanBaseDataSourceBuilder(configuration).UseClientCertificate(certificate).UseRemoteCertificateValidationCallback(Validate).Build();
        private static OceanBaseConnection Connection(string configuration, X509Certificate2 certificate, OceanBaseDataSource? source = null) =>
            source == null ? new OceanBaseConnection(configuration) { ClientCertificate = certificate, RemoteCertificateValidationCallback = Validate } : (OceanBaseConnection)source.CreateConnection();
        private static MutualTlsServer Server() => new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, channel => TextCommandTests.ReadClientCommand(channel));
        private static string Configuration(int port, int minimum = 0) => new OceanBaseConnectionStringBuilder
        { Server = "127.0.0.1", Port = port, UserId = "u", TlsServerName = "ob-loopback", Pooling = true, MaxPoolSize = Math.Max(1, minimum), MinPoolSize = minimum, ConnectionTimeout = 8, CommandTimeout = 3, PoolWaitTimeout = 8 }.ConnectionString;
        private static Task Open(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
        private static async Task Query(OceanBaseConnection connection) { using var command = connection.CreateCommand(); command.CommandText = "selected"; Assert.Equal(1, await command.ExecuteNonQueryAsync()); }
        private static PhysicalSessionPool? Pool(OceanBaseConnection connection) => (PhysicalSessionPool?)typeof(OceanBaseConnection).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection);
        private static void Cleanup(string configuration, PhysicalSessionPool? pool) { if (pool != null) { pool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool); } }
        private static async Task WaitForWaiter(PhysicalSessionPool pool)
        {
            var field = typeof(PhysicalSessionPool).GetField("_waiters", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while ((int)field.GetValue(pool)! == 0) await Task.Delay(10, deadline.Token);
        }
    }
}
