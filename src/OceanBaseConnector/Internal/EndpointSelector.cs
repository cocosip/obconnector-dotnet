using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal sealed class EndpointSelector
    {
        private int _next = -1;

        internal int SelectStart(int count, OceanBaseLoadBalance balance) => balance == OceanBaseLoadBalance.RoundRobin
            ? (int)(unchecked((uint)Interlocked.Increment(ref _next)) % (uint)count) : 0;
    }
}
