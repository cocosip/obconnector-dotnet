using System;
using System.Collections.Generic;

namespace OceanBaseConnector.Internal.Session
{
    // A procedure statement must outlive every cursor returned by that execution.
    internal sealed class OracleCursorOwner
    {
        private readonly object _gate = new object();
        private readonly List<OceanBaseRefCursor> _cursors = new List<OceanBaseRefCursor>();
        private uint? _statement;
        private int _remaining;
        private bool _published;
        private bool _complete;
        internal OceanBaseConnection Connection { get; }
        internal PhysicalSession Session { get; }
        internal OceanBaseTransaction? Transaction { get; }
        internal long Epoch { get; }
        internal int Timeout { get; }

        internal OracleCursorOwner(OceanBaseConnection connection, PhysicalSession session, OceanBaseTransaction? transaction, int timeout)
        { Connection = connection; Session = session; Transaction = transaction; Timeout = timeout; Epoch = connection.TransactionEpoch; }

        internal void SetStatement(uint id) { lock (_gate) _statement = id; }
        internal OceanBaseRefCursor Create(uint id)
        {
            lock (_gate)
            {
                foreach (var cursor in _cursors) if (cursor.Id == id) return cursor;
                var value = new OceanBaseRefCursor(this, id);
                _cursors.Add(value); _remaining++; return value;
            }
        }
        internal void Publish() { lock (_gate) _published = true; }
        internal void EndExecution()
        {
            lock (_gate)
            {
                _complete = true;
                if (!_published) foreach (var cursor in _cursors) cursor.ReleaseResource();
                CloseParent();
            }
        }
        internal void Release(uint id)
        {
            lock (_gate)
            {
                if (id != _statement) Session.QueueStatementClose(id);
                _remaining--; CloseParent();
            }
        }
        private void CloseParent()
        {
            if (!_complete || _remaining != 0 || !_statement.HasValue) return;
            Session.QueueStatementClose(_statement.Value); _statement = null;
        }
    }
}
