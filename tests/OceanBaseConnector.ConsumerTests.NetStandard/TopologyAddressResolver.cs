using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.ConsumerTests.NetStandard
{
    // Loaded beside each product asset so the interface identity stays in its isolated context.
    public sealed class TopologyAddressResolver : IOceanBaseAddressResolver
    {
        public string Addresses { get; set; }
        private readonly bool _asynchronous;
        public int Calls { get; private set; }
        public TopologyAddressResolver(string addresses, bool asynchronous) { Addresses = addresses; _asynchronous = asynchronous; }
        public string Resolve(CancellationToken cancellationToken)
        {
            if (_asynchronous) throw new InvalidOperationException("Unexpected synchronous resolution.");
            cancellationToken.ThrowIfCancellationRequested(); Calls++; return Addresses;
        }
        public Task<string> ResolveAsync(CancellationToken cancellationToken)
        {
            if (!_asynchronous) throw new InvalidOperationException("Unexpected asynchronous resolution.");
            cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Addresses);
        }
    }
}
