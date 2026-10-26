using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal
{
    internal interface IDiscoveryAddressResolver
    {
        DiscoverySnapshot ResolveSnapshot(CancellationToken token);
        Task<DiscoverySnapshot> ResolveSnapshotAsync(CancellationToken token);
    }

    internal enum DiscoveryStrategy { Inherit, Rotation, Random, ServerAffinity }

    internal sealed class DiscoveryGroup
    {
        private readonly OceanBaseEndpoint[] _endpoints;
        private readonly int[] _weights;
        internal DiscoveryGroup(List<OceanBaseEndpoint> endpoints, List<int> weights, DiscoveryStrategy strategy, DiscoveryBlacklistPolicy? blacklist = null, int? retryAllDowns = null)
        {
            _endpoints = endpoints.ToArray(); _weights = weights.ToArray(); Strategy = strategy; BlacklistPolicy = blacklist; RetryAllDowns = retryAllDowns;
        }
        internal DiscoveryStrategy Strategy { get; }
        internal DiscoveryBlacklistPolicy? BlacklistPolicy { get; }
        internal int? RetryAllDowns { get; }
        internal int Count => _endpoints.Length;
        internal OceanBaseEndpoint Endpoint(int index) => _endpoints[index];
        internal int Weight(int index) => _weights[index];
    }

    // A snapshot belongs to one discovery call; shared resolvers never retain mutable routing policy.
    internal sealed class DiscoverySnapshot : IReadOnlyList<OceanBaseEndpoint>
    {
        private readonly DiscoveryGroup[] _groups;
        private readonly OceanBaseEndpoint[] _endpoints;
        internal DiscoverySnapshot(List<DiscoveryGroup> groups, DiscoveryBlacklistPolicy? blacklist = null, int? retryAllDowns = null)
        {
            BlacklistPolicy = blacklist ?? (retryAllDowns.HasValue ? new DiscoveryBlacklistPolicy() : null);
            _groups = groups.ToArray();
            var endpoints = new List<OceanBaseEndpoint>();
            var canonical = new StringBuilder();
            var hasBlacklist = BlacklistPolicy != null;
            foreach (var group in _groups)
            {
                hasBlacklist |= group.BlacklistPolicy != null;
                for (var i = 0; i < group.Count; i++)
                {
                    var endpoint = group.Endpoint(i);
                    if (canonical.Length != 0) canonical.Append(',');
                    canonical.Append(endpoint.ToString()); endpoints.Add(endpoint);
                    if (endpoints.Count > 256 || canonical.Length > 65536) throw new FormatException("Excessive discovery address configuration.");
                }
            }
            RetryAllDowns = retryAllDowns ?? (hasBlacklist ? 120 : (int?)null);
            if (endpoints.Count == 0) throw new FormatException("Discovery requires addresses.");
            _endpoints = endpoints.ToArray(); CanonicalAddresses = canonical.ToString();
        }

        internal string CanonicalAddresses { get; }
        internal DiscoveryBlacklistPolicy? BlacklistPolicy { get; }
        internal int? RetryAllDowns { get; }
        internal bool HasTcpBudget
        {
            get
            {
                if (RetryAllDowns == 0) return false;
                foreach (var group in _groups) if (group.RetryAllDowns != 0) return true;
                return false;
            }
        }
        internal int[] CopyGroupSizes()
        {
            var sizes = new int[_groups.Length];
            for (var i = 0; i < sizes.Length; i++) sizes[i] = _groups[i].Count;
            return sizes;
        }
        internal DiscoveryBlacklistPolicy?[] CopyGroupPolicies()
        {
            var policies = new DiscoveryBlacklistPolicy?[_groups.Length];
            for (var i = 0; i < policies.Length; i++) policies[i] = _groups[i].BlacklistPolicy ?? BlacklistPolicy;
            return policies;
        }
        internal int?[] CopyGroupLimits()
        {
            var limits = new int?[_groups.Length];
            for (var i = 0; i < limits.Length; i++) limits[i] = _groups[i].RetryAllDowns;
            return limits;
        }
        public int Count => _endpoints.Length;
        public OceanBaseEndpoint this[int index] => _endpoints[index];
        public IEnumerator<OceanBaseEndpoint> GetEnumerator() => ((IEnumerable<OceanBaseEndpoint>)_endpoints).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal IReadOnlyList<OceanBaseEndpoint> Order(EndpointSelector selector, OceanBaseLoadBalance balance, int healthSeconds, Func<double>? sample = null, EndpointHealthCache? health = null)
        {
            sample ??= selector.NextDouble;
            health ??= selector.Health;
            uint? rotation = null;
            var result = new List<OceanBaseEndpoint>(Count);
            foreach (var group in _groups)
            {
                var indexes = new List<int>(group.Count);
                for (var i = 0; i < group.Count; i++) indexes.Add(i);
                if (group.Strategy == DiscoveryStrategy.Random)
                {
                    for (var i = indexes.Count - 1; i > 0; i--)
                    {
                        var selected = (int)(sample() * (i + 1));
                        var value = indexes[i]; indexes[i] = indexes[selected]; indexes[selected] = value;
                    }
                }
                else if (group.Strategy == DiscoveryStrategy.ServerAffinity)
                {
                    var remaining = new List<int>(indexes); indexes.Clear();
                    long total = 0;
                    foreach (var index in remaining) total += group.Weight(index);
                    while (total > 0)
                    {
                        var target = sample() * total;
                        long cumulative = 0; var selected = -1;
                        for (var i = 0; i < remaining.Count; i++)
                        {
                            var weight = group.Weight(remaining[i]);
                            if (weight == 0) continue;
                            selected = i; cumulative += weight;
                            if (target < cumulative) break;
                        }
                        var index = remaining[selected];
                        indexes.Add(index); total -= group.Weight(index); remaining.RemoveAt(selected);
                    }
                    indexes.AddRange(remaining);
                }
                var start = 0;
                if (group.Strategy == DiscoveryStrategy.Inherit && balance == OceanBaseLoadBalance.RoundRobin)
                {
                    rotation ??= selector.NextRotation();
                    start = (int)(rotation.Value % (uint)group.Count);
                }
                var ordered = new OceanBaseEndpoint[group.Count];
                for (var i = 0; i < ordered.Length; i++) ordered[i] = group.Endpoint(indexes[(start + i) % group.Count]);
                result.AddRange(healthSeconds > 0 ? health.Order(ordered, 0, healthSeconds) : ordered);
            }
            return result;
        }
    }
}
