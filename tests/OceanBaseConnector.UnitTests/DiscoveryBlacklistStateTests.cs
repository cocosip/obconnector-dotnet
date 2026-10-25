using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DiscoveryBlacklistStateTests
    {
        [Fact]
        public void RetryWindowCountsDistinctFailuresAtTheSameTickAndRecoversByProbe()
        {
            long now = 0; var cache = new DiscoveryBlacklistCache(() => now);
            var endpoint = OceanBaseEndpoint.Parse("one:1", 1)[0];
            var policy = new DiscoveryBlacklistPolicy(true, 3, 1000, 2);
            Assert.False(cache.Failed(endpoint, policy)); Assert.False(cache.Failed(endpoint, policy));
            Assert.True(cache.Failed(endpoint, policy)); Assert.True(cache.IsBlocked(endpoint, policy));
            Assert.Equal(0, cache.FailureEventCount);
            now = 2 * Stopwatch.Frequency;
            Assert.False(cache.IsBlocked(endpoint, policy));
            Assert.True(cache.Failed(endpoint, policy)); Assert.True(cache.IsBlocked(endpoint, policy));
            cache.Succeeded(endpoint, policy); Assert.False(cache.IsBlocked(endpoint, policy)); Assert.Equal(0, cache.Count);
            Assert.False(cache.Failed(endpoint, policy));
        }

        [Fact]
        public void SlidingWindowIncludesItsExactBoundaryAndDropsOlderFailures()
        {
            long now = 0; var endpoint = OceanBaseEndpoint.Parse("one:1", 1)[0];
            var cache = new DiscoveryBlacklistCache(() => now); var policy = new DiscoveryBlacklistPolicy(true, 2, 1000, 2);
            Assert.False(cache.Failed(endpoint, policy)); now = Stopwatch.Frequency;
            Assert.True(cache.Failed(endpoint, policy)); cache.Succeeded(endpoint, policy);
            Assert.False(cache.Failed(endpoint, policy)); now += Stopwatch.Frequency + 1;
            Assert.False(cache.Failed(endpoint, policy)); Assert.Equal(1, cache.FailureEventCount);
        }

        [Fact]
        public void EffectivePoliciesAndPoolGenerationCachesCannotPoisonOrClearEachOther()
        {
            var endpoint = OceanBaseEndpoint.Parse("one:1", 1)[0];
            var old = new EndpointHealthCache(); var fresh = new EndpointHealthCache();
            var normal = new DiscoveryBlacklistPolicy(); var other = new DiscoveryBlacklistPolicy(timeoutSeconds: 1);
            old.Blacklist.Failed(endpoint, normal);
            Assert.True(old.Blacklist.IsBlocked(endpoint, normal)); Assert.False(old.Blacklist.IsBlocked(endpoint, other));
            old.Blacklist.Succeeded(endpoint, other); Assert.True(old.Blacklist.IsBlocked(endpoint, normal));
            old.Blacklist.Failed(endpoint, other); Assert.False(fresh.Blacklist.IsBlocked(endpoint, other));
            old.Blacklist.Failed(endpoint, normal); Assert.False(fresh.Blacklist.IsBlocked(endpoint, normal));
            old.Blacklist.Succeeded(endpoint, normal); Assert.True(old.Blacklist.IsBlocked(endpoint, other));
        }

        [Fact]
        public void ZeroCooldownAllowsImmediateProbeAndZeroThresholdAdmitsTheFirstFailure()
        {
            var endpoint = OceanBaseEndpoint.Parse("one:1", 1)[0]; var cache = new DiscoveryBlacklistCache(() => 0);
            var policy = new DiscoveryBlacklistPolicy(true, 0, 0, 0);
            Assert.True(cache.Failed(endpoint, policy)); Assert.False(cache.IsBlocked(endpoint, policy));
            Assert.True(cache.Failed(endpoint, policy)); Assert.False(cache.IsBlocked(endpoint, policy));
        }

        [Fact]
        public void EntryAndEventCapacityEvictionsForgetHistoryWithoutFalseAdmission()
        {
            var cache = new DiscoveryBlacklistCache(() => 0); var policy = new DiscoveryBlacklistPolicy();
            for (var i = 0; i < 1025; i++) cache.Failed(OceanBaseEndpoint.Parse("h" + i + ":1", 1)[0], policy);
            Assert.Equal(1024, cache.Count); Assert.False(cache.IsBlocked(OceanBaseEndpoint.Parse("h0:1", 1)[0], policy));
            var threshold = new DiscoveryBlacklistPolicy(true, int.MaxValue, int.MaxValue);
            var endpoint = OceanBaseEndpoint.Parse("large:1", 1)[0];
            for (var i = 0; i < 20000; i++) Assert.False(cache.Failed(endpoint, threshold));
            Assert.InRange(cache.Count, 1, 1024); Assert.InRange(cache.FailureEventCount, 1, 16384);
            Assert.False(cache.IsBlocked(endpoint, threshold)); cache.Succeeded(endpoint, threshold);
            Assert.Equal(0, cache.FailureEventCount);
        }

        [Fact]
        public void ConcurrentFailuresCountEveryEventWithoutLosingTheThreshold()
        {
            var endpoint = OceanBaseEndpoint.Parse("one:1", 1)[0]; var cache = new DiscoveryBlacklistCache(() => 0);
            var policy = new DiscoveryBlacklistPolicy(true, 1280, 1000); var admissions = 0;
            Parallel.For(0, 1280, _ => { if (cache.Failed(endpoint, policy)) Interlocked.Increment(ref admissions); });
            Assert.Equal(1, admissions); Assert.True(cache.IsBlocked(endpoint, policy)); Assert.Equal(0, cache.FailureEventCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RandomAndWeightedOrdersAreFrozenAcrossFallbackAndAdditionalCycles(bool weighted)
        {
            var endpoints = OceanBaseEndpoint.Parse("one:1,two:2,three:3,four:4", 1);
            var strategy = weighted ? DiscoveryStrategy.ServerAffinity : DiscoveryStrategy.Random;
            var groups = new List<DiscoveryGroup>
            {
                new DiscoveryGroup(endpoints.Take(2).ToList(), new List<int> { 1, 2 }, strategy),
                new DiscoveryGroup(endpoints.Skip(2).ToList(), new List<int> { 3, 4 }, strategy),
            };
            var random = new FixedRandom(); var snapshot = new DiscoverySnapshot(groups, new DiscoveryBlacklistPolicy(), 12);
            var plan = new EndpointAttemptPlan(snapshot, Options(2), new EndpointSelector(random), new EndpointHealthCache());
            var calls = random.Calls; var ports = new List<int>();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            Assert.Equal(12, ports.Count); Assert.True(calls > 0); Assert.Equal(calls, random.Calls);
            Assert.Equal(ports.Take(4), ports.Skip(4).Take(4)); Assert.Equal(ports.Take(4), ports.Skip(8));
            Assert.All(ports.Take(2), port => Assert.InRange(port, 1, 2)); Assert.All(ports.Skip(2).Take(2), port => Assert.InRange(port, 3, 4));
        }

        private sealed class FixedRandom : Random
        {
            internal int Calls { get; private set; }
            public override double NextDouble() { Calls++; return 0.25; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OptionalFieldsAndMaximumValuesHaveExplicitDefaults(bool ocp)
        {
            var text = ocp ? DiscoveryBlacklistFixture.Json(1, 2) : DiscoveryBlacklistFixture.Tns(1, 2);
            var snapshot = Read(text, ocp);
            Assert.Equal(120, snapshot.RetryAllDowns); Assert.False(snapshot.BlacklistPolicy!.RetryDuration);
            Assert.Equal(50, snapshot.BlacklistPolicy.TimeoutSeconds);
            var capOnly = ocp ? "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"OBLB_RETRY_ALL_DOWNS\":2147483647,\"ADDRESS_LIST\":[{\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"one\",\"PORT\":1}]}]}}}" : "APP=(DESCRIPTION=(OBLB_RETRY_ALL_DOWNS=2147483647)(ADDRESS=(PROTOCOL=TCP)(HOST=one)(PORT=1)))";
            Assert.Equal(int.MaxValue, Read(capOnly, ocp).RetryAllDowns); Assert.NotNull(Read(capOnly, ocp).BlacklistPolicy);
            var empty = ocp ? capOnly.Replace("\"OBLB_RETRY_ALL_DOWNS\":2147483647", "\"OBLB_BLACKLIST\":{}") : capOnly.Replace("(OBLB_RETRY_ALL_DOWNS=2147483647)", "(OBLB_BLACKLIST=)");
            Assert.Equal(snapshot.BlacklistPolicy.Key, Read(empty, ocp).BlacklistPolicy!.Key);
            var max = (ocp ? DiscoveryBlacklistFixture.Json(1, 2, int.MaxValue, int.MaxValue, int.MaxValue) : DiscoveryBlacklistFixture.Tns(1, 2, int.MaxValue, int.MaxValue, int.MaxValue));
            var parsed = Read(max, ocp); Assert.Equal(int.MaxValue, parsed.BlacklistPolicy!.RetryTimes); Assert.Equal(int.MaxValue, parsed.BlacklistPolicy.DurationMilliseconds);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData("2147483648")]
        [InlineData("1.5")]
        [InlineData("1e2")]
        [InlineData("x")]
        public void EveryTnsIntegerBoundaryRejectsInvalidValues(string value)
        {
            var text = DiscoveryBlacklistFixture.Tns(1, 2, 3, 50, 2);
            foreach (var field in new[] { "OBLB_RETRY_ALL_DOWNS=3", "TIMEOUT=50", "RETRYTIMES=2", "DURATION=2147483647" })
                Assert.Throws<FormatException>(() => Read(text.Replace(field, field.Substring(0, field.IndexOf('=') + 1) + value), false));
        }

        [Theory]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("-1")]
        [InlineData("1.5")]
        [InlineData("1e2")]
        [InlineData("2147483648")]
        [InlineData("\" 1\"")]
        public void EveryOcpIntegerRequiresAnIntegerTokenOrDigitString(string value)
        {
            var text = DiscoveryBlacklistFixture.Json(1, 2, 3, 50, 2);
            foreach (var field in new[] { "\"OBLB_RETRY_ALL_DOWNS\":3", "\"TIMEOUT\":50", "\"RETRYTIMES\":2", "\"DURATION\":2147483647" })
                Assert.Throws<FormatException>(() => Read(text.Replace(field, field.Substring(0, field.LastIndexOf(':') + 1) + value), true));
        }

        [Theory]
        [InlineData("(OBLB_BLACKLIST=ON)")]
        [InlineData("(OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=RETRYDURATION)(RETRYTIMES=2)))")]
        [InlineData("(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)))")]
        [InlineData("(OBLB_RETRY_ALL_DOWNS=1)(oblb_retry_all_downs=2)")]
        [InlineData("(OBLB_BLACKLIST=)(oblb_blacklist=)")]
        [InlineData("(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=1)(timeout=2)))")]
        public void MissingDuplicateAndMalformedFieldsAreRejected(string policy) => Assert.Throws<FormatException>(() => Read(One(policy), false));

        [Theory]
        [InlineData("(OBLB_BLACKLIST=(UNKNOWN=x))")]
        [InlineData("(OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=UNKNOWN)))")]
        [InlineData("(OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=NORMAL)(RETRYTIMES=2)))")]
        [InlineData("(OBLB_BLACKLIST=(REMOVE_STRATEGY=(NAME=UNKNOWN)(TIMEOUT=1)))")]
        public void UnknownOrInapplicableStrategiesCannotBeIgnored(string policy) => Assert.Throws<NotSupportedException>(() => Read(One(policy), false));

        [Fact]
        public void ThresholdRequeueThenOneFallbackKeepsGroupsAndSharesCeiling()
        {
            var plan = Plan(6, threshold: 2); var ports = new List<int>(); var waits = new List<bool>();
            while (plan.TryNext(out var endpoint, out var delay)) { ports.Add(endpoint.Port); waits.Add(delay); plan.Failed(); }
            Assert.Equal(new[] { 1, 1, 2, 2, 1, 2 }, ports); Assert.Equal(new[] { false, false, false, false, true, false }, waits);
            plan = Plan(5, retries: 2); ports.Clear(); waits.Clear();
            while (plan.TryNext(out var endpoint, out var delay)) { ports.Add(endpoint.Port); waits.Add(delay); plan.Failed(); }
            Assert.Equal(new[] { 1, 2, 1, 2, 1 }, ports); Assert.Equal(new[] { false, false, true, false, true }, waits);
        }

        [Fact]
        public void AllBlockedCandidatesFallBackImmediatelyAndSuccessClearsOnlyItsEndpoint()
        {
            var snapshot = Read(DiscoveryBlacklistFixture.Tns(1, 2), false); var health = new EndpointHealthCache();
            foreach (var endpoint in snapshot) health.Blacklist.Failed(endpoint, snapshot.BlacklistPolicy!);
            var plan = new EndpointAttemptPlan(snapshot, Options(), new EndpointSelector(), health);
            Assert.True(plan.TryNext(out var chosen, out var delay)); Assert.Equal(1, chosen.Port); Assert.False(delay); plan.Succeeded();
            Assert.False(health.Blacklist.IsBlocked(snapshot[0], snapshot.BlacklistPolicy!)); Assert.True(health.Blacklist.IsBlocked(snapshot[1], snapshot.BlacklistPolicy!));
        }

        [Fact]
        public void CapZeroDoesNotAttemptAndNoPolicyRetainsExactlyTheConfiguredRounds()
        {
            Assert.False(Plan(0).TryNext(out _, out _));
            var snapshot = Read(DiscoveryPolicyFixture.Tns(1, 2, false), false);
            var plan = new EndpointAttemptPlan(snapshot, Options(2), new EndpointSelector(), new EndpointHealthCache());
            var ports = new List<int>(); var waits = new List<bool>();
            while (plan.TryNext(out var endpoint, out var delay)) { ports.Add(endpoint.Port); waits.Add(delay); plan.Failed(); }
            Assert.Equal(new[] { 1, 2, 1, 2, 1, 2 }, ports); Assert.Equal(new[] { false, false, true, false, true, false }, waits);
            plan = Plan(120, threshold: int.MaxValue); ports.Clear();
            while (plan.TryNext(out var endpoint, out _)) { ports.Add(endpoint.Port); plan.Failed(); }
            Assert.Equal(120, ports.Count); Assert.All(ports, port => Assert.Equal(1, port));
        }

        private static EndpointAttemptPlan Plan(int cap, int threshold = 1, int retries = 0) => new EndpointAttemptPlan(Read(DiscoveryBlacklistFixture.Tns(1, 2, cap, threshold: threshold), false), Options(retries), new EndpointSelector(), new EndpointHealthCache());
        private static OceanBaseConnectionOptions Options(int retries = 0) => new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;TLS Mode=Disabled;Connect Retry Count=" + retries).Build();
        private static DiscoverySnapshot Read(string text, bool ocp) => ocp ? OcpAddressParser.ParseSnapshot(Encoding.UTF8.GetBytes(text), "APP", null, CancellationToken.None) : TnsAddressParser.ParseSnapshot(text, "APP", null, CancellationToken.None);
        private static string One(string policy) => "APP=(DESCRIPTION=" + policy + "(ADDRESS=(PROTOCOL=TCP)(HOST=one)(PORT=1)))";
    }
}
