using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed class PoolLeaseMetricsTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CurrentLeasesTrackReuseAndSurviveClearAndDisposalUntilReturned(bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var capture = new LeaseCapture();
            using var pool = new PhysicalSessionPool();
            var options = Options(server.Port);
            var first = await Rent(pool, options, asynchronous); await first.OpenAsync();
            PhysicalSession? second = null;
            try
            {
                Assert.Equal(1, capture.Current);
                second = await Rent(pool, options, asynchronous); await second.OpenAsync();
                Assert.Equal(2, capture.Current); Assert.Equal(2, capture.Borrows);
                Assert.True(asynchronous ? await pool.ReturnAsync(first, new ConnectionLog(null)) : pool.Return(first, new ConnectionLog(null)));
                Assert.Equal(1, capture.Current);
                var reused = await Rent(pool, options, asynchronous); Assert.Same(first, reused);
                Assert.Equal(2, capture.Current); Assert.Equal(3, capture.Borrows);
                pool.Clear(); pool.Dispose(); Assert.Equal(2, capture.Current);
                Assert.False(asynchronous ? await pool.ReturnAsync(reused, new ConnectionLog(null)) : pool.Return(reused, new ConnectionLog(null)));
                Assert.Equal(1, capture.Current);
                pool.Discard(second); Assert.Equal(0, capture.Current);
                pool.Discard(second); pool.Discard(reused); Assert.Equal(0, capture.Current);
                Assert.Equal(3, capture.Borrows);
                capture.AssertMetadata();
            }
            finally { pool.Discard(first); if (second != null) pool.Discard(second); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CanceledWaitAndForeignDiscardDoNotChangeCurrentLeases(bool asynchronous)
        {
            using var capture = new LeaseCapture();
            using var pool = new PhysicalSessionPool(); using var other = new PhysicalSessionPool();
            var options = Options(1, maximum: 1);
            var session = await Rent(pool, options, asynchronous);
            try
            {
                Assert.Equal(1, capture.Current);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rent(pool, options, asynchronous, cancel.Token));
                Assert.Equal(1, capture.Current); Assert.Equal(1, capture.Borrows);
                other.Discard(session); Assert.Equal(1, capture.Current);
                pool.Discard(session); Assert.Equal(0, capture.Current);
                capture.AssertMetadata();
            }
            finally { pool.Discard(session); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PoolWaitDurationClassifiesCancellationAndTimeout(bool asynchronous)
        {
            using var capture = new LeaseCapture(); using var pool = new PhysicalSessionPool();
            var session = await Rent(pool, Options(1, maximum: 1), asynchronous);
            try
            {
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rent(pool, Options(1, maximum: 1), asynchronous, cancellation.Token));
                await Assert.ThrowsAsync<TimeoutException>(() => Rent(pool, Options(1, maximum: 1), asynchronous));
                Assert.Contains("canceled", capture.WaitOutcomes);
                Assert.Contains("timeout", capture.WaitOutcomes);
            }
            finally { pool.Discard(session); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WarmupAndIdleSessionsAreExcludedButTryRentIsCounted(bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var capture = new LeaseCapture(); using var pool = new PhysicalSessionPool();
            var session = await Rent(pool, Options(server.Port, minimum: 2), asynchronous);
            try
            {
                Assert.Equal(2, server.Accepted); Assert.Equal(1, capture.Current);
                Assert.True(asynchronous ? await pool.ReturnAsync(session, new ConnectionLog(null)) : pool.Return(session, new ConnectionLog(null)));
                Assert.Equal(0, capture.Current);
                Assert.True(pool.TryRent(out var reused)); Assert.Same(session, reused);
                Assert.Equal(1, capture.Current); Assert.Equal(2, capture.Borrows);
            }
            finally { pool.Discard(session); }
            pool.Dispose(); Assert.Equal(0, capture.Current); capture.AssertMetadata();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ReturnEndsLeaseBeforeResetEvenWhenResetFails(bool asynchronous, bool fail)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var capture = new LeaseCapture(); using var pool = new PhysicalSessionPool();
            var session = await Rent(pool, Options(server.Port), asynchronous); await session.OpenAsync();
            server.BeforeResetReply = () =>
            {
                Assert.Equal(0, capture.Current);
                if (fail) throw new IOException("Injected reset failure.");
                return Task.CompletedTask;
            };
            try
            {
                Assert.Equal(1, capture.Current);
                Assert.Equal(!fail, asynchronous ? await pool.ReturnAsync(session, new ConnectionLog(null)) : pool.Return(session, new ConnectionLog(null)));
                Assert.Equal(0, capture.Current); capture.AssertMetadata();
            }
            finally { pool.Discard(session); }
            Assert.Equal(0, capture.Current);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedPublicOpenReleasesItsPoolLease(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48, 110, 111 });
            });
            using var capture = new LeaseCapture();
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;Pooling=true;TLS Mode=Disabled;Connection Timeout=5");
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.Equal(0, capture.Current); Assert.Equal(1, capture.Borrows); capture.AssertMetadata();
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void LeaseObserverCanClearPoolFromAnotherThread()
        {
            using var capture = new LeaseCapture(); using var pool = new PhysicalSessionPool();
            var completed = false;
            capture.OnLease = value => { if (value > 0) completed = Task.Run(pool.Clear).Wait(TimeSpan.FromSeconds(5)); };
            var session = pool.Rent(Options(1), null, new EndpointSelector(), out _);
            try { Assert.True(completed); Assert.Equal(1, capture.Current); }
            finally { pool.Discard(session); }
            Assert.Equal(0, capture.Current);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CanceledPublicOpenReleasesLeaseDuringGreetingOrIdentity(bool identity)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var capture = new LeaseCapture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Pause() { entered.TrySetResult(); return release.Task; }
            if (identity) server.BeforeIdentity = Pause; else server.BeforeGreeting = Pause;
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;Pooling=true;TLS Mode=Disabled;Connection Timeout=5");
            using var stop = new CancellationTokenSource();
            var opening = connection.OpenAsync(stop.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, capture.Current);
                stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, capture.Current); Assert.Equal(1, capture.Borrows);
            }
            finally
            {
                stop.Cancel(); release.TrySetResult();
                await Record.ExceptionAsync(() => opening); connection.Close(); OceanBaseConnection.ClearPool(connection);
            }
        }

        [Fact]
        public async Task ConcurrentDuplicateDiscardAndPoolDisposalBalanceAllLeases()
        {
            using var capture = new LeaseCapture(); using var pool = new PhysicalSessionPool();
            var sessions = Enumerable.Range(0, 32).Select(index => pool.Rent(Options(1, maximum: 32), null, new EndpointSelector(), out _)).ToArray();
            Assert.Equal(32, capture.Current);
            await Task.WhenAll(sessions.Select(session => Task.Run(() => { pool.Discard(session); pool.Discard(session); }))
                .Append(Task.Run(pool.Clear)).Append(Task.Run(pool.Dispose))).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, capture.Current); Assert.Equal(32, capture.Borrows); capture.AssertMetadata();
        }

        private static OceanBaseConnectionOptions Options(int port, int maximum = 2, int minimum = 0) => new OceanBaseConnectionStringBuilder
        { Server = "127.0.0.1", Port = port, UserId = "u", Pooling = true, MaxPoolSize = maximum, MinPoolSize = minimum, TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5, CommandTimeout = 3 }.Build();

        private static Task<PhysicalSession> Rent(PhysicalSessionPool pool, OceanBaseConnectionOptions options, bool asynchronous, CancellationToken token = default) =>
            asynchronous ? pool.RentAsync(options, null, new EndpointSelector(), token) : Task.Run(() => pool.Rent(options, null, new EndpointSelector(), out _, token));

        private sealed class LeaseCapture : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener();
            private long _current, _borrows;
            private int _invalidMetadata;
            internal readonly ConcurrentBag<string> WaitOutcomes = new ConcurrentBag<string>();
            internal long Current => Interlocked.Read(ref _current);
            internal long Borrows => Interlocked.Read(ref _borrows);
            internal Action<long>? OnLease;
            internal LeaseCapture()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "OceanBaseConnector" &&
                        (instrument.Name == "oceanbase.client.pool.leased" || instrument.Name == "oceanbase.client.pool.borrowed" || instrument.Name == "oceanbase.client.pool.wait.duration"))
                        listener.EnableMeasurementEvents(instrument);
                };
                _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                {
                    if (tags.Length != 0) Interlocked.Increment(ref _invalidMetadata);
                    if (instrument.Name == "oceanbase.client.pool.leased")
                    {
                        if (instrument.Unit != "{session}") Interlocked.Increment(ref _invalidMetadata);
                        Interlocked.Add(ref _current, value);
                        OnLease?.Invoke(value);
                    }
                    else Interlocked.Add(ref _borrows, value);
                });
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                {
                    if (instrument.Name != "oceanbase.client.pool.wait.duration") return;
                    foreach (var tag in tags)
                        if (tag.Key == "oceanbase.outcome") WaitOutcomes.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
                });
                _listener.Start();
            }
            internal void AssertMetadata() => Assert.Equal(0, Volatile.Read(ref _invalidMetadata));
            public void Dispose() => _listener.Dispose();
        }
    }
}
