using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private OceanBaseTransaction? _transaction;
        private long _transactionEpoch;
        internal long TransactionEpoch { get { lock (_gate) return _transactionEpoch; } }

        internal CommandExchange StartRefCursor(PhysicalSession session, OceanBaseTransaction? transaction, long epoch, int timeout, CancellationToken token, Action release)
        {
            RejectAmbientTransaction();
            lock (_gate)
            {
                if (epoch != _transactionEpoch) throw new InvalidOperationException("The REF CURSOR transaction context has changed.");
                return StartCommand(session, transaction, timeout, token, release);
            }
        }

        internal PhysicalSession GetCommandSession(OceanBaseTransaction? transaction, object? owner = null)
        {
            lock (_gate)
            {
                var session = GetOpenSession(owner);
                ValidateCommandTransaction(session, transaction);
                return session;
            }
        }

        private void ValidateCommandTransaction(PhysicalSession session, OceanBaseTransaction? transaction)
        {
            if (!ReferenceEquals(_session, session) || !ReferenceEquals(_transaction, transaction) ||
                (transaction != null && (transaction.Disposed || transaction.InProgress || !ReferenceEquals(transaction.Session, session))))
                throw new InvalidOperationException("The command must reference the connection's active transaction, or null outside a transaction.");
        }

        internal CommandExchange StartCommand(PhysicalSession session, OceanBaseTransaction? transaction, int timeout, CancellationToken token, Action release, object? owner = null, Func<CancellationToken>? cancellationOrigin = null)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ValidateCommandTransaction(session, transaction);
                return new CommandExchange(session, _log, timeout, token, release, owner: owner, cancellationOrigin: cancellationOrigin);
            }
        }

        internal bool IsActiveTransaction(OceanBaseTransaction transaction)
        {
            lock (_gate) return ReferenceEquals(_transaction, transaction) && !transaction.Disposed &&
                ReferenceEquals(_session, transaction.Session) && (transaction.Session.State == SessionState.Open || transaction.Session.State == SessionState.Busy);
        }

        internal static (IsolationLevel Level, string Sql) TransactionIsolation(OceanBaseCompatibilityMode mode, IsolationLevel level)
        {
            if (level == IsolationLevel.Unspecified)
                level = mode == OceanBaseCompatibilityMode.Oracle ? IsolationLevel.ReadCommitted : IsolationLevel.RepeatableRead;
            string sql;
            switch (level)
            {
                case IsolationLevel.ReadCommitted: sql = "READ COMMITTED"; break;
                case IsolationLevel.Serializable: sql = "SERIALIZABLE"; break;
                case IsolationLevel.ReadUncommitted when mode == OceanBaseCompatibilityMode.MySql: sql = "READ UNCOMMITTED"; break;
                case IsolationLevel.RepeatableRead when mode == OceanBaseCompatibilityMode.MySql: sql = "REPEATABLE READ"; break;
                default: throw new NotSupportedException("This transaction isolation level is not supported in the authenticated mode.");
            }
            return (level, "SET SESSION TRANSACTION ISOLATION LEVEL " + sql);
        }

        private (OceanBaseTransaction Transaction, CommandExchange Exchange, string IsolationSql) StartTransaction(IsolationLevel level, CancellationToken token)
        {
            RejectAmbientTransaction();
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var session = GetOpenSession();
                if (_transaction != null) throw new InvalidOperationException("A local transaction is already active.");
                var isolation = TransactionIsolation(session.Info!.Mode, level);
                var exchange = new CommandExchange(session, _log, _builder.CommandTimeout, token, () => { }, logCommand: false);
                var transaction = new OceanBaseTransaction(this, session, isolation.Level, _builder.CommandTimeout) { InProgress = true };
                _transactionEpoch++;
                _transaction = transaction;
                return (transaction, exchange, isolation.Sql);
            }
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            var operation = StartTransaction(isolationLevel, CancellationToken.None);
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("transaction.begin");
            operation.Exchange.SetTraceContext(diagnostics?.Context ?? default);
            try
            {
                Control(operation.Exchange, operation.IsolationSql);
                Control(operation.Exchange, "set autocommit = 0", false);
                PublishTransaction(operation.Transaction, operation.Exchange);
                diagnostics?.Finish(operation.Transaction.Session.Info!.Mode, null);
                LogTransaction("Begin", operation.Transaction, timer, null);
                return operation.Transaction;
            }
            catch (Exception error) { var mapped = BeginFailed(operation.Transaction, operation.Exchange, timer, error); diagnostics?.Finish(operation.Transaction.Session.Info!.Mode, mapped); throw mapped; }
        }

