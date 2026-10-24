using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    /// <summary>Supplies trusted addresses for the same logical tenant before each logical Open, including pool reuse.
    /// Results use Server syntax and replace its list; omitted ports inherit Port.
    /// An Open uses one snapshot for pool validation and any new physical sessions.
    /// Ordinary connection pools are scoped to the resolver instance by reference identity.
    /// Removed endpoints are retired before reuse; already borrowed sessions are not migrated.
    /// Implementations are caller-owned and must be safe for concurrent data-source opens.
    /// Cancellation callbacks must return promptly and must not throw.</summary>
    public interface IOceanBaseAddressResolver
    {
        /// <summary>Resolve synchronously, honoring cancellation of the total connection budget.</summary>
        string Resolve(CancellationToken cancellationToken);
        /// <summary>Return a task promptly. Cancellation stops waiting; late results cannot open a connection.</summary>
        Task<string> ResolveAsync(CancellationToken cancellationToken);
    }
}
