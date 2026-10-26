using System;
using System.Collections.Generic;

namespace OceanBaseConnector.Internal
{
    internal sealed class EndpointAttemptPlan
    {
        private readonly IReadOnlyList<OceanBaseEndpoint> _ordered;
        private readonly int[] _sizes;
        private readonly DiscoveryBlacklistPolicy?[] _policies;
        private readonly bool _hasBlacklist;
        private DiscoveryBlacklistPolicy? _lastPolicy;
        private readonly DiscoveryBlacklistCache _blacklist;
        private readonly int _ceiling;
        private readonly int _rounds;
        private readonly Queue<int> _pending = new Queue<int>();
        private int _attempts, _round, _group, _offset, _lastIndex;
        private bool _fallback, _passAttempted;

        internal EndpointAttemptPlan(IReadOnlyList<OceanBaseEndpoint> endpoints, OceanBaseConnectionOptions options, EndpointSelector selector, EndpointHealthCache health)
        {
            if (endpoints is DiscoverySnapshot snapshot)
            {
                _ordered = snapshot.Order(selector, options.LoadBalance, options.HostRecheckSeconds, health: health);
                _policies = snapshot.CopyGroupPolicies();
                foreach (var policy in _policies) _hasBlacklist |= policy != null;
                _sizes = snapshot.CopyGroupSizes();
                _ceiling = snapshot.RetryAllDowns ?? endpoints.Count * (options.ConnectRetryCount + 1);
            }
            else
            {
                var start = selector.SelectStart(endpoints.Count, options.LoadBalance);
                if (options.HostRecheckSeconds > 0) _ordered = health.Order(endpoints, start, options.HostRecheckSeconds);
                else
                {
                    var ordered = new OceanBaseEndpoint[endpoints.Count];
                    for (var i = 0; i < ordered.Length; i++) ordered[i] = endpoints[(start + i) % endpoints.Count];
                    _ordered = ordered;
                }
                _policies = new DiscoveryBlacklistPolicy?[1];
                _sizes = new[] { endpoints.Count }; _ceiling = endpoints.Count * (options.ConnectRetryCount + 1);
            }
            _rounds = options.ConnectRetryCount + 1; _blacklist = health.Blacklist;
            FillGroup();
        }

        internal bool TryNext(out OceanBaseEndpoint endpoint, out bool delay)
        {
            endpoint = null!; delay = false;
            while (_attempts < _ceiling && _round < _rounds)
            {
                while (_pending.Count != 0)
                {
                    var index = _pending.Dequeue(); var candidate = _ordered[index];
                    var policy = _policies[_group];
                    if (_fallback && policy == null) continue;
                    if (policy != null && !_fallback && _blacklist.IsBlocked(candidate, policy)) continue;
                    _lastPolicy = policy;
                    _attempts++; _lastIndex = index; _passAttempted = true; endpoint = candidate; return true;
                }
                _offset += _sizes[_group]; _group++;
                if (_group < _sizes.Length) { FillGroup(); continue; }
                delay |= _passAttempted; _passAttempted = false;
                _group = 0; _offset = 0;
                if (_hasBlacklist && !_fallback) _fallback = true;
                else { _fallback = false; _round++; }
                if (_round < _rounds) FillGroup();
            }
            return false;
        }

        internal void Failed()
        {
            if (_lastPolicy == null) return;
            var blocked = _blacklist.Failed(_ordered[_lastIndex], _lastPolicy);
            if (!_fallback && _lastPolicy.RetryDuration && !blocked) _pending.Enqueue(_lastIndex);
        }
        internal void Succeeded()
        {
            if (_lastPolicy != null) _blacklist.Succeeded(_ordered[_lastIndex], _lastPolicy);
        }
        private void FillGroup()
        {
            for (var i = 0; i < _sizes[_group]; i++) _pending.Enqueue(_offset + i);
        }
    }
}
