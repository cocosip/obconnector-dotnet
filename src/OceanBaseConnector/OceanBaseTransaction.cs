using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    // No transaction instances are handed out until local transactions are implemented.
    public sealed class OceanBaseTransaction : DbTransaction
    {
        private OceanBaseTransaction() { }
        public override IsolationLevel IsolationLevel => throw Unsupported();
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw Unsupported();
        public override void Rollback() => throw Unsupported();
#if NET8_0_OR_GREATER
        public override Task CommitAsync(CancellationToken cancellationToken = default) => Task.FromException(Unsupported());
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => Task.FromException(Unsupported());
#endif
        private static NotSupportedException Unsupported() => new NotSupportedException("Local transactions are not implemented by the current provider stage.");
    }
}
