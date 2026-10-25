using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed class ConnectionPhaseDiagnosticsTests
    {
        private const string MetricName = "oceanbase.client.connection.phase.duration";
        private static string Configuration(int port, bool pooled = false) =>
            $"Server=127.0.0.1;Port={port};User ID=u;Password=private-password;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=1";
        private static CredentialServer Server() => new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
            sessionStateOk: TextCommandTests.TimeZoneOk("+00:00"));
        private static Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token = default) =>
            asynchronous ? connection.OpenAsync(token) : Task.Run(connection.Open, token);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SuccessfulOpenMeasuresOnlyExecutedPhases(bool asynchronous, bool callbacks)
        {
            await using var server = Server(); using var capture = new Capture();
            using var parent = new Activity("application").Start();
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            if (callbacks) { connection.CredentialProvider = new Provider(); connection.AddressResolver = new Resolver(server.Port); }
            await Open(connection, asynchronous);
            Assert.Equal((callbacks ? new[] { "credentials", "discovery" } : Array.Empty<string>())
                .Concat(new[] { "tcp", "greeting", "authentication", "identity" }), capture.Phases);
            Assert.All(capture.Samples, sample => Assert.Equal("success", sample.Outcome));
            Assert.Same(parent, Activity.Current); Assert.Equal(ConnectionState.Open, connection.State);
        }

        [Theory]
        [InlineData(false, "credentials")]
        [InlineData(true, "credentials")]
        [InlineData(false, "discovery")]
        [InlineData(true, "discovery")]
        public async Task CallbackFailureHasOneSafeFailedPhaseAndNoTransport(bool asynchronous, string failure)
        {
            await using var server = Server(); using var capture = new Capture();
            using var connection = new OceanBaseConnection(Configuration(server.Port))
            { CredentialProvider = new Provider { Fail = failure == "credentials" }, AddressResolver = new Resolver(server.Port) { Fail = failure == "discovery" } };
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(failure == "credentials" ? new[] { "credentials" } : new[] { "credentials", "discovery" }, capture.Phases);
            Assert.Equal("error", capture.Samples.Last().Outcome); Assert.Equal(0, server.Accepted);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReusedPoolSessionMeasuresProbeWithoutRepeatingHandshake(bool asynchronous)
        {
            await using var server = Server(); using var capture = new Capture();
            using var connection = new OceanBaseConnection(Configuration(server.Port, pooled: true));
            try
            {
                await Open(connection, asynchronous); var session = connection.GetOpenSession();
                connection.Close(); capture.Clear(); await Open(connection, asynchronous);
                Assert.Same(session, connection.GetOpenSession()); Assert.Equal(new[] { "pool.probe" }, capture.Phases);
                Assert.Equal("success", Assert.Single(capture.Samples).Outcome); Assert.Equal(1, server.Accepted);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false, "greeting")]
        [InlineData(true, "greeting")]
        [InlineData(false, "tls")]
        [InlineData(true, "tls")]
        [InlineData(false, "authentication")]
        [InlineData(true, "authentication")]
        [InlineData(false, "identity")]
        [InlineData(true, "identity")]
        public async Task FailedHandshakeReportsItsExactStage(bool asynchronous, string failure)
        {
            using var capture = new Capture();
            var server = LoopbackServer.Start(async peer =>
            {
                var channel = new PacketChannel(peer.GetStream());
                if (failure == "greeting") { await channel.WritePacketAsync(new byte[] { 0 }); return; }
                var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
                if (failure == "tls")
                {
                    var capabilitiesOffset = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1;
                    greeting[capabilitiesOffset + 1] &= 0xf7; // CLIENT_SSL=0x0800.
                }
                await channel.WritePacketAsync(greeting);
                if (failure == "tls") { Assert.Equal(-1, await ReadByte(peer)); return; }
                await channel.ReadPacketAsync();
                if (failure == "authentication") { await channel.WritePacketAsync(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48 }); return; }
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand(); await channel.ReadPacketAsync(); await channel.WritePacketAsync(new byte[] { 0 });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + (failure == "tls" ? ";TLS Mode=Required" : ""));
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                var expected = new List<string> { "tcp", "greeting" };
                if (failure == "tls") expected.Add("tls");
                if (failure == "authentication" || failure == "identity") expected.Add("authentication");
                if (failure == "identity") expected.Add("identity");
                Assert.Equal(expected, capture.Phases); Assert.Equal("error", capture.Samples.Last().Outcome);
                Assert.All(capture.Samples.SkipLast(1), sample => Assert.Equal("success", sample.Outcome));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            static async Task<int> ReadByte(TcpClient peer)
            { var data = new byte[1]; return await peer.GetStream().ReadAsync(data) == 0 ? -1 : data[0]; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TcpRetryMeasuresEachAttemptWithoutEndpointTags(bool asynchronous)
        {
            await using var server = Server(); using var capture = new Capture();
            var unavailable = new TcpListener(IPAddress.Loopback, 0); unavailable.Start();
            var deadPort = ((IPEndPoint)unavailable.LocalEndpoint).Port; unavailable.Stop();
            using var connection = new OceanBaseConnection(Configuration(server.Port))
            { AddressResolver = new Resolver(server.Port) { Addresses = "127.0.0.1:" + deadPort + ",127.0.0.1:" + server.Port } };
            await Open(connection, asynchronous);
            Assert.Equal(new[] { "discovery", "tcp", "tcp", "greeting", "authentication", "identity" }, capture.Phases);
            Assert.Equal(new[] { "success", "error", "success", "success", "success", "success" }, capture.Samples.Select(sample => sample.Outcome));
            Assert.Equal(1, server.Accepted);
        }

        [Theory]
        [InlineData(false, "release")]
        [InlineData(true, "release")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "elapsed")]
        [InlineData(true, "elapsed")]
        public async Task PoolWaitMeasuresOnlyBlockedTimeAndPreservesCapacity(bool asynchronous, string completion)
        {
            await using var server = Server(); using var capture = new Capture();
            var configuration = Configuration(server.Port, pooled: true) + ";Pool Wait Timeout=1";
            using var first = new OceanBaseConnection(configuration); using var second = new OceanBaseConnection(configuration);
            try
            {
                await Open(first, asynchronous); capture.Clear(); var pending = Open(second, asynchronous);
                await capture.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (completion == "release") { first.Close(); await pending; }
                else if (completion == "cancel") { second.Close(); await Assert.ThrowsAnyAsync<Exception>(() => pending); }
                else await Assert.ThrowsAsync<TimeoutException>(() => pending);
                if (completion == "elapsed")
                {
                    // Finite semaphore waits can finish before the overall rent budget; every attempt is measured.
                    Assert.NotEmpty(capture.Samples);
                    Assert.All(capture.Samples, sample => Assert.Equal(("pool.wait", "elapsed"), sample));
                }
                else
                {
                    Assert.Equal(completion == "release" ? new[] { "pool.wait", "pool.probe" } : new[] { "pool.wait" }, capture.Phases);
                    Assert.Equal(completion == "release" ? "success" : "canceled", capture.Samples.First().Outcome);
                }
                Assert.Equal(1, server.Accepted);
                first.Close(); second.Close(); await Open(second, asynchronous); Assert.Equal(1, server.Accepted);
            }
            finally { first.Close(); second.Close(); OceanBaseConnection.ClearPool(first); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ThrowingPhaseListenerCannotChangeConnectionOrAmbientActivity(bool asynchronous)
        {
            await using var server = Server(); using var parent = new Activity("application").Start();
            using var listener = new MeterListener { MeasurementsCompleted = (_, _) => { } };
            listener.InstrumentPublished = (instrument, owner) =>
            { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == MetricName) owner.EnableMeasurementEvents(instrument); };
            var called = 0;
            listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
            { Interlocked.Increment(ref called); Activity.Current = null; throw new InvalidOperationException("private-listener-token"); });
            listener.Start(); using var connection = new OceanBaseConnection(Configuration(server.Port));
            await Open(connection, asynchronous); Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(4, called); Assert.Same(parent, Activity.Current);
            using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(0, await command.ExecuteNonQueryAsync());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TlsSuccessIsMeasuredBetweenGreetingAndAuthentication(bool asynchronous)
        {
            await using var server = new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, channel => TextCommandTests.ReadClientCommand(channel));
            using var clientCertificate = TestCertificateFactory.CreateSelfSigned("private-client");
            using var capture = new Capture();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";TLS Mode=VerifyFull")
            { ClientCertificate = clientCertificate, RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate!.GetCertHashString() == server.Certificate.GetCertHashString() };
            await Open(connection, asynchronous);
            Assert.Equal(new[] { "tcp", "greeting", "tls", "authentication", "identity" }, capture.Phases);
            Assert.All(capture.Samples, sample => Assert.Equal("success", sample.Outcome));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task CallbackInterruptionStopsPhaseAndKeepsOpenFailureClassification(bool asynchronous, bool credentials, bool timeout)
        {
            await using var server = Server(); using var capture = new Capture(); var callback = new BlockedCallbacks();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Connection Timeout=" + (timeout ? "1" : "0"));
            if (credentials) connection.CredentialProvider = callback; else connection.AddressResolver = callback;
            using var canceled = new CancellationTokenSource(); var pending = Open(connection, asynchronous, canceled.Token);
            await callback.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!timeout) { if (asynchronous) canceled.Cancel(); else connection.Close(); }
            if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => pending);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var sample = Assert.Single(capture.Samples);
            Assert.Equal(credentials ? "credentials" : "discovery", sample.Phase); Assert.Equal("canceled", sample.Outcome);
            Assert.Equal(0, server.Accepted); Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CanceledProbeIsMeasuredAndDoesNotLeakPoolCapacity(bool asynchronous)
        {
            await using var server = Server(); using var capture = new Capture();
            var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var connection = new OceanBaseConnection(Configuration(server.Port, pooled: true));
            try
            {
                await Open(connection, asynchronous); connection.Close(); capture.Clear();
                server.BeforeProbeReply = async () => { probing.TrySetResult(); await release.Task; };
                using var canceled = new CancellationTokenSource(); var pending = Open(connection, asynchronous, canceled.Token);
                await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (asynchronous) canceled.Cancel(); else connection.Close();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal("pool.probe", Assert.Single(capture.Samples).Phase); Assert.Equal("canceled", capture.Samples.Single().Outcome);
                server.BeforeProbeReply = null; release.TrySetResult(); await Open(connection, asynchronous);
                Assert.Equal(2, server.Accepted); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PoolWaitDurationExcludesCleanupListenerExecution(bool asynchronous)
        {
            await using var server = Server(); using var capture = new Capture();
            using var cleanupListener = new MeterListener { MeasurementsCompleted = (_, _) => { } };
            cleanupListener.InstrumentPublished = (instrument, owner) =>
            { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.pool.waiters") owner.EnableMeasurementEvents(instrument); };
            var callbackTime = TimeSpan.Zero;
            cleanupListener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (value != -1) return;
                var elapsed = Stopwatch.StartNew(); Thread.Sleep(500); callbackTime = elapsed.Elapsed;
            });
            cleanupListener.Start();
            var configuration = Configuration(server.Port, pooled: true);
            using var first = new OceanBaseConnection(configuration); using var second = new OceanBaseConnection(configuration);
            try
            {
                await Open(first, asynchronous); capture.Clear(); var pending = Open(second, asynchronous);
                await capture.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); first.Close(); await pending;
                Assert.Equal("pool.wait", capture.Phases[0]);
                Assert.True(callbackTime.TotalMilliseconds >= 500);
                Assert.True(capture.Seconds[0] < callbackTime.TotalSeconds, "Blocked time must exclude the later cleanup callback.");
            }
            finally { first.Close(); second.Close(); OceanBaseConnection.ClearPool(first); }
        }

        private sealed class BlockedCallbacks : IOceanBaseCredentialProvider, IOceanBaseAddressResolver
        {
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private async Task Block(CancellationToken token) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            public string Resolve(CancellationToken token) { Block(token).GetAwaiter().GetResult(); return "unused"; }
            public async Task<string> ResolveAsync(CancellationToken token) { await Block(token); return "unused"; }
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token)
            { Block(token).GetAwaiter().GetResult(); throw new InvalidOperationException(); }
            public async Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token)
            { await Block(token); throw new InvalidOperationException(); }
        }

        private sealed class Provider : IOceanBaseCredentialProvider
        {
            internal bool Fail;
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token) =>
                Fail ? throw new InvalidOperationException("private-provider-token") : new OceanBaseCredential("private-user", "private-password", 1);
            public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token) => Task.FromResult(GetCredential(context, token));
        }
        private sealed class Resolver(int port) : IOceanBaseAddressResolver
        {
            internal bool Fail;
            internal string? Addresses;
            public string Resolve(CancellationToken token) => Fail ? throw new InvalidOperationException("private-discovery-token") : Addresses ?? "127.0.0.1:" + port;
            public Task<string> ResolveAsync(CancellationToken token) => Task.FromResult(Resolve(token));
        }
        private sealed class Capture : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener { MeasurementsCompleted = (_, _) => { } };
            private readonly ConcurrentQueue<(double Value, string? Unit, KeyValuePair<string, object?>[] Tags)> _samples = new();
            internal (string Phase, string Outcome)[] Samples => _samples.Select(sample =>
            {
                Assert.Equal("s", sample.Unit); Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0);
                var pairs = sample.Tags.ToDictionary(pair => pair.Key, pair => pair.Value);
                Assert.Equal(2, pairs.Count);
                return (Assert.IsType<string>(pairs["oceanbase.connection.phase"]), Assert.IsType<string>(pairs["oceanbase.outcome"]));
            }).ToArray();
            internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string[] Phases => Samples.Select(sample => sample.Phase).ToArray();
            internal double[] Seconds => _samples.Select(sample => sample.Value).ToArray();
            internal Capture()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && (instrument.Name == MetricName || instrument.Name == "oceanbase.client.pool.waiters")) listener.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<long>((_, value, _, _) => { if (value == 1) Waiting.TrySetResult(); });
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                    _samples.Enqueue((value, instrument.Unit, tags.ToArray())));
                _listener.Start();
            }
            internal void Clear() => _samples.Clear();
            public void Dispose() => _listener.Dispose();
        }
    }
}
