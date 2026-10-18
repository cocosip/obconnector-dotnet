using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed class OceanBaseTransaction : DbTransaction
    {
        private readonly OceanBaseConnection _owner;
        internal PhysicalSession Session { get; }
        internal int Timeout { get; }
        // Lifecycle fields are protected by the owning connection's gate.
        internal bool InProgress { get; set; }
        internal bool Disposed { get; set; }
        internal OceanBaseTransaction(OceanBaseConnection owner, PhysicalSession session, IsolationLevel isolation, int timeout)
        { _owner = owner; Session = session; IsolationLevel = isolation; Timeout = timeout; }
        public override IsolationLevel IsolationLevel { get; }
        protected override DbConnection? DbConnection => _owner.IsActiveTransaction(this) ? _owner : null;
        public override void Commit() => _owner.EndTransaction(this, true);
        public override void Rollback() => _owner.EndTransaction(this, false);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _owner.DisposeTransaction(this);
            base.Dispose(disposing);
        }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override Task CommitAsync(CancellationToken cancellationToken = default) => _owner.EndTransactionAsync(this, true, cancellationToken);
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => _owner.EndTransactionAsync(this, false, cancellationToken);
        public override async ValueTask DisposeAsync()
        {
            await _owner.DisposeTransactionAsync(this).ConfigureAwait(false);
            base.Dispose(true);
            GC.SuppressFinalize(this);
        }
#endif
    }
}
