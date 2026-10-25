using System;
using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal sealed class EndpointSelector
    {
        private int _next = -1;
        // Framework Random seeds by clock tick; a shared source avoids identical burst-connection routes.
        private static readonly Random SharedRandom = new Random();
        private readonly Random _random;
        internal EndpointSelector(Random? random = null) { _random = random ?? SharedRandom; }
        internal EndpointHealthCache Health { get; } = new EndpointHealthCache();

        internal uint NextRotation() => unchecked((uint)Interlocked.Increment(ref _next));
        internal double NextDouble() { lock (_random) return _random.NextDouble(); }

        internal int SelectStart(int count, OceanBaseLoadBalance balance) => balance == OceanBaseLoadBalance.RoundRobin
            ? (int)(NextRotation() % (uint)count) : 0;
    }
}
