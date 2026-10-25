using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed class ConnectRetryTests
    {
        [Theory]
        [InlineData("Connect Retry Count=0;Connect Retry Interval=1")]
        [InlineData("Connect Retry Count=10;Connect Retry Interval=60")]
        public void RetryOptionsAcceptAndSerializeSupportedBounds(string policy)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;" + policy);
            var copy = new OceanBaseConnectionStringBuilder(builder.ConnectionString);
            Assert.Equal(builder["Connect Retry Count"], copy["Connect Retry Count"]);
            Assert.Equal(builder["Connect Retry Interval"], copy["Connect Retry Interval"]);
            Assert.Contains("Connect Retry Count", builder.ToSanitizedConnectionString(false));
        }

        public static IEnumerable<object[]> OpenPaths()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var oracle in new[] { false, true })
                    foreach (var path in new[] { 0, 1, 2, 3 }) yield return new object[] { asynchronous, oracle, path };
        }

        [Theory]
        [MemberData(nameof(OpenPaths))]
        public async Task RetryRoundConnectsAnEndpointThatRecoversAfterFirstTcpFailure(bool asynchronous, bool oracle, int path)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            using var endpoint = RefusedEndpoint();
            var port = ((IPEndPoint)endpoint.LocalEndPoint!).Port;
            var pooled = path != 0;
            var configuration = Configuration(port) + $";Pooling={pooled};Min Pool Size={(path == 3 ? 1 : 0)};Max Pool Size=1";
            var callbacks = new Snapshots(port);
            using var source = path == 2 ? new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(callbacks).UseCredentialProvider(callbacks).Build() : null;
            using var connection = source == null ? new OceanBaseConnection(configuration) { AddressResolver = callbacks, CredentialProvider = callbacks } : (OceanBaseConnection)source.CreateConnection();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new TcpAttempts(() => { endpoint.Listen(1); started.TrySetResult(); });
            var serving = Task.Run(async () =>
            {
                await started.Task.WaitAsync(stop.Token);
                using var client = new TcpClient { Client = await endpoint.AcceptAsync(stop.Token), NoDelay = true };
                var channel = await Authenticate(client, oracle, stop.Token);
                var request = await TextCommandTests.ReadClientCommand(channel);
                if (path == 3 && request[0] == 14)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                    channel.BeginCommand(); request = await TextCommandTests.ReadClientCommand(channel);
                }
                Assert.Equal(3, request[0]); Assert.Equal("after retry", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, stop.Token);
                if (pooled)
                {
                    channel.BeginCommand(); Assert.Equal(31, (await channel.ReadPacketAsync(stop.Token))[0]);
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                    if (oracle) await ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                }
                Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            Exception? failure = null;
            try
            {
                await Open(connection, asynchronous, stop.Token);
                Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(new[] { "error", "success" }, capture.Outcomes);
                Assert.True(capture.SecondsBetweenAttempts >= 0.95, "The additional round must wait for its interval.");
                Assert.Equal(1, callbacks.Credentials); Assert.Equal(1, callbacks.Resolutions);
                using var command = connection.CreateCommand(); command.CommandText = "after retry";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(stop.Token) : command.ExecuteNonQuery());
                connection.Close();
                if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(connection);
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(connection);
                stop.Cancel(); endpoint.Dispose();
                try { await serving; }
                catch (OperationCanceledException) when (failure != null) { }
            }
        }

        [Fact]
        public void RetryDefaultsAndImmutableCopiesRetainThePolicy()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u");
            Assert.Equal(0, builder.ConnectRetryCount); Assert.Equal(1, builder.ConnectRetryInterval);
            var original = builder.Build();
            builder.ConnectRetryCount = 10; builder.ConnectRetryInterval = 60;
            var snapshot = builder.Build(); builder.Clear();
            foreach (var options in new[] { snapshot, snapshot.ForEndpoint(OceanBaseEndpoint.Parse("127.0.0.1", 2882)[0]), snapshot.WithCredential(new OceanBaseCredential("new-user", "secret", 1)) })
            { Assert.Equal(10, options.ConnectRetryCount); Assert.Equal(60, options.ConnectRetryIntervalSeconds); }
            Assert.Equal(0, original.ConnectRetryCount); Assert.Equal(1, original.ConnectRetryIntervalSeconds);
            Assert.Equal("Connect Retry Count", OceanBaseConnectionStringBuilder.ConnectRetryCountKey);
            Assert.Equal("Connect Retry Interval", OceanBaseConnectionStringBuilder.ConnectRetryIntervalKey);
        }

        [Theory]
        [InlineData("Connect Retry Count", "-1")]
        [InlineData("Connect Retry Count", "11")]
        [InlineData("Connect Retry Count", "2147483648")]
        [InlineData("Connect Retry Count", "1.5")]
        [InlineData("Connect Retry Interval", "-1")]
        [InlineData("Connect Retry Interval", "0")]
        [InlineData("Connect Retry Interval", "61")]
        [InlineData("Connect Retry Interval", "2147483648")]
        [InlineData("Connect Retry Interval", "oops")]
        public void RetryPolicyRejectsInvalidValuesAtEveryPublicSetter(string key, string value)
        {
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder(key + "=" + value));
            var builder = new OceanBaseConnectionStringBuilder();
            Assert.Throws<ArgumentException>(() => builder[key] = value);
            if (int.TryParse(value, out var number))
            {
                if (key == "Connect Retry Count") Assert.Throws<ArgumentException>(() => builder.ConnectRetryCount = number);
                else Assert.Throws<ArgumentException>(() => builder.ConnectRetryInterval = number);
            }
        }

        [Theory]
        [InlineData(false, -1)]
        [InlineData(true, -1)]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task RefusedTcpStopsAtTheConfiguredRoundLimitEvenWithoutDeadline(bool asynchronous, int retries)
        {
            using var endpoint = RefusedEndpoint(); var port = ((IPEndPoint)endpoint.LocalEndPoint!).Port;
            var builder = new OceanBaseConnectionStringBuilder(Configuration(port) + ";Connection Timeout=0;Password=private-secret");
            if (retries < 0) builder.Remove("Connect Retry Count"); else builder.ConnectRetryCount = retries;
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            using var capture = new TcpAttempts();
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(Enumerable.Repeat("error", Math.Max(retries, 0) + 1), capture.Outcomes);
            Assert.Equal(ConnectionState.Closed, connection.State); Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Contains("127.0.0.1:" + port, error.Message); Assert.DoesNotContain("private-secret", error.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AHealthyNextCandidateDoesNotWaitForTheRetryInterval(bool asynchronous)
        {
            using var failed = RefusedEndpoint();
            using var healthy = new TcpListener(IPAddress.Loopback, 0); healthy.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)failed.LocalEndPoint!).Port) + $";Server=127.0.0.1:{((IPEndPoint)failed.LocalEndPoint!).Port},127.0.0.1:{((IPEndPoint)healthy.LocalEndpoint).Port};Connect Retry Interval=60");
            using var capture = new TcpAttempts();
            var serving = Task.Run(async () =>
            {
                using var peer = await healthy.AcceptTcpClientAsync(stop.Token);
                await Authenticate(peer, false, stop.Token);
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            try
            {
                await Open(connection, asynchronous, stop.Token);
                Assert.Equal(new[] { "error", "success" }, capture.Outcomes);
                connection.Close(); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); stop.Cancel(); healthy.Stop(); await ObserveStopped(serving, stop.Token); }
        }

        [Theory]
        [InlineData(true, "cancel")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        public async Task BackoffHonorsCancellationCloseAndTheOriginalDeadlineAndReleasesPoolCapacity(bool asynchronous, string ending)
        {
            using var endpoint = RefusedEndpoint(); var port = ((IPEndPoint)endpoint.LocalEndPoint!).Port;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var caller = new CancellationTokenSource();
            using var connection = new OceanBaseConnection(Configuration(port) + $";Connection Timeout={(ending == "timeout" ? 4 : 10)};Connect Retry Interval=60;Pooling=true;Max Pool Size=1");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new TcpAttempts(() => { endpoint.Listen(1); ready.TrySetResult(); });
            var opening = Open(connection, asynchronous, caller.Token);
            await ready.Task.WaitAsync(stop.Token);
            await Task.Delay(150, stop.Token); Assert.False(opening.IsCompleted);
            if (ending == "cancel") caller.Cancel(); else if (ending == "close") connection.Close();
            if (ending == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(stop.Token));
            else
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(stop.Token));
                if (ending == "cancel") Assert.Equal(caller.Token, error.CancellationToken);
            }
            Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(new[] { "error" }, capture.Outcomes);
            var serving = Task.Run(async () =>
            {
                using var peer = new TcpClient { Client = await endpoint.AcceptAsync(stop.Token), NoDelay = true };
                var channel = await Authenticate(peer, false, stop.Token);
                Assert.Equal(31, (await channel.ReadPacketAsync(stop.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            try
            {
                await Open(connection, asynchronous, stop.Token); Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(new[] { "error", "success" }, capture.Outcomes);
                connection.Close(); OceanBaseConnection.ClearPool(connection); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); stop.Cancel(); endpoint.Dispose(); await ObserveStopped(serving, stop.Token); }
        }

        [Theory]
        [InlineData(false, "greeting")]
        [InlineData(true, "greeting")]
        [InlineData(false, "authentication")]
        [InlineData(true, "authentication")]
        [InlineData(false, "identity")]
        [InlineData(true, "identity")]
        [InlineData(false, "tls")]
        [InlineData(true, "tls")]
        [InlineData(false, "command")]
        [InlineData(true, "command")]
        public async Task RetryPolicyNeverRetriesAfterTcpSucceeded(bool asynchronous, string stage)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Connect Retry Count=10" + (stage == "tls" ? ";TLS Mode=Required" : ""));
            using var capture = new TcpAttempts();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                if (stage == "greeting") return;
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                await channel.ReadPacketAsync(stop.Token);
                if (stage == "tls") return;
                if (stage != "authentication")
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                    if (stage == "command")
                    {
                        await ServerIdentity.ReplyAsync(channel, false, token: stop.Token);
                        Assert.Equal(3, (await TextCommandTests.ReadClientCommand(channel))[0]);
                    }
                    else { channel.BeginCommand(); Assert.Equal(3, (await channel.ReadPacketAsync(stop.Token))[0]); }
                }
                await channel.WritePacketAsync(new byte[] { 255, 21, 4 }.Concat(Encoding.ASCII.GetBytes("#08S01 private-detail")).ToArray(), stop.Token);
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            try
            {
                if (stage == "command")
                {
                    await Open(connection, asynchronous, stop.Token);
                    using var command = connection.CreateCommand(); command.CommandText = "fail once";
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(stop.Token) : Task.Run(command.ExecuteNonQuery));
                    Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous, stop.Token));
                connection.Close(); await serving.WaitAsync(stop.Token);
                Assert.Equal(new[] { "success" }, capture.Outcomes); Assert.False(listener.Pending());
            }
            finally { connection.Close(); stop.Cancel(); listener.Stop(); await ObserveStopped(serving, stop.Token); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task RetryEnabledWarmupSharesOneLogicalOpenDeadline(bool asynchronous, bool dataSource)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false)
            { BeforeGreeting = () => Task.Delay(1200) };
            var configuration = Configuration(server.Port) + ";Pooling=true;Min Pool Size=2;Max Pool Size=2;Connection Timeout=2";
            using var source = dataSource ? new OceanBaseDataSourceBuilder(configuration).Build() : null;
            using var connection = source == null ? new OceanBaseConnection(configuration) : (OceanBaseConnection)source.CreateConnection();
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State);
                server.BeforeGreeting = null;
                await Open(connection, asynchronous); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task RepeatedRoundsKeepTheWholeCandidateOrder(bool asynchronous, bool roundRobin)
        {
            using var first = RefusedEndpoint(); using var second = RefusedEndpoint();
            var firstPort = ((IPEndPoint)first.LocalEndPoint!).Port; var secondPort = ((IPEndPoint)second.LocalEndPoint!).Port;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var connection = new OceanBaseConnection(Configuration(firstPort) + $";Server=127.0.0.1:{firstPort},127.0.0.1:{secondPort};Connection Timeout=15;Load Balance={(roundRobin ? "RoundRobin" : "Sequential")}");
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new TcpAttempts(onAttempt: attempt => { if (attempt == 2) { second.Listen(1); listening.TrySetResult(); } });
            var serving = Task.Run(async () =>
            {
                await listening.Task.WaitAsync(stop.Token);
                using var peer = new TcpClient { Client = await second.AcceptAsync(stop.Token), NoDelay = true };
                await Authenticate(peer, false, stop.Token);
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            try
            {
                await Open(connection, asynchronous, stop.Token);
                Assert.Equal(new[] { "error", "error", "error", "success" }, capture.Outcomes);
                connection.Close(); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); stop.Cancel(); second.Dispose(); await ObserveStopped(serving, stop.Token); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExhaustedRoundsAttemptEveryCandidateAndReportTheLastEndpoint(bool asynchronous)
        {
            using var first = RefusedEndpoint(); using var second = RefusedEndpoint();
            var firstPort = ((IPEndPoint)first.LocalEndPoint!).Port; var secondPort = ((IPEndPoint)second.LocalEndPoint!).Port;
            using var connection = new OceanBaseConnection(Configuration(firstPort) + $";Server=127.0.0.1:{firstPort},127.0.0.1:{secondPort};Connection Timeout=0");
            using var capture = new TcpAttempts();
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(Enumerable.Repeat("error", 4), capture.Outcomes);
            Assert.Contains("127.0.0.1:" + secondPort, error.Message);
        }

        private static Socket RefusedEndpoint()
        {
            var endpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            endpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return endpoint;
        }
        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Connect Retry Count=1;Connect Retry Interval=1";
        private static Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token = default) => asynchronous ? connection.OpenAsync(token) : Task.Run(connection.Open, token);
        private static async Task<PacketChannel> Authenticate(TcpClient client, bool oracle, CancellationToken token)
        {
            var channel = new PacketChannel(client.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, token);
            await channel.ReadPacketAsync(token);
            await channel.WritePacketAsync(oracle ? TextCommandTests.TimeZoneOk("+00:00") : new byte[] { 0, 0, 0, 2, 0, 0, 0 }, token);
            await ServerIdentity.ReplyAsync(channel, oracle, token: token); return channel;
        }
        private static async Task ObserveStopped(Task serving, CancellationToken stopped)
        {
            try { await serving; }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        }
        private sealed class Snapshots : IOceanBaseCredentialProvider, IOceanBaseAddressResolver
        {
            private readonly int _port;
            internal int Credentials; internal int Resolutions;
            internal Snapshots(int port) => _port = port;
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token) { Interlocked.Increment(ref Credentials); return new OceanBaseCredential("u", "", 1); }
            public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token) => Task.FromResult(GetCredential(context, token));
            public string Resolve(CancellationToken token) { Interlocked.Increment(ref Resolutions); return "127.0.0.1:" + _port; }
            public Task<string> ResolveAsync(CancellationToken token) => Task.FromResult(Resolve(token));
        }

        private sealed class TcpAttempts : IDisposable
        {
            private readonly MeterListener _listener = new();
            private readonly Activity _scope = new Activity("retry-test").Start();
            private readonly string _id = Guid.NewGuid().ToString("N");
            private readonly List<string> _outcomes = new();
            private readonly List<long> _timestamps = new();
            internal double SecondsBetweenAttempts => (_timestamps[1] - _timestamps[0]) / (double)Stopwatch.Frequency;
            internal string[] Outcomes => _outcomes.ToArray();
            internal TcpAttempts(Action? onFirstFailure = null, Action<int>? onAttempt = null)
            {
                _scope.SetBaggage("retry-test", _id);
                _listener.InstrumentPublished = (instrument, listener) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.connection.phase.duration") listener.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
                {
                    if (Activity.Current?.GetBaggageItem("retry-test") != _id) return;
                    var values = tags.ToArray();
                    if (!values.Any(tag => tag.Key == "oceanbase.connection.phase" && Equals(tag.Value, "tcp"))) return;
                    var outcome = (string)values.Single(tag => tag.Key == "oceanbase.outcome").Value!;
                    _timestamps.Add(Stopwatch.GetTimestamp());
                    _outcomes.Add(outcome); onAttempt?.Invoke(_outcomes.Count);
                    if (_outcomes.Count == 1 && outcome == "error") onFirstFailure?.Invoke();
                });
                _listener.Start();
            }
            public void Dispose() { _listener.Dispose(); _scope.Dispose(); }
        }
    }
}
