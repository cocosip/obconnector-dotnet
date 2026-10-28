using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed partial class EndpointHealthTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(3600)]
        public void HostRecheckBoundsSerializeWithoutSecrets(int seconds)
        {
            var builder = new OceanBaseConnectionStringBuilder($"Server=localhost;User ID=u;Password=secret;Host Recheck Seconds={seconds}");
            var serialized = builder.ToSanitizedConnectionString(false);
            Assert.DoesNotContain("secret", serialized);
            Assert.Equal(builder["Host Recheck Seconds"], new OceanBaseConnectionStringBuilder(serialized)["Host Recheck Seconds"]);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task RepeatedOpenPrefersReachableEndpointWithinItsOwnPoolOrConnection(bool asynchronous, bool oracle, int path)
        {
            using var refused = Refused(); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            var pooled = path == 1 || path == 2 || path >= 4;
            var configuration = $"Server=127.0.0.1:{port},127.0.0.1:{server.Port};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Pooling={pooled};Max Pool Size=2;Min Pool Size={(path >= 4 ? 2 : 0)};Host Recheck Seconds=60";
            using var source = path == 2 || path == 3 || path == 5 ? new OceanBaseDataSourceBuilder(configuration).Build() : null;
            using var first = source == null ? new OceanBaseConnection(configuration) : (OceanBaseConnection)source.CreateConnection();
            using var second = path == 0 ? first : source == null ? new OceanBaseConnection(configuration) : (OceanBaseConnection)source.CreateConnection();
            using var capture = new TcpAttemptCapture();
            try
            {
                await Open(first, asynchronous);
                if (!pooled) first.Close();
                await Open(second, asynchronous);
                Assert.Equal(ConnectionState.Open, second.State);
                Assert.Equal(new[] { "error", "success", "success" }, capture.Outcomes);
                Assert.Equal(2, server.Accepted);
                using var command = second.CreateCommand(); command.CommandText = "cache selected";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally
            {
                second.Close(); first.Close();
                if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(first);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExpiryRestoresConfiguredOrderAndZeroRetainsExistingBehavior(bool asynchronous)
        {
            using var refused = Refused();
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            var configuration = $"Server=127.0.0.1:{((IPEndPoint)refused.LocalEndPoint!).Port},127.0.0.1:{server.Port};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Host Recheck Seconds=1";
            using var connection = new OceanBaseConnection(configuration);
            using var capture = new TcpAttemptCapture();
            await Open(connection, asynchronous); connection.Close();
            await Open(connection, asynchronous); connection.Close();
            Assert.Equal(new[] { "error", "success", "success" }, capture.Outcomes);
            await Task.Delay(1100);
            await Open(connection, asynchronous); connection.Close();
            Assert.Equal(new[] { "error", "success", "success", "error", "success" }, capture.Outcomes);
            connection.ConnectionString = configuration + ";Host Recheck Seconds=0";
            await Open(connection, asynchronous); connection.Close();
            await Open(connection, asynchronous); connection.Close();
            Assert.Equal(new[] { "error", "success", "success", "error", "success", "error", "success", "error", "success" }, capture.Outcomes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AllFailedCandidatesRemainAvailableAndFinalFailureIsRemembered(bool asynchronous)
        {
            using var first = Refused(); using var last = Refused();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var connection = new OceanBaseConnection($"Server=127.0.0.1:{((IPEndPoint)first.LocalEndPoint!).Port},127.0.0.1:{((IPEndPoint)last.LocalEndPoint!).Port};User ID=u;TLS Mode=Disabled;Host Recheck Seconds=60;Connection Timeout=8");
            using var capture = new TcpAttemptCapture();
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(new[] { "error", "error" }, capture.Outcomes);
            first.Listen(1);
            var serving = ServeOne(first, stop.Token);
            try
            {
                await Open(connection, asynchronous, stop.Token);
                Assert.Equal(new[] { "error", "error", "success" }, capture.Outcomes);
                connection.Close(); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); stop.Cancel(); first.Dispose(); await Observe(serving, stop.Token); }
        }

        [Fact]
        public async Task CancellationAfterTcpFailureDoesNotPoisonTheNextOpen()
        {
            using var endpoint = Refused();
            await using var fallback = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var caller = new CancellationTokenSource();
            using var connection = new OceanBaseConnection($"Server=127.0.0.1:{((IPEndPoint)endpoint.LocalEndPoint!).Port},127.0.0.1:{fallback.Port};User ID=u;TLS Mode=Disabled;Host Recheck Seconds=60;Connection Timeout=8");
            using var capture = new TcpAttemptCapture((count, outcome) => { if (count == 1 && outcome == "error") { endpoint.Listen(1); caller.Cancel(); } });
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.OpenAsync(caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
            var serving = ServeOne(endpoint, stop.Token);
            try
            {
                await connection.OpenAsync(stop.Token);
                Assert.Equal(new[] { "error", "success" }, capture.Outcomes);
                Assert.Equal(0, fallback.Accepted);
                connection.Close(); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); stop.Cancel(); endpoint.Dispose(); await Observe(serving, stop.Token); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ClearDoesNotAcceptOldAttemptFeedbackOrInterruptItsBorrower(bool asynchronous, bool dataSource)
        {
            using var recovered = Refused();
            await using var fallback = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var configuration = $"Server=127.0.0.1:{((IPEndPoint)recovered.LocalEndPoint!).Port},127.0.0.1:{fallback.Port};User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=2;Host Recheck Seconds=60;Connection Timeout=8";
            using var source = dataSource ? new OceanBaseDataSourceBuilder(configuration).Build() : null;
            using var old = source == null ? new OceanBaseConnection(configuration) : (OceanBaseConnection)source.CreateConnection();
            using var next = source == null ? new OceanBaseConnection(configuration) : (OceanBaseConnection)source.CreateConnection();
            var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new TcpAttemptCapture((count, outcome) =>
            {
                if (count != 1 || outcome != "error") return;
                if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(old);
                recovered.Listen(1);
            });
            var serving = Task.Run(async () =>
            {
                // Accept only after the failure callback starts listening.
                await firstFailure.Task.WaitAsync(stop.Token);
                await ServeOne(recovered, stop.Token, pooled: true);
            });
            try
            {
                await Open(old, asynchronous, stop.Token);
                firstFailure.TrySetResult();
                Assert.Equal(ConnectionState.Open, old.State);
                await Open(next, asynchronous, stop.Token);
                Assert.Equal(new[] { "error", "success", "success" }, capture.Outcomes);
                Assert.Equal(1, fallback.Accepted);
                using var command = old.CreateCommand(); command.CommandText = "old borrower still works";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync(stop.Token) : command.ExecuteNonQuery());
                next.Close(); old.Close();
                if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(next);
                await serving.WaitAsync(stop.Token);
            }
            finally { next.Close(); old.Close(); if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(next); stop.Cancel(); recovered.Dispose(); firstFailure.TrySetCanceled(stop.Token); await Observe(serving, stop.Token); }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        public async Task SeparatePoliciesSourcesAndDirectConnectionsDoNotShareHealth(bool asynchronous, int scope)
        {
            using var refused = Refused();
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            var addresses = $"127.0.0.1:{((IPEndPoint)refused.LocalEndPoint!).Port},127.0.0.1:{server.Port}";
            var configuration = $"Server={addresses};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Pooling={scope != 3};Max Pool Size=2;Host Recheck Seconds=60";
            using var firstSource = scope == 2 ? new OceanBaseDataSourceBuilder(configuration).Build() : null;
            using var nextSource = scope == 2 ? new OceanBaseDataSourceBuilder(configuration).Build() : null;
            using var first = firstSource == null ? new OceanBaseConnection(configuration) : (OceanBaseConnection)firstSource.CreateConnection();
            using var next = nextSource == null ? new OceanBaseConnection(configuration + (scope == 0 ? ";Host Recheck Seconds=30" : "")) : (OceanBaseConnection)nextSource.CreateConnection();
            if (scope == 1) { first.AddressResolver = new Resolver(addresses); next.AddressResolver = new Resolver(addresses); }
            using var capture = new TcpAttemptCapture();
            try
            {
                await Open(first, asynchronous);
                if (scope == 3) first.Close();
                await Open(next, asynchronous);
                Assert.Equal(new[] { "error", "success", "error", "success" }, capture.Outcomes);
                Assert.Equal(2, server.Accepted);
            }
            finally
            {
                next.Close(); first.Close();
                if (firstSource != null) firstSource.Clear(); else OceanBaseConnection.ClearPool(first);
                if (nextSource != null) nextSource.Clear(); else OceanBaseConnection.ClearPool(next);
            }
        }

        [Fact]
        public async Task ElapsedDeadlineBeforeTcpFailureFeedbackDoesNotPoisonTheNextOpen()
        {
            using var endpoint = Refused();
            await using var fallback = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new OceanBaseConnection($"Server=127.0.0.1:{((IPEndPoint)endpoint.LocalEndPoint!).Port},127.0.0.1:{fallback.Port};User ID=u;TLS Mode=Disabled;Host Recheck Seconds=60;Connection Timeout=1");
            using var capture = new TcpAttemptCapture((count, _) =>
            {
                if (count != 1) return;
                endpoint.Listen(1);
                // Deliver feedback only after the total deadline, including on fast-refusal platforms.
                Thread.Sleep(1200);
            });
            await Assert.ThrowsAsync<TimeoutException>(() => connection.OpenAsync(stop.Token));
            var serving = ServeOne(endpoint, stop.Token);
            try
            {
                await connection.OpenAsync(stop.Token);
                Assert.Equal(2, capture.Outcomes.Length); Assert.Equal("success", capture.Outcomes[1]);
                Assert.Equal(0, fallback.Accepted); connection.Close(); await serving.WaitAsync(stop.Token);
            }
            finally { connection.Close(); stop.Cancel(); endpoint.Dispose(); await Observe(serving, stop.Token); }
        }
        public static IEnumerable<object[]> Paths()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var oracle in new[] { false, true })
                    foreach (var path in new[] { 0, 1, 2, 3, 4, 5 }) yield return new object[] { asynchronous, oracle, path };
        }

        private sealed class Resolver : IOceanBaseAddressResolver
        {
            private readonly string _addresses;
            internal Resolver(string addresses) => _addresses = addresses;
            public string Resolve(CancellationToken token) => _addresses;
            public Task<string> ResolveAsync(CancellationToken token) => Task.FromResult(_addresses);
        }

        private static async Task ServeOne(Socket endpoint, CancellationToken token, bool pooled = false)
        {
            using var peer = new TcpClient { Client = await endpoint.AcceptAsync(token), NoDelay = true };
            var channel = new PacketChannel(peer.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, token);
            await channel.ReadPacketAsync(token);
            await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, token);
            await ServerIdentity.ReplyAsync(channel, false, token: token);
            if (pooled)
            {
                Assert.Equal(31, (await channel.ReadPacketAsync(token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, token);
            }
            Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], token));
        }
        private static Socket Refused()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket;
        }
    }
}
