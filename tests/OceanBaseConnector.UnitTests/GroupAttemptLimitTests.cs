using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class GroupAttemptLimitTests
    {
        [Theory]
        [InlineData(false, 0, new[] { 2, 2 })]
        [InlineData(true, 0, new[] { 2, 2 })]
        [InlineData(false, 1, new[] { 1, 2, 2 })]
        [InlineData(true, 1, new[] { 1, 2, 2 })]
        [InlineData(false, 2, new[] { 1, 1, 2, 2 })]
        [InlineData(true, 2, new[] { 1, 1, 2, 2 })]
        public void GroupLimitPersistsAcrossEligibleAndFallbackPhases(bool ocp, int cap, int[] expected)
        {
            var snapshot = Read(Text(ocp, cap), ocp);
            Assert.Equal(expected, Attempts(snapshot));
            Assert.Equal("127.0.0.1:1,127.0.0.1:2", snapshot.CanonicalAddresses);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GroupLimitDoesNotResetForAdditionalCycles(bool ocp)
            => Assert.Equal(new[] { 1, 2, 2, 2 }, Attempts(Read(Text(ocp, 1), ocp), retries: 1));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SharedLimitStillCapsAllGroups(bool ocp)
            => Assert.Equal(new[] { 1, 2 }, Attempts(Read(Text(ocp, 1, globalCap: 2), ocp)));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LocalLimitAloneDoesNotEnableBlacklistOrExtraCycles(bool ocp)
        {
            var text = ocp
                ? "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"OBLB_RETRY_ALL_DOWNS\":\"0\",\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"one\",\"PORT\":1}]},{\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"two\",\"PORT\":2}]}]}}}"
                : "APP=(DESCRIPTION=(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=0)(ADDRESS=(PROTOCOL=TCP)(HOST=one)(PORT=1)))(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=two)(PORT=2))))";
            var snapshot = Read(text, ocp);
            Assert.Null(snapshot.BlacklistPolicy); Assert.Null(snapshot.RetryAllDowns);
            Assert.Equal(new[] { 2, 2 }, Attempts(snapshot, retries: 1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExhaustedGroupDoesNotReceivePhantomFailureFeedback(bool ocp)
        {
            var snapshot = Read(Text(ocp, 0), ocp); var health = new EndpointHealthCache();
            Assert.Equal(new[] { 2, 2 }, Attempts(snapshot, health: health));
            Assert.False(health.Blacklist.IsBlocked(snapshot[0], snapshot.CopyGroupPolicies()[0]!));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReloadedLimitsDoNotMutateEarlierSnapshotsOrAttemptCounters(bool ocp)
        {
            using var file = new TnsFileFixture(Text(false, 1)); using var http = new OcpHttpFixture(Text(true, 1));
            IDiscoveryAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            var before = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            file.Write(Text(false, 0)); http.Body = Text(true, 0);
            var after = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            Assert.Equal(new[] { 1, 2, 2 }, Attempts(before));
            Assert.Equal(new[] { 2, 2 }, Attempts(after));
            Assert.Equal(new[] { 1, 2, 2 }, Attempts(before));
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("2147483648")]
        [InlineData("1.0")]
        [InlineData("")]
        [InlineData("1)(OBLB_RETRY_ALL_DOWNS=2")]
        public void InvalidTnsGroupLimitsAreRejected(string value)
            => Assert.Throws<FormatException>(() => Read(Text(false, 1).Replace("(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=1)", "(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=" + value + ")"), false));

        [Theory]
        [InlineData("-1")]
        [InlineData("2147483648")]
        [InlineData("1.0")]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("1,\"OBLB_RETRY_ALL_DOWNS\":2")]
        public void InvalidOcpGroupLimitsAreRejected(string value)
            => Assert.Throws<FormatException>(() => Read(Text(true, 1).Replace("\"OBLB_RETRY_ALL_DOWNS\":1,\"OBLB_BLACKLIST\"", "\"OBLB_RETRY_ALL_DOWNS\":" + value + ",\"OBLB_BLACKLIST\""), true));

        public static IEnumerable<object[]> TransportCases()
        {
            foreach (var ocp in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooled in new[] { false, true })
                        yield return new object[] { ocp, asynchronous, pooled };
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task AllGroupsAtZeroCanReuseHealthyIdleBelowMinimumWithoutTcp(bool ocp, bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            string Tns(int cap) => GroupBlacklistFixture.Tns(server.Port, server.Port, cap: 6).Replace("(ADDRESS_LIST=", "(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=" + cap + ")");
            string Json(int cap) => GroupBlacklistFixture.Json(server.Port, server.Port, cap: 6).Replace("\"ADDRESS\":[", "\"OBLB_RETRY_ALL_DOWNS\":" + cap + ",\"ADDRESS\":[");
            using var file = new TnsFileFixture(Tns(1)); using var http = new OcpHttpFixture(Json(1));
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Min Pool Size=2;Max Pool Size=2;Connection Timeout=10")
            { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            Task Open() { if (asynchronous) return connection.OpenAsync(); connection.Open(); return Task.CompletedTask; }
            using var capture = new TcpAttemptCapture();
            try
            {
                await Open();
                using (var command = connection.CreateCommand()) { command.CommandText = "SET @discard = 1"; Assert.Equal(0, command.ExecuteNonQuery()); }
                connection.Close(); file.Write(Tns(0)); http.Body = Json(0);
                await Open();
                using (var command = connection.CreateCommand()) { command.CommandText = "select retained idle"; Assert.Equal(0, command.ExecuteNonQuery()); }
                Assert.Equal(2, server.Accepted); Assert.Equal(new[] { "success", "success" }, capture.Outcomes);
                connection.Close(); OceanBaseConnection.ClearPool(connection);
                await Assert.ThrowsAsync<OceanBaseException>(Open);
                Assert.Equal(new[] { "success", "success" }, capture.Outcomes);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [MemberData(nameof(TransportCases))]
        public async Task LocalLimitAdvancesToHealthyGroupBeforeThresholdAndPrewarms(bool ocp, bool asynchronous, bool pooled)
        {
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            string Tns() => AddLimit(GroupBlacklistFixture.Tns(port, server.Port, cap: 6), false, 1);
            string Json() => AddLimit(GroupBlacklistFixture.Json(port, server.Port, cap: 6), true, 1);
            using var file = new TnsFileFixture(Tns()); using var http = new OcpHttpFixture(Json());
            using var connection = new OceanBaseConnection($"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling={pooled};Min Pool Size={(pooled ? 2 : 0)};Max Pool Size=2;Connection Timeout=15")
            { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            using var capture = new TcpAttemptCapture();
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(pooled ? new[] { "error", "success", "error", "success" } : new[] { "error", "success" }, capture.Outcomes);
                Assert.Equal(pooled ? 2 : 1, server.Accepted);
                using var command = connection.CreateCommand(); command.CommandText = "select local attempt ceiling";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        private static string AddLimit(string text, bool ocp, int cap) => ocp
            ? text.Replace("\"ADDRESS_LIST\":[{", "\"ADDRESS_LIST\":[{\"OBLB_RETRY_ALL_DOWNS\":" + cap + ",")
            : text.Replace("(ADDRESS_LIST=(OBLB_BLACKLIST", "(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=" + cap + ")(OBLB_BLACKLIST");
        private static string Text(bool ocp, int cap, int globalCap = 6) => AddLimit(ocp
            ? GroupBlacklistFixture.Json(1, 2, cap: globalCap) : GroupBlacklistFixture.Tns(1, 2, cap: globalCap), ocp, cap);
        private static DiscoverySnapshot Read(string text, bool ocp) => ocp
            ? OcpAddressParser.ParseSnapshot(Encoding.UTF8.GetBytes(text), "APP", null, CancellationToken.None)
            : TnsAddressParser.ParseSnapshot(text, "APP", null, CancellationToken.None);
        private static int[] Attempts(DiscoverySnapshot snapshot, int retries = 0, EndpointHealthCache? health = null)
        {
            var plan = new EndpointAttemptPlan(snapshot, new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;Connect Retry Count=" + retries).Build(), new EndpointSelector(), health ?? new EndpointHealthCache());
            var ports = new List<int>();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            return ports.ToArray();
        }
    }
}
