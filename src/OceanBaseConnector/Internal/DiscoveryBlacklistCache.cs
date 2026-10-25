using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OceanBaseConnector.Internal
{
    internal sealed class DiscoveryBlacklistCache
    {
        private const int EntryCapacity = 1024;
        private const int EventCapacity = 16384;
        private readonly object _gate = new object();
        private readonly Func<long> _clock;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private int _events;
        private long _sequence;

        internal DiscoveryBlacklistCache(Func<long>? clock = null) { _clock = clock ?? Stopwatch.GetTimestamp; }
        internal int Count { get { lock (_gate) return _entries.Count; } }
        internal int FailureEventCount { get { lock (_gate) return _events; } }

        internal bool IsBlocked(OceanBaseEndpoint endpoint, DiscoveryBlacklistPolicy policy)
        {
            lock (_gate)
                return _entries.TryGetValue(Key(endpoint, policy), out var entry) && entry.BlockedAt.HasValue &&
                    _clock() - entry.BlockedAt.Value < policy.TimeoutSeconds * (double)Stopwatch.Frequency;
        }

        internal bool Failed(OceanBaseEndpoint endpoint, DiscoveryBlacklistPolicy policy)
        {
            lock (_gate)
            {
                var now = _clock(); var key = Key(endpoint, policy); var entry = GetOrCreate(key);
                entry.LastFeedback = ++_sequence;
                // An expired blocked entry is a probe: another failure immediately restarts its cooldown.
                if (entry.BlockedAt.HasValue || !policy.RetryDuration || policy.RetryTimes == 1)
                {
                    Block(entry, now); return true;
                }
                while (entry.Failures.Count != 0 && now - entry.Failures.Peek() > policy.DurationMilliseconds * (double)Stopwatch.Frequency / 1000)
                {
                    entry.Failures.Dequeue(); _events--;
                }
                while (_events >= EventCapacity) EvictOldest();
                entry = GetOrCreate(key);
                entry.LastFeedback = ++_sequence;
                entry.Failures.Enqueue(now); _events++;
                if (entry.Failures.Count < policy.RetryTimes) return false;
                Block(entry, now); return true;
            }
        }

        internal void Succeeded(OceanBaseEndpoint endpoint, DiscoveryBlacklistPolicy policy)
        {
            lock (_gate) Remove(Key(endpoint, policy));
        }

        private static string Key(OceanBaseEndpoint endpoint, DiscoveryBlacklistPolicy policy) => policy.Key + "/" + endpoint.ToString();
        private Entry GetOrCreate(string key)
        {
            if (_entries.TryGetValue(key, out var entry)) return entry;
            if (_entries.Count == EntryCapacity) EvictOldest();
            entry = new Entry { LastFeedback = ++_sequence }; _entries.Add(key, entry); return entry;
        }
        private void Block(Entry entry, long now)
        {
            _events -= entry.Failures.Count; entry.Failures.Clear(); entry.BlockedAt = now;
        }
        private void Remove(string key)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            _events -= entry.Failures.Count; _entries.Remove(key);
        }
        private void EvictOldest()
        {
            string? oldest = null; var sequence = long.MaxValue;
            foreach (var pair in _entries)
                if (pair.Value.LastFeedback < sequence) { oldest = pair.Key; sequence = pair.Value.LastFeedback; }
            if (oldest != null) Remove(oldest);
        }
        private sealed class Entry
        {
            internal long LastFeedback;
            internal long? BlockedAt;
            internal Queue<long> Failures { get; } = new Queue<long>();
        }
    }
}
