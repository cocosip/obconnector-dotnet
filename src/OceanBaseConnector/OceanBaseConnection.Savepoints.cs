#if NET8_0_OR_GREATER
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private (CommandExchange Exchange, string Sql) StartSavepoint(OceanBaseTransaction transaction, string name, string operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (name is null) throw new ArgumentNullException(nameof(name));
            if (name.Length == 0 || name.Length > 30) throw new ArgumentException("Savepoint names require 1 to 30 ASCII letters, digits or underscores, beginning with a letter.", nameof(name));
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (i > 0 && (c == '_' || (c >= '0' && c <= '9')))))
                    throw new ArgumentException("The savepoint name is not a supported SQL identifier.", nameof(name));
            }
            RejectAmbientTransaction();
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!IsActiveTransaction(transaction) || transaction.InProgress)
                    throw new InvalidOperationException("The transaction has ended or has an active operation.");
                var mysql = transaction.Session.Info!.Mode == OceanBaseCompatibilityMode.MySql;
                if (!mysql && operation == "RELEASE SAVEPOINT")
                    throw new NotSupportedException("Oracle mode does not support releasing savepoints.");
                var sql = (mysql && operation == "ROLLBACK TO" ? "ROLLBACK TO SAVEPOINT" : operation) + " " + (mysql ? "`" + name + "`" : name);
                return (StartTransactionEnd(transaction, token), sql);
            }
        }

        internal void ExecuteSavepoint(OceanBaseTransaction transaction, string name, string operation)
        {
            var request = StartSavepoint(transaction, name, operation, CancellationToken.None);
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("transaction.savepoint");
            request.Exchange.SetTraceContext(diagnostics?.Context ?? default);
            try
            {
                Control(request.Exchange, request.Sql, autocommit: false);
                PublishSavepoint(transaction, operation);
                request.Exchange.Complete();
            }
            catch (Exception error)
            {
                var mapped = SavepointFailed(transaction, request.Exchange, error);
                diagnostics?.Finish(transaction.Session.Info!.Mode, mapped);
                LogTransaction(operation, transaction, timer, mapped);
                throw mapped;
            }
            diagnostics?.Finish(transaction.Session.Info!.Mode, null);
            LogTransaction(operation, transaction, timer, null);
        }

        internal async Task ExecuteSavepointAsync(OceanBaseTransaction transaction, string name, string operation, CancellationToken token)
        {
            var request = StartSavepoint(transaction, name, operation, token);
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("transaction.savepoint");
            request.Exchange.SetTraceContext(diagnostics?.Context ?? default);
            try
            {
                await ControlAsync(request.Exchange, request.Sql, autocommit: false).ConfigureAwait(false);
                PublishSavepoint(transaction, operation);
                request.Exchange.Complete();
            }
            catch (Exception error)
            {
                var mapped = SavepointFailed(transaction, request.Exchange, error);
                diagnostics?.Finish(transaction.Session.Info!.Mode, mapped);
                LogTransaction(operation, transaction, timer, mapped);
                throw mapped;
            }
            diagnostics?.Finish(transaction.Session.Info!.Mode, null);
            LogTransaction(operation, transaction, timer, null);
        }

        private void PublishSavepoint(OceanBaseTransaction transaction, string operation)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_transaction, transaction) && ReferenceEquals(_session, transaction.Session) && operation == "ROLLBACK TO")
                    _transactionEpoch++;
                transaction.InProgress = false;
            }
        }

        private Exception SavepointFailed(OceanBaseTransaction transaction, CommandExchange exchange, Exception error)
        {
            var mapped = exchange.Fail(error);
            lock (_gate)
            {
                transaction.InProgress = false;
                if (transaction.Session.State != SessionState.Open) DetachTransaction(transaction);
            }
            return mapped;
        }
    }
}
#endif
