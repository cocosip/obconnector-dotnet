using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OceanBaseConnector.Internal;
using System.Text;
using OceanBaseConnector.Internal.Session;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DiscoveryPolicyTests
    {
        public static IEnumerable<object[]> Paths()
        {
            foreach (var ocp in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        foreach (var pooled in new[] { false, true })
                            foreach (var weighted in new[] { false, true })
                                yield return new object[] { ocp, asynchronous, oracle, pooled, weighted };
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task PoolHealthFeedbackPrefersAnotherMemberWithinItsGroup(bool ocp, bool asynchronous)
        {
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            using var file = new TnsFileFixture(DiscoveryPolicyFixture.Tns(port, server.Port, true).Replace("SERVERAFFINITY", "ROTATION").Replace("(WEIGHT=0)", "").Replace("(WEIGHT=10)", "").Replace("(WEIGHT=1)", ""));
            using var http = new OcpHttpFixture(DiscoveryPolicyFixture.Json(port, server.Port, true).Replace("SERVERAFFINITY", "ROTATION").Replace(",\"WEIGHT\":0", "").Replace(",\"WEIGHT\":10", "").Replace(",\"WEIGHT\":1", ""));
            IOceanBaseAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            var config = "Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=2;Host Recheck Seconds=60;Connection Timeout=5";
            using var first = new OceanBaseConnection(config) { AddressResolver = resolver };
            using var second = new OceanBaseConnection(config) { AddressResolver = resolver };
            using var capture = new TcpAttemptCapture();
            try
            {
                if (asynchronous) { await first.OpenAsync(); await second.OpenAsync(); }
                else { first.Open(); second.Open(); }
                Assert.Equal(new[] { "error", "success", "success" }, capture.Outcomes);
            }
            finally { second.Close(); first.Close(); OceanBaseConnection.ClearPool(first); }
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task PhysicalOpenPreservesGroupsAndZeroWeightFallback(bool ocp, bool asynchronous, bool oracle, bool pooled, bool weighted)
        {
            var greeting = ProtocolFixture.Load("initial-handshake-oceanbase").Payload;
            await using var first = new CredentialServer(greeting, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            await using var second = new CredentialServer(greeting, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            using var file = new TnsFileFixture(DiscoveryPolicyFixture.Tns(first.Port, second.Port, weighted));
            using var http = new OcpHttpFixture(DiscoveryPolicyFixture.Json(first.Port, second.Port, weighted));
            IOceanBaseAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            using var connection = new OceanBaseConnection($"Server=localhost;Port=1;User ID=u;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=1;Connection Timeout=5;Load Balance=RoundRobin;Host Recheck Seconds=60") { AddressResolver = resolver };
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "select grouped discovery";
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    connection.Close();
                    if (pooled) OceanBaseConnection.ClearPool(connection);
                }
                Assert.Equal(weighted ? 0 : 3, first.Accepted);
                Assert.Equal(weighted ? 3 : 0, second.Accepted);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }
        [Theory]
        [InlineData("", "", "a,b,c")]
        [InlineData("(OBLB_STRATEGY=ROTATION)", "", "a,b,c")]
        [InlineData("(OBLB_STRATEGY=RANDOM)", "", "b,c,a")]
        [InlineData("(OBLB=ON)", "", "b,c,a")]
        [InlineData("(LOAD_BALANCE=ON)", "", "b,c,a")]
        [InlineData("(OBLB_STRATEGY=RANDOM)", "(OBLB_STRATEGY=ROTATION)", "a,b,c")]
        [InlineData("(OBLB_STRATEGY=ROTATION)", "(OBLB_STRATEGY=RANDOM)", "b,c,a")]
        [InlineData("(OBLB_STRATEGY=ROTATION)", "(LOAD_BALANCE=ON)", "b,c,a")]
        public void PoliciesAndGroupOverridesKeepDeclaredGroupPriority(string policy, string groupPolicy, string expected)
        {
            var snapshot = Parse(policy + "(ADDRESS_LIST=" + groupPolicy + Address("a") + Address("b") + Address("c") + ")(ADDRESS_LIST=" + Address("last") + ")");
            Assert.Equal("a:2881,b:2881,c:2881,last:2881", snapshot.CanonicalAddresses);
            var ordered = snapshot.Order(new EndpointSelector(), OceanBaseLoadBalance.Sequential, 0, () => 0);
            Assert.Equal(expected + ",last", string.Join(",", ordered.Select(e => e.Host)));
        }

        [Fact]
        public void InheritedRoundRobinUsesOneCounterPerPhysicalRouteAcrossGroupsAndWraps()
        {
            var snapshot = Parse("(ADDRESS_LIST=" + Address("a") + Address("b") + ")(ADDRESS_LIST=" + Address("c") + Address("d") + ")");
            var selector = new EndpointSelector();
            Assert.Equal("a,b,c,d", Hosts(snapshot.Order(selector, OceanBaseLoadBalance.RoundRobin, 0)));
            Assert.Equal("b,a,d,c", Hosts(snapshot.Order(selector, OceanBaseLoadBalance.RoundRobin, 0)));
            Assert.Equal("a,b,c,d", Hosts(snapshot.Order(selector, OceanBaseLoadBalance.RoundRobin, 0)));
            typeof(EndpointSelector).GetField("_next", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(selector, int.MaxValue);
            Assert.Equal("a,b,c,d", Hosts(snapshot.Order(selector, OceanBaseLoadBalance.RoundRobin, 0)));
        }

        [Theory]
        [InlineData(0, "a,b,z,y")]
        [InlineData(0.499999, "a,b,z,y")]
        [InlineData(0.5, "b,a,z,y")]
        [InlineData(0.9999999999999999, "b,a,z,y")]
        public void WeightedSelectionUsesWideSumWithoutReplacementAndRetainsZeroFallback(double sample, string expected)
        {
            var snapshot = Parse("(OBLB_STRATEGY=SERVERAFFINITY)(ADDRESS_LIST=" + Address("z", "0") + Address("a", "2147483647") + Address("b", "2147483647") + Address("y", "0") + ")");
            Assert.Equal(expected, Hosts(snapshot.Order(new EndpointSelector(), OceanBaseLoadBalance.RoundRobin, 0, () => sample)));
            Assert.Equal("z:2881,a:2881,b:2881,y:2881", snapshot.CanonicalAddresses);
        }

        [Fact]
        public void DefaultsDuplicatesAndHealthStayWithinTheirOriginalGroup()
        {
            var snapshot = Parse("(OBLB_STRATEGY=SERVERAFFINITY)(ADDRESS_LIST=" + Address("a") + Address("a") + Address("z", "0") + ")(ADDRESS_LIST=" + Address("last") + ")");
            var selector = new EndpointSelector(); selector.Health.Failed(snapshot[0], 60);
            Assert.Equal("z,a,a,last", Hosts(snapshot.Order(selector, OceanBaseLoadBalance.Sequential, 60, () => 0)));
            Assert.Equal("a,a,z,last", Hosts(snapshot.Order(selector, OceanBaseLoadBalance.Sequential, 0, () => 0)));
            var grouped = Parse("(OBLB_STRATEGY=ROTATION)(ADDRESS_LIST=" + Address("a") + ")(ADDRESS_LIST=" + Address("last") + ")");
            Assert.Equal("a,last", Hosts(grouped.Order(selector, OceanBaseLoadBalance.RoundRobin, 60)));
        }

        [Fact]
        public void SnapshotClonesItsInputsAndSelectionsNeverMutateMembership()
        {
            var endpoints = OceanBaseEndpoint.Parse("a:1,b:2", 2881).ToList(); var weights = new List<int> { 0, 1 };
            var group = new DiscoveryGroup(endpoints, weights, DiscoveryStrategy.ServerAffinity);
            var groups = new List<DiscoveryGroup> { group }; var snapshot = new DiscoverySnapshot(groups);
            endpoints.Clear(); weights.Clear(); groups.Clear();
            Parallel.For(0, 100, _ => Assert.Equal("b,a", Hosts(snapshot.Order(new EndpointSelector(), OceanBaseLoadBalance.RoundRobin, 0))));
            Assert.Equal("a:1,b:2", snapshot.CanonicalAddresses); Assert.Equal(2, snapshot.Count);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData("2147483648")]
        [InlineData("1.5")]
        [InlineData("1e2")]
        [InlineData("zero")]
        public void InvalidWeightsAreRejected(string weight) => Assert.Throws<FormatException>(() => Parse("(OBLB_STRATEGY=SERVERAFFINITY)" + Address("a", weight)));

        [Theory]
        [InlineData("(OBLB_STRATEGY=ROTATION)(OBLB_STRATEGY=RANDOM)")]
        [InlineData("(OBLB=ON)(LOAD_BALANCE=ON)")]
        [InlineData("(OBLB_GROUP_STRATEGY=ROTATION)(oblb_group_strategy=ROTATION)")]
        public void DuplicatePolicyKeysAndAliasesAreRejected(string policy) => Assert.Throws<FormatException>(() => Parse(policy + Address("a")));

        [Theory]
        [InlineData("(OBLB=OFF)")]
        [InlineData("(LOAD_BALANCE=OFF)")]
        [InlineData("(OBLB_STRATEGY=UNKNOWN)")]
        [InlineData("(OBLB_GROUP_STRATEGY=RANDOM)")]
        [InlineData("(OBLB_BLACKLIST=ON)")]
        [InlineData("(OBLB_RETRY_ALL_DOWNS=2)")]
        [InlineData("(FAILOVER=ON)")]
        public void PoliciesAreAcceptedOrRejectedExplicitly(string policy)
        {
            if (policy == "(OBLB_RETRY_ALL_DOWNS=2)") Assert.Equal(2, Parse(policy + Address("a")).RetryAllDowns);
            else if (policy == "(OBLB_BLACKLIST=ON)") Assert.Throws<FormatException>(() => Parse(policy + Address("a")));
            else Assert.Throws<NotSupportedException>(() => Parse(policy + Address("a")));
        }

        [Fact]
        public void InvalidGroupWeightsAndMixedFormsAreRejected()
        {
            Assert.Throws<FormatException>(() => Parse("(OBLB_STRATEGY=SERVERAFFINITY)(ADDRESS_LIST=" + Address("a", "0") + Address("b", "0") + ")"));
            Assert.Throws<FormatException>(() => Parse("(ADDRESS_LIST=)"));
            Assert.Throws<NotSupportedException>(() => Parse(Address("a") + "(ADDRESS_LIST=" + Address("b") + ")"));
            Assert.Throws<NotSupportedException>(() => Parse(Address("a", "1")));
            Assert.Throws<FormatException>(() => Parse("(OBLB_STRATEGY=SERVERAFFINITY)" + Address("a", "1").Replace("(WEIGHT=1)", "(WEIGHT=1)(WEIGHT=2)")));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AddressAndCanonicalLimitsApplyAcrossAllGroups(bool ocp)
        {
            var first = ocp ? DiscoveryPolicyFixture.Json(1, 2, false) : DiscoveryPolicyFixture.Tns(1, 2, false);
            var member = ocp ? "{\"PROTOCOL\":\"TCP\",\"HOST\":\"127.0.0.1\",\"PORT\":1}" : "(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1))";
            var expanded = first.Replace(member, string.Join(ocp ? "," : "", Enumerable.Repeat(member, 255)));
            Assert.Equal(256, Read(expanded, ocp).Count);
            Assert.Throws<FormatException>(() => Read(expanded.Replace(member, member + (ocp ? "," : "") + member), ocp));
            var host = string.Join(".", Enumerable.Repeat(new string('a', 63), 4));
            var large = first.Replace("127.0.0.1", host);
            member = member.Replace("127.0.0.1", host);
            Assert.Throws<FormatException>(() => Read(large.Replace(member, string.Join(ocp ? "," : "", Enumerable.Repeat(member, 255))), ocp));
        }

        [Theory]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("1.0")]
        [InlineData("1e2")]
        [InlineData("-1")]
        [InlineData("\" 1\"")]
        public void OcpWeightScalarMustBeAnUnsignedInteger(string weight)
        {
            var json = DiscoveryPolicyFixture.Json(1, 2, true).Replace("\"WEIGHT\":10", "\"WEIGHT\":" + weight);
            Assert.Throws<FormatException>(() => Read(json, true));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SharedResolverSnapshotsRetainTheirOwnWeightsAfterLaterDiscovery(bool ocp)
        {
            using var file = new TnsFileFixture(DiscoveryPolicyFixture.Tns(1, 2, true));
            using var http = new OcpHttpFixture(DiscoveryPolicyFixture.Json(1, 2, true));
            IDiscoveryAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            var before = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            file.Write(DiscoveryPolicyFixture.Tns(1, 2, true).Replace("(WEIGHT=0)", "(WEIGHT=20)").Replace("(WEIGHT=10)", "(WEIGHT=0)"));
            http.Body = DiscoveryPolicyFixture.Json(1, 2, true).Replace("\"WEIGHT\":0", "\"WEIGHT\":20").Replace("\"WEIGHT\":10", "\"WEIGHT\":0");
            var after = await resolver.ResolveSnapshotAsync(CancellationToken.None);
            var selector = new EndpointSelector();
            Assert.Equal(2, before.Order(selector, OceanBaseLoadBalance.Sequential, 0)[0].Port);
            Assert.Equal(1, after.Order(selector, OceanBaseLoadBalance.Sequential, 0)[0].Port);
            Assert.Equal(before.CanonicalAddresses, after.CanonicalAddresses);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RetryRoundsUseOneFrozenRandomSelection(bool asynchronous)
        {
            using var first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var second = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            first.Bind(new IPEndPoint(IPAddress.Loopback, 0)); second.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var a = ((IPEndPoint)first.LocalEndPoint!).Port; var b = ((IPEndPoint)second.LocalEndPoint!).Port;
            var snapshot = Read(DiscoveryPolicyFixture.Tns(a, b, true).Replace("SERVERAFFINITY", "RANDOM").Replace("(WEIGHT=0)", "").Replace("(WEIGHT=10)", "").Replace("(WEIGHT=1)", ""), false);
            var random = new CountingRandom(); var selector = new EndpointSelector(random);
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;TLS Mode=Disabled;Connect Retry Count=1;Host Recheck Seconds=60;Connection Timeout=30").Build(), endpointSelector: selector, resolvedEndpoints: snapshot);
            var endpoints = new List<string>(); using var capture = new TcpAttemptCapture((_, _) => endpoints.Add(session.Endpoint));
            if (asynchronous) await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync());
            else Assert.Throws<OceanBaseException>(() => session.Open());
            Assert.Equal(6, endpoints.Count); Assert.Equal(endpoints.Take(3), endpoints.Skip(3));
            Assert.Equal(1, random.Calls);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task TcpSuccessWithMissingGreetingNeverAdvancesToAnotherGroup(bool ocp, bool asynchronous)
        {
            var first = LoopbackServer.Start(_ => Task.CompletedTask);
            try
            {
                await using var fallback = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
                using var file = new TnsFileFixture(DiscoveryPolicyFixture.Tns(first.Port, fallback.Port, false));
                using var http = new OcpHttpFixture(DiscoveryPolicyFixture.Json(first.Port, fallback.Port, false));
                IOceanBaseAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
                using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Connect Retry Count=2;Connection Timeout=5") { AddressResolver = resolver };
                if (asynchronous) await Assert.ThrowsAsync<OceanBaseException>(() => connection.OpenAsync());
                else Assert.Throws<OceanBaseException>(() => connection.Open());
                Assert.Equal(0, fallback.Accepted); Assert.Equal(ocp ? 1 : 0, http.Requests.Count);
            }
            finally { first.Stop(); }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LoadBalanceStrategyAliasRetainsPolicyAndRejectsConflicts(bool ocp)
        {
            var text = ocp ? DiscoveryPolicyFixture.Json(1, 2, false) : DiscoveryPolicyFixture.Tns(1, 2, false);
            var alias = text.Replace("OBLB_STRATEGY", "LOAD_BALANCE_STRATEGY");
            Assert.Equal(1, Read(alias, ocp).Order(new EndpointSelector(), OceanBaseLoadBalance.RoundRobin, 0)[0].Port);
            var duplicate = alias.Replace(ocp ? "\"LOAD_BALANCE_STRATEGY\":\"ROTATION\"" : "(LOAD_BALANCE_STRATEGY=ROTATION)", ocp ? "\"LOAD_BALANCE_STRATEGY\":\"ROTATION\",\"OBLB_STRATEGY\":\"RANDOM\"" : "(LOAD_BALANCE_STRATEGY=ROTATION)(OBLB_STRATEGY=RANDOM)");
            Assert.Throws<FormatException>(() => Read(duplicate, ocp));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task PolicyOnlyUpdatesKeepIdleMembershipUntilPhysicalRecreation(bool ocp, bool asynchronous)
        {
            var greeting = ProtocolFixture.Load("initial-handshake-oceanbase").Payload;
            await using var first = new CredentialServer(greeting, oracle: false);
            await using var second = new CredentialServer(greeting, oracle: false);
            using var file = new TnsFileFixture(DiscoveryPolicyFixture.Tns(first.Port, second.Port, true));
            using var http = new OcpHttpFixture(DiscoveryPolicyFixture.Json(first.Port, second.Port, true));
            IOceanBaseAddressResolver resolver = ocp ? new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP") : new OceanBaseTnsAddressResolver(file.Path, "APP");
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Connection Timeout=5") { AddressResolver = resolver };
            async Task Open() { if (asynchronous) await connection.OpenAsync(); else connection.Open(); }
            try
            {
                await Open(); connection.Close();
                file.Write(DiscoveryPolicyFixture.Tns(first.Port, second.Port, true).Replace("(WEIGHT=0)", "(WEIGHT=20)").Replace("(WEIGHT=10)", "(WEIGHT=0)"));
                http.Body = DiscoveryPolicyFixture.Json(first.Port, second.Port, true).Replace("\"WEIGHT\":0", "\"WEIGHT\":20").Replace("\"WEIGHT\":10", "\"WEIGHT\":0");
                await Open(); connection.Close(); Assert.Equal(0, first.Accepted); Assert.Equal(1, second.Accepted);
                OceanBaseConnection.ClearPool(connection);
                await Open(); Assert.Equal(1, first.Accepted); Assert.Equal(1, second.Accepted);
                Assert.Equal(ocp ? 3 : 0, http.Requests.Count);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Fact]
        public async Task ConcurrentOcpCallsDoNotMixResponsePolicies()
        {
            var count = 0;
            using var http = new OcpHttpFixture("");
            http.Respond = async (_, _) =>
            {
                var index = Interlocked.Increment(ref count) - 1;
                var body = DiscoveryPolicyFixture.Json(1, 2, true);
                if (index % 2 == 1) body = body.Replace("\"WEIGHT\":0", "\"WEIGHT\":20").Replace("\"WEIGHT\":10", "\"WEIGHT\":0");
                await Task.Yield();
                return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json") };
            };
            IDiscoveryAddressResolver resolver = new OceanBaseOcpAddressResolver(http.Client, new Uri("https://ocp.example/"), "APP");
            var tasks = Enumerable.Range(0, 32).Select(_ => resolver.ResolveSnapshotAsync(CancellationToken.None)).ToArray();
            var snapshots = await Task.WhenAll(tasks);
            Parallel.For(0, snapshots.Length, i => Assert.Equal(i % 2 == 0 ? 2 : 1, snapshots[i].Order(new EndpointSelector(), OceanBaseLoadBalance.RoundRobin, 0)[0].Port));
            Assert.Equal(32, http.Requests.Count);
        }

        private sealed class CountingRandom : Random
        {
            internal int Calls { get; private set; }
            public override double NextDouble() { Calls++; return 0; }
        }

        private static DiscoverySnapshot Parse(string body) => TnsAddressParser.ParseSnapshot("APP=(DESCRIPTION=" + body + ")", "APP", null, CancellationToken.None);
        private static DiscoverySnapshot Read(string text, bool ocp) => ocp ? OcpAddressParser.ParseSnapshot(Encoding.UTF8.GetBytes(text), "APP", null, CancellationToken.None) : TnsAddressParser.ParseSnapshot(text, "APP", null, CancellationToken.None);
        private static string Address(string host, string? weight = null) => "(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=2881)" + (weight == null ? "" : "(WEIGHT=" + weight + ")") + ")";
        private static string Hosts(IReadOnlyList<OceanBaseEndpoint> endpoints) => string.Join(",", endpoints.Select(e => e.Host));
    }
}
