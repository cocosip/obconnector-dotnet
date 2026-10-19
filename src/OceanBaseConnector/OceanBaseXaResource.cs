using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public enum OceanBaseXaStartMode { New, Join, Resume }
    public enum OceanBaseXaEndMode { Success, Fail, Suspend }
    public enum OceanBaseXaVote { Prepared = 0, ReadOnly = 3 }

    /// <summary>A dedicated explicit XA resource bound to one physical session until Close.
    /// It never enlists ambient transactions, replays work or performs implicit completion.</summary>
    public sealed class OceanBaseXaResource
    {
        private readonly OceanBaseConnection _connection;
        internal PhysicalSession Session { get; }
        internal int Timeout { get; }
        // All mutable resource state is protected by the connection gate.
        internal XaState State { get; set; }
        internal OceanBaseXid? Xid { get; set; }
        internal bool RollbackOnly { get; set; }
        internal bool InProgress { get; set; }
        internal bool OracleInitialized { get; set; }
        internal bool AllowsCommands => State == XaState.Active && !InProgress;
        internal OceanBaseXaResource(OceanBaseConnection connection, PhysicalSession session, int timeout)
        { _connection = connection; Session = session; Timeout = timeout; }

        public void Start(OceanBaseXid xid, OceanBaseXaStartMode mode = OceanBaseXaStartMode.New)
            => _connection.ExecuteXa(this, XaOperation.Start, xid, (int)mode, false, CancellationToken.None).GetAwaiter().GetResult();
        public Task StartAsync(OceanBaseXid xid, OceanBaseXaStartMode mode = OceanBaseXaStartMode.New, CancellationToken cancellationToken = default)
            => _connection.ExecuteXa(this, XaOperation.Start, xid, (int)mode, true, cancellationToken);
        public void End(OceanBaseXid xid, OceanBaseXaEndMode mode = OceanBaseXaEndMode.Success)
            => _connection.ExecuteXa(this, XaOperation.End, xid, (int)mode, false, CancellationToken.None).GetAwaiter().GetResult();
        public Task EndAsync(OceanBaseXid xid, OceanBaseXaEndMode mode = OceanBaseXaEndMode.Success, CancellationToken cancellationToken = default)
            => _connection.ExecuteXa(this, XaOperation.End, xid, (int)mode, true, cancellationToken);
        public OceanBaseXaVote Prepare(OceanBaseXid xid)
            => _connection.ExecuteXa(this, XaOperation.Prepare, xid, 0, false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<OceanBaseXaVote> PrepareAsync(OceanBaseXid xid, CancellationToken cancellationToken = default)
            => _connection.ExecuteXa(this, XaOperation.Prepare, xid, 0, true, cancellationToken);
        public void Commit(OceanBaseXid xid, bool onePhase = false)
            => _connection.ExecuteXa(this, XaOperation.Commit, xid, onePhase ? 1 : 0, false, CancellationToken.None).GetAwaiter().GetResult();
        public Task CommitAsync(OceanBaseXid xid, bool onePhase = false, CancellationToken cancellationToken = default)
            => _connection.ExecuteXa(this, XaOperation.Commit, xid, onePhase ? 1 : 0, true, cancellationToken);
        public void Rollback(OceanBaseXid xid)
            => _connection.ExecuteXa(this, XaOperation.Rollback, xid, 0, false, CancellationToken.None).GetAwaiter().GetResult();
        public Task RollbackAsync(OceanBaseXid xid, CancellationToken cancellationToken = default)
            => _connection.ExecuteXa(this, XaOperation.Rollback, xid, 0, true, cancellationToken);
        /// <summary>Returns a complete prepared-branch snapshot; never returns partial results.</summary>
        public OceanBaseXid[] Recover()
            => _connection.RecoverXa(this, false, CancellationToken.None).GetAwaiter().GetResult();
        public Task<OceanBaseXid[]> RecoverAsync(CancellationToken cancellationToken = default)
            => _connection.RecoverXa(this, true, cancellationToken);
    }

    internal enum XaState { Idle, Active, Ended, Suspended, Prepared }
    internal enum XaOperation { Start, End, Prepare, Commit, Rollback, Recover }
}
