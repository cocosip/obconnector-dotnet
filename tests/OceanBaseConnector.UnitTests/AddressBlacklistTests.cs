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
    public sealed class AddressBlacklistTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AddressBlockOverridesItsGroupWithoutChangingOtherGroups(bool ocp)
            => Assert.Equal(new[] { 1, 1, 1, 2, 1, 2 }, Attempts(Read(Text(ocp), ocp)));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AddressOverrideStillObeysItsGroupAttemptLimit(bool ocp)
        {
            var text = Text(ocp);
            text = ocp ? text.Replace("\"ADDRESS_LIST\":[{", "\"ADDRESS_LIST\":[{\"OBLB_RETRY_ALL_DOWNS\":2,")
                : text.Replace("(ADDRESS_LIST=(OBLB_BLACKLIST", "(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=2)(OBLB_BLACKLIST");
            Assert.Equal(new[] { 1, 1, 2, 2 }, Attempts(Read(text, ocp)));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void EmptyAndPartialAddressBlocksReplaceTheWholeInheritedPolicy(bool ocp, bool partial)
        {
            var text = Text(ocp);
            text = ocp ? text.Replace(JsonPolicy(3), partial ? "{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":7}}" : "{}")
                : text.Replace(Policy(3), partial ? "(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=7)))" : "(OBLB_BLACKLIST=)");
            Assert.Equal(new[] { 1, 2, 1, 2 }, Attempts(Read(text, ocp)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AddressOnlyBlacklistAddsFallbackOnlyForThatAddress(bool ocp)
        {
            var text = ocp
                ? "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"ADDRESS\":[" + JsonAddress(1, JsonPolicy(1)) + "," + JsonAddress(2) + "]}]}}}"
                : "APP=(DESCRIPTION=(ADDRESS_LIST=" + Address(1, Policy(1)) + Address(2) + "))";
            var snapshot = Read(text, ocp);
            Assert.Null(snapshot.BlacklistPolicy); Assert.Equal(120, snapshot.RetryAllDowns);
            Assert.Equal(new[] { 1, 2, 1 }, Attempts(snapshot));
        }

        [Fact]
        public void DuplicateEndpointOccurrencesKeepDifferentEffectivePoliciesAndFeedback()
        {
            var snapshot = Read("APP=(DESCRIPTION=(ADDRESS_LIST=" + Address(1, Policy(1)) + Address(1, Policy(3)) + "))", false);
            var health = new EndpointHealthCache(); var plan = Plan(snapshot, health: health);
            Assert.True(plan.TryNext(out var first, out _)); Assert.Same(snapshot[0], first); plan.Failed();
            Assert.True(health.Blacklist.IsBlocked(first, new DiscoveryBlacklistPolicy()));
            Assert.True(plan.TryNext(out var second, out _)); Assert.Same(snapshot[1], second); plan.Failed();
            Assert.False(health.Blacklist.IsBlocked(second, new DiscoveryBlacklistPolicy(true, 3, int.MaxValue)));
            Assert.True(plan.TryNext(out var repeated, out _)); Assert.Same(second, repeated);
            plan.Succeeded(); Assert.True(health.Blacklist.IsBlocked(first, new DiscoveryBlacklistPolicy()));
        }

        [Theory]
        [InlineData(false, new[] { 2, 1, 1, 1, 2, 1 })]
        [InlineData(true, new[] { 1, 2, 1, 1, 1, 2 })]
        public void WeightedAndHealthOrderingMoveThePolicyWithItsAddress(bool healthFirst, int[] expected)
        {
            var snapshot = Read("APP=(DESCRIPTION=(OBLB_STRATEGY=SERVERAFFINITY)(OBLB_RETRY_ALL_DOWNS=6)(ADDRESS_LIST=" +
                Address(1, Policy(3), 0) + Address(2, "", 1) + "))", false);
            var health = new EndpointHealthCache(); if (healthFirst) health.Failed(snapshot[1], 60);
            Assert.Equal(expected, Attempts(snapshot, health, healthFirst ? 60 : 0));
        }

        [Theory]
        [InlineData("(OBLB_BLACKLIST=ON)", true)]
        [InlineData("(OBLB_BLACKLIST=)(OBLB_BLACKLIST=)", true)]
        [InlineData("(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=-1)))", true)]
        [InlineData("(OBLB_BLACKLIST=(UNKNOWN=1))", false)]
        [InlineData("(OBLB_RETRY_ALL_DOWNS=1)", false)]
        public void MalformedOrUnsupportedAddressOptionsFailBeforeNetworking(string policy, bool malformed)
        {
            var text = "APP=(DESCRIPTION=(ADDRESS_LIST=" + Address(1, policy) + "))";
            if (malformed) Assert.Throws<FormatException>(() => Read(text, false));
            else Assert.Throws<NotSupportedException>(() => Read(text, false));
        }

        [Theory]
        [InlineData("null", true)]
        [InlineData("true", true)]
        [InlineData("{\"REMOVE_STRATEGY\":{\"NAME\":\"TIMEOUT\",\"TIMEOUT\":-1}}", true)]
        [InlineData("{},\"OBLB_BLACKLIST\":{}", true)]
        [InlineData("{\"UNKNOWN\":1}", false)]
        public void InvalidOcpAddressBlocksFailBeforeNetworking(string policy, bool malformed)
        {
            var text = "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"ADDRESS\":[" + JsonAddress(1, policy) + "]}]}}}";
            if (malformed) Assert.Throws<FormatException>(() => Read(text, true));
            else Assert.Throws<NotSupportedException>(() => Read(text, true));
        }

        [Fact]
        public void EquivalentAddressAndInheritedPoliciesShareOnlyMatchingHistory()
        {
            var snapshot = Read("APP=(DESCRIPTION=(OBLB_BLACKLIST=)(ADDRESS_LIST=" + Address(1, Policy(1)) + Address(1) + "))", false);
            var health = new EndpointHealthCache(); var plan = Plan(snapshot, health);
            Assert.True(plan.TryNext(out var first, out var delay)); Assert.False(delay); plan.Failed();
            Assert.True(health.Blacklist.IsBlocked(snapshot[1], snapshot.BlacklistPolicy!));
            Assert.True(plan.TryNext(out _, out delay)); Assert.True(delay);
            plan.Succeeded(); Assert.False(health.Blacklist.IsBlocked(first, snapshot.BlacklistPolicy!));
        }

        [Fact]
        public void RoundRobinMovesEachOccurrenceWithItsPolicy()
        {
            var snapshot = Read("APP=(DESCRIPTION=(ADDRESS_LIST=" + Address(1, Policy(3)) + Address(2, Policy(1)) + "))", false);
            var selector = new EndpointSelector(); var options = new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;Load Balance=RoundRobin").Build();
            int[] Run()
            {
                var plan = new EndpointAttemptPlan(snapshot, options, selector, new EndpointHealthCache()); var ports = new List<int>();
                while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
                return ports.ToArray();
            }
            Assert.Equal(new[] { 1, 2, 1, 1, 1, 2 }, Run());
            Assert.Equal(new[] { 2, 1, 1, 1, 2, 1 }, Run());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReloadDoesNotMutateAnEarlierAddressPolicySnapshot(bool ocp)
        {
            using var file = new TnsFileFixture(Text(false)); using var http = new OcpHttpFixture(Text(true));
            IDiscoveryAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            var before = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            file.Write(Text(false, threshold: 1)); http.Body = Text(true, threshold: 1);
            var after = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            Assert.Equal(new[] { 1, 1, 1, 2, 1, 2 }, Attempts(before));
            Assert.Equal(new[] { 1, 2, 1, 2 }, Attempts(after));
        }

        public static IEnumerable<object[]> TransportCases()
        {
            foreach (var ocp in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooled in new[] { false, true }) yield return new object[] { ocp, asynchronous, pooled };
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MalformedAddressPolicyFailsBeforeIdleReuseAndValidReloadRecovers(bool ocp, bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            var tns = Text(false, server.Port, server.Port); var json = Text(true, server.Port, server.Port);
            using var file = new TnsFileFixture(tns); using var http = new OcpHttpFixture(json);
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1")
            { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            Task Open() { if (asynchronous) return connection.OpenAsync(); connection.Open(); return Task.CompletedTask; }
            try
            {
                await Open(); connection.Close(); Assert.Equal(1, server.Accepted);
                file.Write(tns.Replace(Policy(3), "(OBLB_BLACKLIST=ON)"));
                http.Body = json.Replace(JsonPolicy(3), "true");
                var error = await Assert.ThrowsAsync<OceanBaseException>(Open); Assert.IsType<FormatException>(error.InnerException);
                Assert.Equal(1, server.Accepted);
                file.Write(tns); http.Body = json; await Open();
                using var command = connection.CreateCommand(); command.CommandText = "select after address policy repair";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); Assert.Equal(1, server.Accepted);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [MemberData(nameof(TransportCases))]
        public async Task EffectiveAddressThresholdControlsTcpAndPoolWarmup(bool ocp, bool asynchronous, bool pooled)
        {
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(Text(false, port, server.Port)); using var http = new OcpHttpFixture(Text(true, port, server.Port));
            using var connection = new OceanBaseConnection($"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling={pooled};Min Pool Size={(pooled ? 2 : 0)};Max Pool Size=2;Connection Timeout=15")
            { AddressResolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP") };
            using var capture = new TcpAttemptCapture();
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(pooled ? new[] { "error", "error", "error", "success", "success" } : new[] { "error", "error", "error", "success" }, capture.Outcomes);
                using var command = connection.CreateCommand(); command.CommandText = "select address policy";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        private static string Text(bool ocp, int first = 1, int second = 2, int threshold = 3) => ocp
            ? GroupBlacklistFixture.Json(first, second, cap: 6).Replace("\"PORT\":" + first + "}", "\"PORT\":" + first + ",\"OBLB_BLACKLIST\":" + JsonPolicy(threshold) + "}")
            : GroupBlacklistFixture.Tns(first, second, cap: 6).Replace("(PORT=" + first + "))", "(PORT=" + first + ")" + Policy(threshold) + ")");
        private static string Policy(int threshold) => "(OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=" + (threshold == 1 ? "NORMAL" : "RETRYDURATION") + ")" + (threshold == 1 ? "" : "(RETRYTIMES=" + threshold + ")(DURATION=2147483647)") + "))";
        private static string JsonPolicy(int threshold) => threshold == 1 ? "{}" : "{\"APPEND_STRATEGY\":{\"NAME\":\"RETRYDURATION\",\"RETRYTIMES\":" + threshold + ",\"DURATION\":2147483647}}";
        private static string Address(int port, string policy = "", int? weight = null) => "(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + port + ")" + policy + (weight.HasValue ? "(WEIGHT=" + weight + ")" : "") + ")";
        private static string JsonAddress(int port, string? policy = null) => "{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":" + port + (policy == null ? "" : ",\"OBLB_BLACKLIST\":" + policy) + "}";
        private static DiscoverySnapshot Read(string text, bool ocp) => ocp ? OcpAddressParser.ParseSnapshot(Encoding.UTF8.GetBytes(text), "APP", null, CancellationToken.None) : TnsAddressParser.ParseSnapshot(text, "APP", null, CancellationToken.None);
        private static EndpointAttemptPlan Plan(DiscoverySnapshot snapshot, EndpointHealthCache? health = null, int healthSeconds = 0) => new EndpointAttemptPlan(snapshot, new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;Host Recheck Seconds=" + healthSeconds).Build(), new EndpointSelector(), health ?? new EndpointHealthCache());
        private static int[] Attempts(DiscoverySnapshot snapshot, EndpointHealthCache? health = null, int healthSeconds = 0)
        {
            var plan = Plan(snapshot, health, healthSeconds); var ports = new List<int>();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            return ports.ToArray();
        }
    }
}
