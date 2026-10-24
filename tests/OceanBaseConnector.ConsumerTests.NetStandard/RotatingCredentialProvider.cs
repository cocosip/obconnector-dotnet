using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.ConsumerTests.NetStandard
{
    // Loaded beside each product asset to preserve the provider interface's assembly identity.
    public sealed class RotatingCredentialProvider : IOceanBaseCredentialProvider
    {
        private readonly bool _asynchronous;
        private int _calls;
        public RotatingCredentialProvider(bool asynchronous) => _asynchronous = asynchronous;
        public long Version { get; set; } = 1;
        public bool Fail { get; set; }
        public int Calls => _calls;
        public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token)
        {
            if (_asynchronous) throw new InvalidOperationException("Unexpected synchronous credential acquisition.");
            return Snapshot(context, token);
        }
        public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token)
        {
            if (!_asynchronous) throw new InvalidOperationException("Unexpected asynchronous credential acquisition.");
            return Task.FromResult(Snapshot(context, token));
        }
        private OceanBaseCredential Snapshot(OceanBaseCredentialContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls);
            if (Fail) throw new InvalidOperationException("consumer-provider-secret");
            if (context.UserId != "configured-user") throw new InvalidOperationException("Unexpected configured identity.");
            return new OceanBaseCredential("consumer-user", "consumer-secret-" + Version, Version);
        }
    }
}
