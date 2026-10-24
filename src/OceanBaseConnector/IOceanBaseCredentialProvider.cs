using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    /// <summary>Supplies one credential snapshot per logical Open, including pool reuse.
    /// Implementations are caller-owned, concurrency-safe and responsible for caching and
    /// coalescing refreshes. Never return an older version after issuing a newer one.
    /// Callbacks must return promptly and honor cancellation; cancellation callbacks must not throw.</summary>
    public interface IOceanBaseCredentialProvider
    {
        OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken cancellationToken);
        /// <summary>Return a task promptly. Cancellation stops waiting and ignores late results.</summary>
        Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken cancellationToken);
    }
}
