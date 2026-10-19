using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.ConsumerTests.NetStandard
{
    // Loaded beside each product asset so the interface identity stays in its isolated context.
    public sealed class TopologyAddressResolver : IOceanBaseAddressResolver
    {
        private readonly string _addresses;
        private readonly bool _asynchronous;
        public int Calls { get; private set; }
        public TopologyAddressResolver(string addresses, bool asynchronous) { _addresses = addresses; _asynchronous = asynchronous; }
        public string Resolve(CancellationToken cancellationToken)
        {
            if (_asynchronous) throw new InvalidOperationException("Unexpected synchronous resolution.");
            cancellationToken.ThrowIfCancellationRequested(); Calls++; return _addresses;
        }
        public Task<string> ResolveAsync(CancellationToken cancellationToken)
        {
            if (!_asynchronous) throw new InvalidOperationException("Unexpected asynchronous resolution.");
            cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(_addresses);
        }
    }
}
