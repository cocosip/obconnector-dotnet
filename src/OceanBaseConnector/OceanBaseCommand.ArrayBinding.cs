using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseCommand
    {
        /// <summary>The last started array execution, including proven partial results after failure; null before SQL was sent.</summary>
        public OceanBaseArrayBindingResult? ArrayBindingResult { get; private set; }

        /// <summary>Executes Oracle parameter columns in one request within the caller's explicit transaction.</summary>
        /// <remarks>Each input is a factory anonymous array of rowCount elements. OUT/RETURNING requires an explicit
        /// anonymous array type and publishes object[][] grouped by input position only after a validated terminal OK.
        /// Item errors are returned in the result; protocol or terminal errors throw and preserve old output values.
        /// FetchSize is accepted as a hint; internal batch results do not use a fetch cursor.
        /// No commit, rollback or replay is performed.</remarks>
        public OceanBaseArrayBindingResult ExecuteArray(int rowCount)
        {
            ValidateArrayCount(rowCount);
            using var reader = (OceanBaseDataReader)ExecuteReaderCore(CommandBehavior.Default, CancellationToken.None, rowCount);
            return reader.ArrayBindingResult ?? throw new InvalidOperationException("Array execution has no completed result.");
        }

        /// <summary>Asynchronously executes Oracle array binding; see ExecuteArray for output and transaction semantics.</summary>
        public async Task<OceanBaseArrayBindingResult> ExecuteArrayAsync(int rowCount, CancellationToken cancellationToken = default)
        {
            ValidateArrayCount(rowCount);
            using var reader = (OceanBaseDataReader)await ExecuteReaderCoreAsync(CommandBehavior.Default, cancellationToken, rowCount).ConfigureAwait(false);
            return reader.ArrayBindingResult ?? throw new InvalidOperationException("Array execution has no completed result.");
        }

        private static void ValidateArrayCount(int count)
        { if (count < 1 || count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(count)); }
    }
}
