using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class EndpointHealthCacheTests
    {
        [Fact]
        public void ConfigurationDefaultsCopiesAndCanonicalIdentityRetainThePolicy()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=first,second;User ID=u");
            Assert.Equal(0, builder.HostRecheckSeconds);
            builder.HostRecheckSeconds = 3600; var snapshot = builder.Build(); builder.HostRecheckSeconds = 0;
            foreach (var options in new[] { snapshot, snapshot.ForEndpoint(snapshot.Endpoints[1]), snapshot.WithCredential(new OceanBaseCredential("other", "secret", 1)) })
                Assert.Equal(3600, options.HostRecheckSeconds);
            Assert.Equal("Host Recheck Seconds", OceanBaseConnectionStringBuilder.HostRecheckSecondsKey);
            Assert.Equal(3600, new OceanBaseConnectionStringBuilder(snapshot.SanitizedConnectionString).HostRecheckSeconds);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("3601")]
        [InlineData("2147483648")]
        [InlineData("1.5")]
        [InlineData("oops")]
        public void EverySetterRejectsOutOfRangeAndMalformedPolicies(string value)
        {
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Host Recheck Seconds=" + value));
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder { ["Host Recheck Seconds"] = value });
            if (int.TryParse(value, out var number)) Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder { HostRecheckSeconds = number });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void StablePartitionPreservesRotatedOrderAndDuplicateCandidates(int start)
        {
            var endpoints = OceanBaseEndpoint.Parse("a,b,a,c", 2881);
            var cache = new EndpointHealthCache(); cache.Failed(endpoints[0], 60);
            var rotated = Enumerable.Range(0, endpoints.Length).Select(i => endpoints[(start + i) % endpoints.Length]).ToArray();
            var expected = rotated.Where(e => e.Host != "a").Concat(rotated.Where(e => e.Host == "a"));
            Assert.Equal(expected, cache.Order(endpoints, start, 60));
            foreach (var endpoint in endpoints) cache.Failed(endpoint, 60);
            Assert.Equal(rotated, cache.Order(endpoints, start, 60));
        }

        [Fact]
        public void ZeroDoesNotRecordFailuresAndSuccessRemovesAnExistingFailure()
        {
            var endpoints = OceanBaseEndpoint.Parse("first,second", 2881); var cache = new EndpointHealthCache();
            cache.Failed(endpoints[0], 0); Assert.Equal(endpoints, cache.Order(endpoints, 0, 60));
            cache.Failed(endpoints[0], 60); Assert.Equal(endpoints.Reverse(), cache.Order(endpoints, 0, 60));
            cache.Succeeded(endpoints[0]); Assert.Equal(endpoints, cache.Order(endpoints, 0, 60));
        }

        [Fact]
        public async Task ExpiryIsLazyAndDoesNotMutateAnAlreadySelectedRound()
        {
            var endpoints = OceanBaseEndpoint.Parse("first,second", 2881); var cache = new EndpointHealthCache();
            cache.Failed(endpoints[0], 1); var round = cache.Order(endpoints, 0, 1);
            await Task.Delay(1100);
            Assert.Equal(endpoints, cache.Order(endpoints, 0, 1));
            cache.Failed(endpoints[1], 1); Assert.Equal(endpoints, cache.Order(endpoints, 0, 1));
            Assert.Equal(endpoints.Reverse(), round);
        }

        [Fact]
        public void EndpointIdentityIncludesExactHostAndPort()
        {
            var endpoints = OceanBaseEndpoint.Parse("Host:2881,host:2881,Host:2882,[::1]:2881", 2881);
            var cache = new EndpointHealthCache(); cache.Failed(endpoints[0], 60);
            Assert.Equal(endpoints.Skip(1).Concat(endpoints.Take(1)), cache.Order(endpoints, 0, 60));
        }

        [Fact]
        public void CapacityEvictsOldestFailuresWhenResolverAddressesKeepChanging()
        {
            var endpoints = Enumerable.Range(0, 1500).Select(i => OceanBaseEndpoint.Parse("node" + i, 2881)[0]).ToArray();
            var cache = new EndpointHealthCache(); foreach (var endpoint in endpoints) cache.Failed(endpoint, 60);
            Assert.Equal(1024, Failures(cache).Count);
            Assert.Equal(new[] { endpoints[0], endpoints[1499] }, cache.Order(new[] { endpoints[1499], endpoints[0] }, 0, 60));
            cache.Failed(endpoints[1499], 60); Assert.Equal(1024, Failures(cache).Count);
        }

        [Fact]
        public void ConcurrentFeedbackAndSelectionPreserveEveryCandidateAndCapacity()
        {
            var endpoints = Enumerable.Range(0, 1500).Select(i => OceanBaseEndpoint.Parse("node" + i, 2881)[0]).ToArray();
            var cache = new EndpointHealthCache();
            Parallel.For(0, 4000, i =>
            {
                var index = i % endpoints.Length;
                cache.Failed(endpoints[index], 60);
                if (i % 7 == 0) cache.Succeeded(endpoints[index]);
                var candidates = new[] { endpoints[index], endpoints[(index + 1) % endpoints.Length] };
                Assert.Equal(candidates.OrderBy(e => e.Host), cache.Order(candidates, i % 2, 60).OrderBy(e => e.Host));
            });
            Assert.InRange(Failures(cache).Count, 1, 1024);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ClearAndCredentialRotationIsolateOldFeedbackAndConstructorRaces(bool rotateCredential)
        {
            var endpoints = OceanBaseEndpoint.Parse("first,second", 2881);
            var options = new OceanBaseConnectionStringBuilder("Server=first,second;User ID=u;Pooling=true;Max Pool Size=2;Host Recheck Seconds=60").Build();
            var provider = new Provider();
            using var pool = new PhysicalSessionPool(credentialProvider: rotateCredential ? provider : null);
            var first = pool.Rent(options, null, new EndpointSelector(), out _);
            PhysicalSession? next = null;
            try
            {
                var old = Health(first); old.Failed(endpoints[0], 60);
                if (rotateCredential) provider.Version = 2; else pool.Clear();
                next = pool.Rent(options, null, new EndpointSelector(), out _);
                var current = Health(next); Assert.NotSame(old, current);
                old.Failed(endpoints[0], 60); Assert.Equal(endpoints, current.Order(endpoints, 0, 60));
                current.Failed(endpoints[0], 60); old.Succeeded(endpoints[0]);
                Assert.Equal(endpoints.Reverse(), current.Order(endpoints, 0, 60));
                var raced = (EndpointHealthCache)typeof(PhysicalSessionPool).GetMethod("HealthForGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pool, new object[] { 0L })!;
                Assert.NotSame(current, raced); raced.Succeeded(endpoints[0]);
                Assert.Equal(endpoints.Reverse(), current.Order(endpoints, 0, 60));
                pool.Dispose(); old.Failed(endpoints[1], 60);
                Assert.Throws<ObjectDisposedException>(() => pool.Rent(options, null, new EndpointSelector(), out _));
            }
            finally { pool.Discard(first); if (next != null) pool.Discard(next); }
        }

        private static EndpointHealthCache Health(PhysicalSession session) => (EndpointHealthCache)typeof(PhysicalSession).GetField("_endpointHealth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        private static Dictionary<string, long> Failures(EndpointHealthCache cache) => (Dictionary<string, long>)typeof(EndpointHealthCache).GetField("_failures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        private sealed class Provider : IOceanBaseCredentialProvider
        {
            internal long Version = 1;
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token) => new("u", "", Version);
            public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token) => Task.FromResult(GetCredential(context, token));
        }
    }
}
