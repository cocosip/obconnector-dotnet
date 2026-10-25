using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OceanBaseConnector.Internal
{
    // TCP reachability hints only; failed candidates remain available as fallback.
    internal sealed class EndpointHealthCache
    {
        private const int Capacity = 1024;
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _failures = new Dictionary<string, long>(StringComparer.Ordinal);

        internal IReadOnlyList<OceanBaseEndpoint> Order(IReadOnlyList<OceanBaseEndpoint> endpoints, int start, int seconds)
        {
            var ordered = new OceanBaseEndpoint[endpoints.Count];
            lock (_gate)
            {
                Prune(Stopwatch.GetTimestamp(), seconds);
                var index = 0;
                for (var failed = 0; failed != 2; failed++)
                    for (var i = 0; i < endpoints.Count; i++)
                    {
                        var endpoint = endpoints[(start + i) % endpoints.Count];
                        if (_failures.ContainsKey(endpoint.ToString()) == (failed == 1)) ordered[index++] = endpoint;
                    }
            }
            return ordered;
        }

        internal void Failed(OceanBaseEndpoint endpoint, int seconds)
        {
            if (seconds == 0) return;
            var key = endpoint.ToString();
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                Prune(now, seconds);
                if (_failures.Count == Capacity && !_failures.ContainsKey(key))
                {
                    string? oldest = null; var timestamp = long.MaxValue;
                    foreach (var entry in _failures)
                        if (entry.Value < timestamp) { oldest = entry.Key; timestamp = entry.Value; }
                    if (oldest != null) _failures.Remove(oldest);
                }
                _failures[key] = now;
            }
        }

        internal void Succeeded(OceanBaseEndpoint endpoint)
        {
            lock (_gate) _failures.Remove(endpoint.ToString());
        }

        private void Prune(long now, int seconds)
        {
            List<string>? expired = null;
            foreach (var entry in _failures)
                if (now - entry.Value >= seconds * (double)Stopwatch.Frequency)
                    (expired ??= new List<string>()).Add(entry.Key);
            if (expired != null) foreach (var key in expired) _failures.Remove(key);
        }
    }
}