#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            var operation = StartTransaction(isolationLevel, cancellationToken);
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("transaction.begin");
            operation.Exchange.SetTraceContext(diagnostics?.Context ?? default);
            try
            {
                await ControlAsync(operation.Exchange, operation.IsolationSql).ConfigureAwait(false);
                await ControlAsync(operation.Exchange, "set autocommit = 0", false).ConfigureAwait(false);
                PublishTransaction(operation.Transaction, operation.Exchange);
                diagnostics?.Finish(operation.Transaction.Session.Info!.Mode, null);
                LogTransaction("Begin", operation.Transaction, timer, null);
                return operation.Transaction;
            }
            catch (Exception error) { var mapped = BeginFailed(operation.Transaction, operation.Exchange, timer, error); diagnostics?.Finish(operation.Transaction.Session.Info!.Mode, mapped); throw mapped; }
        }
#endif

        private void PublishTransaction(OceanBaseTransaction transaction, CommandExchange exchange)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_transaction, transaction) || !ReferenceEquals(_session, transaction.Session))
                    throw new InvalidOperationException("The connection was closed while beginning its transaction.");
                transaction.InProgress = false;
                exchange.Complete();
            }
        }

        private Exception BeginFailed(OceanBaseTransaction transaction, CommandExchange exchange, Stopwatch timer, Exception error)
        {
            var mapped = exchange.Fail(error);
            // A partially changed session cannot be handed back as an autocommit connection.
            if (exchange.HasSentSql) transaction.Session.Break();
            DetachTransaction(transaction);
            LogTransaction("Begin", transaction, timer, mapped);
            return mapped;
        }

        private CommandExchange StartTransactionEnd(OceanBaseTransaction transaction, CancellationToken token)
        {
            RejectAmbientTransaction();
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!IsActiveTransaction(transaction) || transaction.InProgress)
                    throw new InvalidOperationException("The transaction has ended or has an active operation.");
                var exchange = new CommandExchange(transaction.Session, _log, transaction.Timeout, token, () => { }, logCommand: false);
                transaction.InProgress = true;
                return exchange;
            }
        }

        internal void EndTransaction(OceanBaseTransaction transaction, bool commit) =>
            FinishTransaction(transaction, StartTransactionEnd(transaction, CancellationToken.None), commit);

        internal Task EndTransactionAsync(OceanBaseTransaction transaction, bool commit, CancellationToken token) =>
            FinishTransactionAsync(transaction, StartTransactionEnd(transaction, token), commit);

        private void FinishTransaction(OceanBaseTransaction transaction, CommandExchange exchange, bool commit)
        {
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start(commit ? "transaction.commit" : "transaction.rollback");
            exchange.SetTraceContext(diagnostics?.Context ?? default);
            Exception? restoreError = null;
            try
            {
                Control(exchange, commit ? "COMMIT" : "ROLLBACK", transactionEnded: true);
            }
            catch (Exception error) { var mapped = EndFailed(transaction, exchange, timer, commit, error); diagnostics?.Finish(transaction.Session.Info!.Mode, mapped); throw mapped; }
            try { Control(exchange, "set autocommit = 1", true, true); }
            catch (Exception error) { restoreError = error; transaction.Session.Break(); }
            DetachTransaction(transaction);
            exchange.Complete();
            diagnostics?.Finish(transaction.Session.Info!.Mode, null);
            if (restoreError != null) LogTransaction("RestoreAutocommit", transaction, timer, restoreError);
            LogTransaction(commit ? "Commit" : "Rollback", transaction, timer, null);
        }

        private async Task FinishTransactionAsync(OceanBaseTransaction transaction, CommandExchange exchange, bool commit)
        {
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start(commit ? "transaction.commit" : "transaction.rollback");
            exchange.SetTraceContext(diagnostics?.Context ?? default);
            Exception? restoreError = null;
            try
            {
                await ControlAsync(exchange, commit ? "COMMIT" : "ROLLBACK", transactionEnded: true).ConfigureAwait(false);
            }
            catch (Exception error) { var mapped = EndFailed(transaction, exchange, timer, commit, error); diagnostics?.Finish(transaction.Session.Info!.Mode, mapped); throw mapped; }
            try { await ControlAsync(exchange, "set autocommit = 1", true, true).ConfigureAwait(false); }
            catch (Exception error) { restoreError = error; transaction.Session.Break(); }
            DetachTransaction(transaction);
            exchange.Complete();
            diagnostics?.Finish(transaction.Session.Info!.Mode, null);
            if (restoreError != null) LogTransaction("RestoreAutocommit", transaction, timer, restoreError);
            LogTransaction(commit ? "Commit" : "Rollback", transaction, timer, null);
        }

        private Exception EndFailed(OceanBaseTransaction transaction, CommandExchange exchange, Stopwatch timer, bool commit, Exception error)
        {
            var mapped = exchange.Fail(error);
            lock (_gate)
            {
                transaction.InProgress = false;
                if (transaction.Session.State != SessionState.Open) DetachTransaction(transaction);
            }
            LogTransaction(commit ? "Commit" : "Rollback", transaction, timer, mapped);
            return mapped;
        }

        private void DetachTransaction(OceanBaseTransaction transaction)
        {
            lock (_gate)
            {
                transaction.InProgress = false;
                if (ReferenceEquals(_transaction, transaction)) _transaction = null;
            }
        }

        private static void Control(CommandExchange exchange, string sql, bool? autocommit = null, bool transactionEnded = false)
        {
            try
            {
                exchange.Send(OceanBaseCommand.SqlPacket(sql, 3));
                CheckControl(exchange, exchange.Receive(), autocommit, transactionEnded);
            }
            catch (Exception error) { throw exchange.Fail(error); }
        }

        private static async Task ControlAsync(CommandExchange exchange, string sql, bool? autocommit = null, bool transactionEnded = false)
        {
            try
            {
                await exchange.SendAsync(OceanBaseCommand.SqlPacket(sql, 3)).ConfigureAwait(false);
                var packet = await exchange.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                CheckControl(exchange, packet, autocommit, transactionEnded);
            }
            catch (Exception error) { throw exchange.Fail(error); }
        }

        private static void CheckControl(CommandExchange exchange, byte[] packet, bool? autocommit, bool transactionEnded)
        {
            var ok = OkPacket.Parse(packet, exchange.Capabilities);
            if ((ok.StatusFlags & ServerStatusFlags.MoreResultsExists) != 0 ||
                (transactionEnded && (ok.StatusFlags & ServerStatusFlags.InTransaction) != 0) ||
                (autocommit.HasValue && ((ok.StatusFlags & ServerStatusFlags.Autocommit) != 0) != autocommit.Value))
                throw new ProtocolException("The transaction control response did not confirm the requested state.");
            exchange.ObserveSessionState(ok);
            exchange.ResponseComplete();
        }

        private void LogTransaction(string operation, OceanBaseTransaction transaction, Stopwatch timer, Exception? error) =>
            _log.TransactionFinished(operation, transaction.Session.Endpoint, transaction.Session.Info!.Mode, timer.ElapsedMilliseconds, error);

        private CommandExchange? StartTransactionCleanup(OceanBaseTransaction transaction)
        {
            lock (_gate)
            {
                if (transaction.Disposed) return null;
                transaction.Disposed = true;
                if (!ReferenceEquals(_transaction, transaction)) return null;
                if (transaction.InProgress || transaction.Session.State != SessionState.Open)
                { DetachTransaction(transaction); transaction.Session.Break(); return null; }
                try
                {
                    var exchange = new CommandExchange(transaction.Session, _log, transaction.Timeout, CancellationToken.None, () => { }, logCommand: false);
                    transaction.InProgress = true;
                    return exchange;
                }
                catch { DetachTransaction(transaction); transaction.Session.Break(); return null; }
            }
        }

        internal void DisposeTransaction(OceanBaseTransaction transaction)
        {
            var exchange = StartTransactionCleanup(transaction);
            if (exchange != null) CleanupTransaction(transaction, exchange);
        }

        internal async Task DisposeTransactionAsync(OceanBaseTransaction transaction)
        {
            var exchange = StartTransactionCleanup(transaction);
            if (exchange != null) await CleanupTransactionAsync(transaction, exchange).ConfigureAwait(false);
        }

        private void CleanupTransaction(OceanBaseTransaction transaction, CommandExchange exchange)
        {
            try { FinishTransaction(transaction, exchange, false); }
            catch { DetachTransaction(transaction); transaction.Session.Break(); }
        }

        private async Task CleanupTransactionAsync(OceanBaseTransaction transaction, CommandExchange exchange)
        {
            try { await FinishTransactionAsync(transaction, exchange, false).ConfigureAwait(false); }
            catch { DetachTransaction(transaction); transaction.Session.Break(); }
        }

        private (PhysicalSession? Session, OceanBaseTransaction? Transaction, CommandExchange? Exchange, ConnectionState Previous, string? Endpoint) DetachForClose(PhysicalSession? requiredSession = null)
        {
            lock (_gate)
            {
                if (requiredSession != null && !ReferenceEquals(_session, requiredSession)) return (null, null, null, ConnectionState.Closed, null);
                var previous = State;
                var session = _session;
                var transaction = _transaction;
                _session = null;
                _transaction = null;
                CommandExchange? exchange = null;
                if (transaction != null && !transaction.InProgress && session?.State == SessionState.Open)
                {
                    try { exchange = new CommandExchange(session, _log, transaction.Timeout, CancellationToken.None, () => { }, logCommand: false); }
                    catch { session.Break(); }
                }
                return (session, transaction, exchange, previous, session?.Endpoint);
            }
        }

        private void FinishClose(PhysicalSession? session, string? endpoint)
        {
            session?.Dispose();
            if (session != null) _log.Closed(endpoint!);
        }
    }
}
