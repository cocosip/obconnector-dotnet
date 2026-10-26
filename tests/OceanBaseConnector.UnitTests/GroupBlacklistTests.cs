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
    public sealed class GroupBlacklistTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GroupOverrideRequeuesWithinItsGroupAndKeepsOneSharedCeiling(bool ocp)
        {
            var snapshot = Read(Text(ocp, cap: 6), ocp);
            Assert.Equal("127.0.0.1:1,127.0.0.1:2", snapshot.CanonicalAddresses);
            Assert.False(snapshot.BlacklistPolicy!.RetryDuration);
            var plan = Plan(snapshot); var ports = new List<int>();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            Assert.Equal(new[] { 1, 1, 2, 1, 2 }, ports);
            plan = Plan(Read(Text(ocp, cap: 2), ocp)); ports.Clear();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            Assert.Equal(new[] { 1, 1 }, ports);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LocalOnlyPolicyDefaultsCeilingWithoutAddingFallbackForPolicyFreeGroups(bool ocp)
        {
            var snapshot = Read(Text(ocp, cap: null, global: false), ocp);
            Assert.Null(snapshot.BlacklistPolicy); Assert.Equal(120, snapshot.RetryAllDowns);
            var plan = Plan(snapshot, retries: 1); var ports = new List<int>();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            Assert.Equal(new[] { 1, 1, 2, 1, 2, 1 }, ports);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EffectiveLocalPolicyHistoryDoesNotPoisonOrClearGlobalHistory(bool ocp)
        {
            var snapshot = Read(Text(ocp, cap: 3), ocp); var health = new EndpointHealthCache();
            health.Blacklist.Failed(snapshot[0], snapshot.BlacklistPolicy!);
            var plan = Plan(snapshot, health: health);
            Assert.True(plan.TryNext(out var endpoint, out _)); Assert.Equal(1, endpoint.Port);
            plan.Succeeded(); Assert.True(health.Blacklist.IsBlocked(snapshot[0], snapshot.BlacklistPolicy!));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EquivalentLocalAndGlobalPoliciesShareEndpointHistory(bool ocp)
        {
            var text = ocp ? GroupBlacklistFixture.Json(1, 1, threshold: 1) : GroupBlacklistFixture.Tns(1, 1, threshold: 1);
            var snapshot = Read(text, ocp); var health = new EndpointHealthCache(); var plan = Plan(snapshot, health: health);
            Assert.True(plan.TryNext(out _, out var delay)); Assert.False(delay); plan.Failed();
            Assert.True(health.Blacklist.IsBlocked(snapshot[1], snapshot.BlacklistPolicy!));
            Assert.True(plan.TryNext(out _, out delay)); Assert.True(delay); // The inherited occurrence was skipped before fallback.
            plan.Succeeded(); Assert.False(health.Blacklist.IsBlocked(snapshot[0], snapshot.BlacklistPolicy!));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void LocalBlockReplacesRatherThanMergesTheGlobalPolicy(bool ocp, bool partial)
        {
            var text = ocp
                ? "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"OBLB_BLACKLIST\":{\"APPEND_STRATEGY\":{\"NAME\":\"RETRYDURATION\",\"RETRYTIMES\":3,\"DURATION\":1000},\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":99}},\"ADDRESS_LIST\":[{\"OBLB_BLACKLIST\":{},\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"one\",\"PORT\":1}]},{\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"two\",\"PORT\":2}]}]}}}"
                : "APP=(DESCRIPTION=(OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=RETRYDURATION)(RETRYTIMES=3)(DURATION=1000))(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=99)))(ADDRESS_LIST=(OBLB_BLACKLIST=)(ADDRESS=(PROTOCOL=TCP)(HOST=one)(PORT=1)))(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=two)(PORT=2))))";
            if (partial) text = ocp ? text.Replace("\"OBLB_BLACKLIST\":{}", "\"OBLB_BLACKLIST\":{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":7}}")
                : text.Replace("(OBLB_BLACKLIST=)", "(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=7)))");
            var snapshot = Read(text, ocp); var policies = snapshot.CopyGroupPolicies();
            Assert.False(policies[0]!.RetryDuration); Assert.Equal(1, policies[0]!.RetryTimes); Assert.Equal(partial ? 7 : 50, policies[0]!.TimeoutSeconds);
            Assert.Same(snapshot.BlacklistPolicy, policies[1]); Assert.True(policies[1]!.RetryDuration); Assert.Equal(99, policies[1]!.TimeoutSeconds);
            var plan = Plan(snapshot); Assert.True(plan.TryNext(out _, out _)); plan.Failed();
            Assert.True(plan.TryNext(out var endpoint, out _)); Assert.Equal(2, endpoint.Port);
        }

        [Theory]
        [InlineData("(OBLB_BLACKLIST=)(oblb_blacklist=)", true)]
        [InlineData("(OBLB_BLACKLIST=ON)", true)]
        [InlineData("(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=-1)))", true)]
        [InlineData("(OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=RETRYDURATION)(RETRYTIMES=2)))", true)]
        [InlineData("(OBLB_BLACKLIST=(UNKNOWN=1))", false)]
        [InlineData("(OBLB_RETRY_ALL_DOWNS=1)", false)]
        public void InvalidGroupPoliciesAndLocalAttemptCeilingsFailExplicitly(string policy, bool malformed)
        {
            var text = "APP=(DESCRIPTION=(ADDRESS_LIST=" + policy + "(ADDRESS=(PROTOCOL=TCP)(HOST=one)(PORT=1))))";
            if (malformed) Assert.Throws<FormatException>(() => Read(text, false));
            else Assert.Throws<NotSupportedException>(() => Read(text, false));
        }

        [Theory]
        [InlineData("\"OBLB_BLACKLIST\":{},\"OBLB_BLACKLIST\":{}", true)]
        [InlineData("\"OBLB_BLACKLIST\":true", true)]
        [InlineData("\"OBLB_BLACKLIST\":{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":-1}}", true)]
        [InlineData("\"OBLB_BLACKLIST\":{\"APPEND_STRATEGY\":{\"NAME\":\"RETRYDURATION\",\"RETRYTIMES\":2}}", true)]
        [InlineData("\"OBLB_BLACKLIST\":{\"UNKNOWN\":1}", false)]
        [InlineData("\"OBLB_RETRY_ALL_DOWNS\":1", false)]
        public void InvalidOcpGroupPoliciesAndLocalAttemptCeilingsFailExplicitly(string policy, bool malformed)
        {
            var text = "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{" + policy + ",\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"one\",\"PORT\":1}]}]}}}";
            if (malformed) Assert.Throws<FormatException>(() => Read(text, true));
            else Assert.Throws<NotSupportedException>(() => Read(text, true));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReloadedPolicyDoesNotMutateAnEarlierSnapshotOrItsFeedback(bool ocp)
        {
            using var file = new TnsFileFixture(GroupBlacklistFixture.Tns(1, 2));
            using var http = new OcpHttpFixture(GroupBlacklistFixture.Json(1, 2));
            IDiscoveryAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            var before = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            var health = new EndpointHealthCache(); var pending = Plan(before, health: health);
            Assert.True(pending.TryNext(out _, out _)); pending.Failed();
            file.Write(GroupBlacklistFixture.Tns(1, 2, threshold: 1)); http.Body = GroupBlacklistFixture.Json(1, 2, threshold: 1);
            var after = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            Assert.Equal(2, before.CopyGroupPolicies()[0]!.RetryTimes); Assert.Equal(1, after.CopyGroupPolicies()[0]!.RetryTimes);
            var updated = Plan(after, health: health); Assert.True(updated.TryNext(out var endpoint, out _)); Assert.Equal(1, endpoint.Port);
            updated.Failed(); Assert.True(updated.TryNext(out endpoint, out _)); Assert.Equal(2, endpoint.Port);
            Assert.True(pending.TryNext(out endpoint, out _)); Assert.Equal(1, endpoint.Port);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MalformedLocalPolicyFailsBeforeIdleReuseAndValidReloadRecovers(bool ocp, bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            var tns = GroupBlacklistFixture.Tns(server.Port, server.Port, threshold: 1);
            var json = GroupBlacklistFixture.Json(server.Port, server.Port, threshold: 1);
            using var file = new TnsFileFixture(tns); using var http = new OcpHttpFixture(json);
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1")
            { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            Task Open() { if (asynchronous) return connection.OpenAsync(); connection.Open(); return Task.CompletedTask; }
            try
            {
                await Open(); connection.Close(); Assert.Equal(1, server.Accepted);
                file.Write(tns.Replace("(ADDRESS_LIST=", "(ADDRESS_LIST=(OBLB_BLACKLIST=ON)"));
                http.Body = json.Replace("\"ADDRESS_LIST\":[{", "\"ADDRESS_LIST\":[{\"OBLB_BLACKLIST\":true,");
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open()); Assert.IsType<FormatException>(error.InnerException);
                Assert.Equal(1, server.Accepted);
                file.Write(tns); http.Body = json; await Open();
                using var command = connection.CreateCommand(); command.CommandText = "select after local policy repair";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); Assert.Equal(1, server.Accepted);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        public static IEnumerable<object[]> TransportCases()
        {
            foreach (var ocp in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooled in new[] { false, true })
                        yield return new object[] { ocp, asynchronous, pooled };
        }

        [Theory]
        [MemberData(nameof(TransportCases))]
        public async Task LocalThresholdReachesPhysicalAttemptsAndPoolWarmup(bool ocp, bool asynchronous, bool pooled)
        {
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(GroupBlacklistFixture.Tns(port, server.Port));
            using var http = new OcpHttpFixture(GroupBlacklistFixture.Json(port, server.Port));
            using var connection = new OceanBaseConnection($"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling={pooled};Min Pool Size={(pooled ? 2 : 0)};Max Pool Size=2;Connection Timeout=15")
            { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            using var capture = new TcpAttemptCapture();
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(pooled ? new[] { "error", "error", "success", "success" } : new[] { "error", "error", "success" }, capture.Outcomes);
                Assert.Equal(pooled ? 2 : 1, server.Accepted);
                using var command = connection.CreateCommand(); command.CommandText = "select group blacklist";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        private static DiscoverySnapshot Read(string text, bool ocp) => ocp
            ? OcpAddressParser.ParseSnapshot(Encoding.UTF8.GetBytes(text), "APP", null, CancellationToken.None)
            : TnsAddressParser.ParseSnapshot(text, "APP", null, CancellationToken.None);
        private static string Text(bool ocp, int? cap = 3, bool global = true) => ocp
            ? GroupBlacklistFixture.Json(1, 2, cap, global: global) : GroupBlacklistFixture.Tns(1, 2, cap, global: global);
        private static EndpointAttemptPlan Plan(DiscoverySnapshot snapshot, int retries = 0, EndpointHealthCache? health = null) =>
            new EndpointAttemptPlan(snapshot, new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;TLS Mode=Disabled;Connect Retry Count=" + retries).Build(), new EndpointSelector(), health ?? new EndpointHealthCache());
    }
}
