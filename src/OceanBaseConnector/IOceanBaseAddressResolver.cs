using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    /// <summary>Supplies trusted addresses for the same logical tenant before each Open.
    /// Results use Server syntax and replace its list; omitted ports inherit Port.
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
