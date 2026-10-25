#if NET8_0_OR_GREATER
using System.Data.Common;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        public override bool CanCreateBatch => true;
        protected override DbBatch CreateDbBatch()
        {
            lock (_gate) { ThrowIfDisposed(); return new OceanBaseBatch { Connection = this, Timeout = _builder.CommandTimeout }; }
        }
        internal PhysicalSession ReserveBatch(OceanBaseTransaction? transaction, object owner, out long openGeneration)
        {
            lock (_gate)
            {
                var session = GetCommandSession(transaction);
                session.Reserve(owner);
                openGeneration = _openGeneration;
                return session;
            }
        }
    }
}
#endif
