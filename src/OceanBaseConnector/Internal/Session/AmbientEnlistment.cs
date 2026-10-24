using System;
using System.Transactions;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class AmbientEnlistment : IEnlistmentNotification
    {
        private readonly OceanBaseConnection _connection;
        private readonly Transaction _transaction;
        private int _active = 1;

        internal AmbientEnlistment(OceanBaseConnection connection, Transaction transaction)
        { _connection = connection; _transaction = transaction; }

        internal void Deactivate() => System.Threading.Interlocked.Exchange(ref _active, 0);

        public void Prepare(PreparingEnlistment preparingEnlistment)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _active, 1, 1) == 0)
            { preparingEnlistment.ForceRollback(); return; }
            try { _connection.PrepareAmbientTransaction(_transaction); preparingEnlistment.Prepared(); }
            catch (Exception error) { preparingEnlistment.ForceRollback(error); }
        }

        public void Commit(Enlistment enlistment)
        {
            try
            {
                if (System.Threading.Interlocked.CompareExchange(ref _active, 1, 1) != 0)
                    _connection.EndAmbientTransaction(_transaction, commit: true);
            }
            finally { enlistment.Done(); }
        }

        public void Rollback(Enlistment enlistment)
        {
            try
            {
                if (System.Threading.Interlocked.CompareExchange(ref _active, 1, 1) != 0)
                    _connection.EndAmbientTransaction(_transaction, commit: false);
            }
            finally { enlistment.Done(); }
        }

        public void InDoubt(Enlistment enlistment)
        {
            try
            {
                if (System.Threading.Interlocked.CompareExchange(ref _active, 1, 1) != 0)
                    _connection.EndAmbientInDoubt(_transaction);
            }
            finally { enlistment.Done(); }
        }
    }
}
