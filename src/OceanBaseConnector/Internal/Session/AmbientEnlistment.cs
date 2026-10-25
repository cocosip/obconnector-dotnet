using System;
using System.Threading;
using System.Transactions;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class AmbientEnlistment : IPromotableSinglePhaseNotification
    {
        private readonly OceanBaseConnection _connection;
        private readonly Transaction _transaction;
        private readonly long _openGeneration;
        private readonly CancellationToken _openingToken;
        private int _active = 1;

        internal AmbientEnlistment(OceanBaseConnection connection, Transaction transaction, long openGeneration, CancellationToken openingToken)
        { _connection = connection; _transaction = transaction; _openGeneration = openGeneration; _openingToken = openingToken; }

        internal void Deactivate() => System.Threading.Interlocked.Exchange(ref _active, 0);
        internal OceanBaseTransaction? LocalTransaction { get; private set; }

        public void Initialize() => LocalTransaction = _connection.BeginAmbientTransaction(_transaction, _openGeneration, _openingToken);

        public byte[] Promote() => throw new NotSupportedException("Distributed transaction promotion is not supported.");

        public void SinglePhaseCommit(SinglePhaseEnlistment enlistment)
        {
            var error = End(commit: true);
            if (error == null) enlistment.Committed();
            else if (error is OceanBaseException databaseError && databaseError.IsExecutionOutcomeUnknown) enlistment.InDoubt(error);
            else enlistment.Aborted(error);
        }

        public void Rollback(SinglePhaseEnlistment enlistment)
        {
            var error = End(commit: false);
            if (error == null) enlistment.Aborted();
            else enlistment.Aborted(error);
        }

        private Exception? End(bool commit)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _active, 1, 1) == 0 || LocalTransaction == null)
                return new InvalidOperationException("The connection's ambient transaction is no longer active.");
            try
            {
                _connection.EndAmbientTransaction(_transaction, LocalTransaction, commit);
                return null;
            }
            catch (Exception error) { _connection.DisposeTransaction(LocalTransaction); return error; }
        }
    }
}
