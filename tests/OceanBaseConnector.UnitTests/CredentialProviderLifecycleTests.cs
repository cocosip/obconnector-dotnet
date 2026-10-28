using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class CredentialProviderTests
    {
        [Fact]
        public async Task SourceDisposalWinsOpenFailureWhilePoolRetirementIsStillRunning()
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await connection.OpenAsync(); await connection.CloseAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<OceanBaseCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Async = _ => { entered.TrySetResult(); return late.Task; };
            var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var disposalThread = 0;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.pool.sessions")
                        owner.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (value != -1 || Environment.CurrentManagedThreadId != Volatile.Read(ref disposalThread)) return;
                retiring.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            });
            listener.Start();
            var opening = connection.OpenAsync();
            Task disposing = Task.CompletedTask;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                disposing = Task.Run(() => { Volatile.Write(ref disposalThread, Environment.CurrentManagedThreadId); source.Dispose(); });
                await retiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
                late.TrySetException(new InvalidOperationException("provider failed during retirement"));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            finally
            {
                release.Set(); late.TrySetResult(Credential(1)); connection.Close();
                await disposing.WaitAsync(TimeSpan.FromSeconds(5));
                await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SourceDisposalPreservesEstablishedBusyConnection(bool pooling)
        {
            await using var server = new TestInfrastructure.CredentialServer(
                ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port, pooling)).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await connection.OpenAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeProbeReply = () => { entered.TrySetResult(); return release.Task; };
            var session = connection.GetOpenSession();
            var probe = session.ProbeAsync(new ConnectionLog(null), 5);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await source.DisposeAsync();
                Assert.Equal(SessionState.Busy, session.State);
                release.TrySetResult();
                await probe.WaitAsync(TimeSpan.FromSeconds(5));
                using var command = connection.CreateCommand(); command.CommandText = "SELECT 1 FROM DUAL";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); await Record.ExceptionAsync(() => probe); }
        }

        [Fact]
        public async Task SourceShutdownCancellationCallbackCanDisposeSourceFromAnotherThread()
        {
            var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(1)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<OceanBaseCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Async = async token =>
            {
                using var registration = token.Register(() =>
                {
                    Task.Run(source.Dispose).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    reentered.TrySetResult();
                });
                entered.TrySetResult();
                return await late.Task;
            };
            var opening = connection.OpenAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(source.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
                await reentered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
            }
            finally { late.TrySetResult(Credential(1)); connection.Close(); await Record.ExceptionAsync(() => opening); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task SourceDisposalCancelsPendingCredentials(bool asynchronous, bool pooling)
        {
            await using var server = Server();
            var provider = new Provider(Credential(1));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<OceanBaseCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Sync = token => { entered.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return provider.Current; };
            provider.Async = _ => { entered.TrySetResult(); return late.Task; };
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port, pooling)) { ConnectionTimeout = 0 }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            var opening = Open(connection, asynchronous);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                source.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.True(source.Pool.IsEmpty);
                late.TrySetResult(Credential(1));
                Assert.Equal(0, server.Accepted);
                Assert.False(provider.Disposed);
            }
            finally
            {
                connection.Close(); late.TrySetResult(Credential(1));
                await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LogicalSnapshotMustRemainValidUntilPublicationAfterLoggingCallback(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var logger = new TestInfrastructure.RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger) { CredentialProvider = provider };
            try
            {
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                var expires = DateTimeOffset.UtcNow.AddSeconds(1); provider.Current = Credential(1, expires);
                logger.OnLog = entry => { if (entry.Id == 1000) Expire(expires).GetAwaiter().GetResult(); };
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(1, server.Accepted);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SupersededWarmupCannotDestroySessionBorrowedByAnotherOpen(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            var secondHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handshakes = 0;
            server.BeforeIdentity = () => { if (Interlocked.Increment(ref handshakes) != 2) return Task.CompletedTask; secondHandshake.TrySetResult(); return release.Task; };
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port, minimum: 2)).UseCredentialProvider(provider).Build();
            using var warming = (OceanBaseConnection)source.CreateConnection(); using var borrower = (OceanBaseConnection)source.CreateConnection(); using var rotating = (OceanBaseConnection)source.CreateConnection();
            var warm = Open(warming, asynchronous);
            try
            {
                await secondHandshake.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Open(borrower, asynchronous); var borrowed = borrower.GetOpenSession();
                provider.Current = Credential(2); await Open(rotating, asynchronous);
                release.TrySetResult(); await Assert.ThrowsAsync<OceanBaseException>(() => warm);
                Assert.Equal(ConnectionState.Open, borrower.State);
                if (asynchronous) await borrowed.ProbeAsync(new ConnectionLog(null), 2); else borrowed.Probe(new ConnectionLog(null), 2);
                await Close(borrower, asynchronous); Assert.Equal(SessionState.Closed, borrowed.State);
                await Close(rotating, asynchronous); Assert.Equal(3, server.Accepted);
            }
            finally { release.TrySetResult(); warming.Close(); borrower.Close(); rotating.Close(); await warm.ContinueWith(_ => { }); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LogicalSnapshotExpiryDuringIdleProbeCannotUseOlderPhysicalExpiry(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); var original = connection.GetOpenSession(); await Close(connection, asynchronous);
            var expires = DateTimeOffset.UtcNow.AddSeconds(1); provider.Current = Credential(1, expires);
            server.BeforeProbeReply = () => Expire(expires);
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(SessionState.Closed, original.State); Assert.Equal(1, server.Accepted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CredentialExpiryBoundsOtherwiseInfiniteCapacityWait(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { MaxPoolSize = 1, ConnectionTimeout = 0, PoolWaitTimeout = 0 }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseCredentialProvider(provider).Build();
            using var borrowed = (OceanBaseConnection)source.CreateConnection(); using var waiting = (OceanBaseConnection)source.CreateConnection();
            await Open(borrowed, asynchronous); provider.Current = Credential(1, DateTimeOffset.UtcNow.AddSeconds(1));
            var pending = Open(waiting, asynchronous);
            try
            {
                var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.IsType<OceanBaseException>(error); Assert.Equal(ConnectionState.Open, borrowed.State);
            }
            finally { waiting.Close(); await pending.ContinueWith(_ => { }); await Close(borrowed, asynchronous); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ExpiryDuringHandshakeOrIdentityCannotPublish(bool asynchronous, bool afterAuthentication)
        {
            await using var server = Server(); var expires = DateTimeOffset.UtcNow.AddSeconds(1);
            var provider = new Provider(Credential(1, expires));
            if (afterAuthentication) server.BeforeIdentity = () => Expire(expires); else server.BeforeGreeting = () => Expire(expires);
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(afterAuthentication ? 1 : 0, server.Authentications.Count);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ExpiredBorrowedSessionContinuesButCannotReturnOrReuse(bool asynchronous, bool duringReset)
        {
            await using var server = Server(); var expires = DateTimeOffset.UtcNow.AddSeconds(1);
            var provider = new Provider(Credential(1, expires));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); var original = connection.GetOpenSession();
            if (duringReset) server.BeforeResetReply = () => Expire(expires);
            else
            {
                await Expire(expires);
                if (asynchronous) await original.ProbeAsync(new ConnectionLog(null), 2); else original.Probe(new ConnectionLog(null), 2);
            }
            await Close(connection, asynchronous); Assert.Equal(SessionState.Closed, original.State);
            server.BeforeResetReply = null; provider.Current = Credential(1, DateTimeOffset.UtcNow.AddMinutes(1));
            await Open(connection, asynchronous); Assert.Equal(2, server.Accepted); await Close(connection, asynchronous);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExpiredIdleSessionIsNotExtendedBySameVersionRefresh(bool asynchronous)
        {
            await using var server = Server(); var expires = DateTimeOffset.UtcNow.AddSeconds(1);
            var provider = new Provider(Credential(1, expires));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); var original = connection.GetOpenSession(); await Close(connection, asynchronous);
            await Expire(expires); provider.Current = Credential(1, DateTimeOffset.UtcNow.AddMinutes(1));
            await Open(connection, asynchronous); Assert.NotSame(original, connection.GetOpenSession()); Assert.Equal(SessionState.Closed, original.State);
            Assert.Equal(2, server.Accepted); await Close(connection, asynchronous);
        }

        [Theory]
        [InlineData(false, "null")]
        [InlineData(true, "null")]
        [InlineData(true, "null-task")]
        [InlineData(false, "throw")]
        [InlineData(true, "throw")]
        public async Task InvalidProviderResultsLeaveNoRegistryReservation(bool asynchronous, string result)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            var configuration = Configuration(server.Port); using var connection = new OceanBaseConnection(configuration) { CredentialProvider = provider };
            PhysicalSessionPool? oldPool = null;
            PhysicalSessionPool? CapturePool() => (PhysicalSessionPool?)typeof(OceanBaseConnection).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection);
            provider.Sync = _ => { oldPool = CapturePool(); return result == "null" ? null! : throw new InvalidOperationException("secret-provider-token"); };
            if (result == "null-task") provider.Async = _ => { oldPool = CapturePool(); return null!; };
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.DoesNotContain("secret-provider-token", error.ToString()); Assert.Equal(0, server.Accepted);
                Assert.NotNull(oldPool); Assert.True(oldPool.IsEmpty); Assert.Throws<ObjectDisposedException>(oldPool.BeginRent);
                provider.Sync = null; provider.Async = null; await Open(connection, asynchronous); await Close(connection, asynchronous);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OutOfOrderProviderCompletionCannotRollBackPool(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(2));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldResult = new TaskCompletionSource<OceanBaseCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            provider.Sync = token => { if (Interlocked.Increment(ref calls) != 1) return provider.Current; entered.TrySetResult(); return oldResult.Task.WaitAsync(token).GetAwaiter().GetResult(); };
            provider.Async = _ => { if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(provider.Current); entered.TrySetResult(); return oldResult.Task; };
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var older = (OceanBaseConnection)source.CreateConnection(); using var newer = (OceanBaseConnection)source.CreateConnection();
            var pending = Open(older, asynchronous);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Open(newer, asynchronous);
                oldResult.TrySetResult(Credential(1)); await Assert.ThrowsAsync<OceanBaseException>(() => pending);
                Assert.Equal(1, server.Accepted); await Close(newer, asynchronous); source.Clear();
                provider.Sync = null; provider.Async = null; provider.Current = Credential(1);
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(older, asynchronous));
                provider.Current = Credential(3); await Open(older, asynchronous); await Close(older, asynchronous);
            }
            finally { oldResult.TrySetResult(Credential(1)); older.Close(); newer.Close(); await pending.ContinueWith(_ => { }); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RotationWhileHandshakeInFlightRetiresOldSessionOnReturn(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0;
            server.BeforeIdentity = () => { if (Interlocked.Increment(ref attempts) != 1) return Task.CompletedTask; entered.TrySetResult(); return release.Task; };
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var a = (OceanBaseConnection)source.CreateConnection(); using var b = (OceanBaseConnection)source.CreateConnection();
            var pending = Open(a, asynchronous);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); provider.Current = Credential(2); await Open(b, asynchronous);
                release.TrySetResult(); await pending; var original = a.GetOpenSession();
                Assert.Equal(ConnectionState.Open, a.State); await Close(a, asynchronous); Assert.Equal(SessionState.Closed, original.State);
                var current = b.GetOpenSession(); await Close(b, asynchronous); await Open(a, asynchronous);
                Assert.Same(current, a.GetOpenSession()); await Close(a, asynchronous); Assert.Equal(2, server.Accepted);
            }
            finally { release.TrySetResult(); a.Close(); b.Close(); await pending.ContinueWith(_ => { }); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReentrantCloseDuringCredentialAcquisitionCannotOpen(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var connection = new OceanBaseConnection(Configuration(server.Port)) { CredentialProvider = provider };
            provider.Sync = _ => { connection.Close(); return provider.Current; };
            try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(connection, asynchronous)); Assert.Equal(0, server.Accepted); }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SnapshotReplacesPamPasswordsAcrossEndpointCopyAndTls(bool asynchronous)
        {
            await using var server = new TestInfrastructure.CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, pam: true);
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { TlsMode = OceanBaseTlsMode.VerifyFull };
            configuration["Password2"] = "static-secondary";
            var provider = new Provider(new OceanBaseCredential("user", "fresh-primary", 1, additionalPasswords: new Dictionary<int, string> { [2] = "fresh-secondary" }));
            using var source = new OceanBaseDataSourceBuilder(configuration.ConnectionString).UseCredentialProvider(provider).UseRemoteCertificateValidationCallback((_, _, _, _) => true).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous);
            Assert.Equal(new[] { "fresh-primary\0", "fresh-secondary\0" }, System.Linq.Enumerable.Select(server.PamResponses, System.Text.Encoding.UTF8.GetString));
            await Close(connection, asynchronous);
            provider.Current = new OceanBaseCredential("user", "fresh-primary", 1, additionalPasswords: new Dictionary<int, string> { [2] = "changed-secondary" });
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(1, server.Accepted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExpiryBeforeAuthenticationContinuationSendsNoPamPassword(bool asynchronous)
        {
            await using var server = new TestInfrastructure.CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, pam: true);
            var expires = DateTimeOffset.UtcNow.AddSeconds(1); server.BeforeAuthSwitch = () => Expire(expires);
            var provider = new Provider(Credential(1, expires));
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { TlsMode = OceanBaseTlsMode.VerifyFull }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseCredentialProvider(provider).UseRemoteCertificateValidationCallback((_, _, _, _) => true).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous)); Assert.Empty(server.PamResponses);
        }

        private static async Task Expire(DateTimeOffset expires)
        {
            while (DateTimeOffset.UtcNow <= expires) await Task.Delay(20);
        }
    }
}
