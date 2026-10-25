using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DiscoveryBlacklistTests
    {
        public static IEnumerable<object[]> BudgetPaths()
        {
            foreach (var ocp in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooled in new[] { false, true })
                        foreach (var cap in new[] { 0, 1, 2 }) yield return new object[] { ocp, asynchronous, pooled, cap };
        }

        [Theory]
        [MemberData(nameof(BudgetPaths))]
        public async Task DiscoveryBudgetCountsTcpAttemptsAcrossGroups(bool ocp, bool asynchronous, bool pooled, int cap)
        {
            using var refused = Refused(); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(port, server.Port, cap));
            using var http = new OcpHttpFixture(DiscoveryBlacklistFixture.Json(port, server.Port, cap));
            IOceanBaseAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            Assert.Equal($"127.0.0.1:{port},127.0.0.1:{server.Port}", await resolver.ResolveAsync(CancellationToken.None));
            using var connection = new OceanBaseConnection($"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling={pooled};Connection Timeout=15;Connect Retry Count=2") { AddressResolver = resolver };
            using var capture = new TcpAttemptCapture();
            try
            {
                if (cap < 2) await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                else await Open(connection, asynchronous);
                Assert.Equal(cap, capture.Outcomes.Length);
                Assert.Equal(cap == 2 ? 1 : 0, server.Accepted);
                Assert.Equal(ocp ? 2 : 0, http.Requests.Count);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        public static IEnumerable<object[]> CachePaths()
        {
            foreach (var ocp in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooled in new[] { false, true })
                        foreach (var timeout in new[] { 0, 50 }) yield return new object[] { ocp, asynchronous, pooled, timeout };
        }

        [Theory]
        [MemberData(nameof(CachePaths))]
        public async Task BlacklistSkipsFailedGroupUntilExpiryWithinItsPoolGeneration(bool ocp, bool asynchronous, bool pooled, int timeout)
        {
            using var refused = Refused(); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(port, server.Port, timeout: timeout));
            using var http = new OcpHttpFixture(DiscoveryBlacklistFixture.Json(port, server.Port, timeout: timeout));
            IOceanBaseAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            var config = $"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=2;Connection Timeout=15";
            using var first = new OceanBaseConnection(config) { AddressResolver = resolver };
            using var second = pooled ? new OceanBaseConnection(config) { AddressResolver = resolver } : first;
            using var capture = new TcpAttemptCapture();
            try
            {
                await Open(first, asynchronous); if (!pooled) first.Close();
                await Open(second, asynchronous);
                Assert.Equal(timeout == 0 ? new[] { "error", "success", "error", "success" } : new[] { "error", "success", "success" }, capture.Outcomes);
                Assert.Equal(2, server.Accepted);
            }
            finally { second.Close(); first.Close(); OceanBaseConnection.ClearPool(first); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task RetryDurationRequeuesCurrentGroupBeforeAdvancing(bool ocp, bool asynchronous)
        {
            using var refused = Refused(); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryBlacklistFixture.Tns(port, server.Port, cap: 3, threshold: 2));
            using var http = new OcpHttpFixture(DiscoveryBlacklistFixture.Json(port, server.Port, cap: 3, threshold: 2));
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Connection Timeout=15") { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            using var capture = new TcpAttemptCapture();
            await Open(connection, asynchronous);
            Assert.Equal(new[] { "error", "error", "success" }, capture.Outcomes);
        }

        private static Socket Refused()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket;
        }
        private static Task Open(OceanBaseConnection connection, bool asynchronous)
        {
            if (asynchronous) return connection.OpenAsync();
            connection.Open(); return Task.CompletedTask;
        }
    }
}
