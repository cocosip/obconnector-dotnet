using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class DiscoveryBlacklistLifecycleTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ZeroBudgetCanReuseIdleButMalformedPolicyIsValidatedBeforeReuse(bool ocp, bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(server.Port, server.Port, 1));
            using var http = new OcpHttpFixture(DiscoveryBlacklistFixture.Json(server.Port, server.Port, 1));
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Min Pool Size=2;Max Pool Size=2;Connection Timeout=10") { AddressResolver = Resolver(ocp, file, http) };
            using var capture = new TcpAttemptCapture();
            void Update(int cap) { file.Write(DiscoveryBlacklistFixture.Tns(server.Port, server.Port, cap)); http.Body = DiscoveryBlacklistFixture.Json(server.Port, server.Port, cap); }
            try
            {
                await Open(connection, asynchronous);
                using (var dirty = connection.CreateCommand()) { dirty.CommandText = "SET @discard = 1"; Assert.Equal(0, await dirty.ExecuteNonQueryAsync()); }
                connection.Close(); Update(0);
                await Open(connection, asynchronous); connection.Close(); Assert.Equal(2, server.Accepted);
                file.Write(DiscoveryBlacklistFixture.Tns(server.Port, server.Port, 0).Replace("NAME=NORMAL", "NAME=BAD"));
                http.Body = DiscoveryBlacklistFixture.Json(server.Port, server.Port, 0).Replace("\"NAME\":\"NORMAL\"", "\"NAME\":\"BAD\"");
                var malformed = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.IsType<NotSupportedException>(malformed.InnerException);
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(new[] { "success", "success" }, capture.Outcomes);
                Update(0); await Open(connection, asynchronous); connection.Close();
                OceanBaseConnection.ClearPool(connection);
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(2, server.Accepted); Assert.Equal(new[] { "success", "success" }, capture.Outcomes);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ClearInsideTcpFailureFeedbackCannotBlacklistTheNewPoolGeneration(bool asynchronous, bool dataSource)
        {
            using var refused = Refused(); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(port, server.Port, 2));
            var resolver = new OceanBaseTnsAddressResolver(file.Path, "APP");
            const string config = "Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=2;Connection Timeout=15";
            using var source = dataSource ? new OceanBaseDataSourceBuilder(config).UseAddressResolver(resolver).Build() : null;
            using var old = source == null ? new OceanBaseConnection(config) { AddressResolver = resolver } : (OceanBaseConnection)source.CreateConnection();
            using var fresh = source == null ? new OceanBaseConnection(config) { AddressResolver = resolver } : (OceanBaseConnection)source.CreateConnection();
            void Clear() { if (source != null) source.Clear(); else OceanBaseConnection.ClearPool(old); }
            using var capture = new TcpAttemptCapture((count, outcome) => { if (count == 1 && outcome == "error") Clear(); });
            try
            {
                await Open(old, asynchronous); await Open(fresh, asynchronous);
                Assert.Equal(new[] { "error", "success", "error", "success" }, capture.Outcomes);
                using var command = old.CreateCommand(); command.CommandText = "old borrower remains valid";
                Assert.Equal(0, await command.ExecuteNonQueryAsync()); Assert.Equal(2, server.Accepted);
            }
            finally { old.Close(); fresh.Close(); Clear(); }
        }

        [Theory]
        [InlineData(true, "cancel")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        public async Task DiscoveryFallbackBackoffHonorsCancellationCloseAndTotalDeadline(bool asynchronous, string ending)
        {
            using var refused = Refused(); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(port, port, 4));
            using var connection = new OceanBaseConnection($"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Connection Timeout={(ending == "timeout" ? 4 : 15)};Connect Retry Interval=60") { AddressResolver = new OceanBaseTnsAddressResolver(file.Path, "APP") };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var caller = new CancellationTokenSource();
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new TcpAttemptCapture((count, _) => { if (count == 1) attempted.TrySetResult(); });
            try
            {
                var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
                await attempted.Task.WaitAsync(stop.Token); await Task.Delay(100, stop.Token); Assert.False(opening.IsCompleted);
                if (ending == "close") connection.Close(); else if (ending == "cancel") caller.Cancel();
                if (ending == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(stop.Token));
                else
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(stop.Token));
                    if (ending == "cancel") Assert.Equal(caller.Token, error.CancellationToken);
                }
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(new[] { "error" }, capture.Outcomes);
                file.Write(DiscoveryBlacklistFixture.Tns(server.Port, server.Port, 1));
                await Open(connection, asynchronous); Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(new[] { "error", "success" }, capture.Outcomes);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }
        private static IOceanBaseAddressResolver Resolver(bool ocp, TnsFileFixture file, OcpHttpFixture http) => ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
        private static Socket Refused() { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket; }
    }
}
