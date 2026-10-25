using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    // A statement or fetched cursor must outlive every cursor returned by that execution.
    internal sealed class OracleCursorOwner
    {
        private readonly object _gate = new object();
        private readonly Dictionary<uint, OceanBaseRefCursor> _cursors = new Dictionary<uint, OceanBaseRefCursor>();
        private readonly HashSet<uint> _published = new HashSet<uint>();
        private readonly OceanBaseRefCursor? _parentCursor;
        private readonly HashSet<uint> _identifiers;
        private uint? _statement;
        private int _remaining;
        private bool _complete;
        private bool _closed;
        private bool _discardOnReturn;
        internal OceanBaseConnection Connection { get; }
        internal PhysicalSession Session { get; }
        internal OceanBaseTransaction? Transaction { get; }
        internal long Epoch { get; }
        internal int Timeout { get; }
        internal bool HasParent { get { lock (_gate) return _statement.HasValue || _parentCursor != null; } }

        internal OracleCursorOwner(OceanBaseConnection connection, PhysicalSession session, OceanBaseTransaction? transaction, int timeout)
        { Connection = connection; Session = session; Transaction = transaction; Timeout = timeout; Epoch = connection.TransactionEpoch; _identifiers = new HashSet<uint>(); }

        internal OracleCursorOwner(OceanBaseRefCursor parent)
            : this(parent.Owner.Connection, parent.Owner.Session, parent.Owner.Transaction, parent.Owner.Timeout)
        { _parentCursor = parent; _identifiers = parent.Owner._identifiers; }

        internal void SetStatement(uint id)
        {
            lock (_gate)
            {
                if (_statement.HasValue && _statement.Value != id) Session.QueueStatementClose(_statement.Value);
                _statement = id;
            }
        }
        internal OceanBaseRefCursor Create(uint id)
        {
            lock (_gate)
            {
                if (_cursors.TryGetValue(id, out var cursor)) return cursor;
                if (_complete) throw new InvalidOperationException("The cursor execution has completed.");
                for (var parent = _parentCursor; parent != null; parent = parent.Owner._parentCursor)
                    if (parent.Id == id) throw new ProtocolException("A cursor result cannot reference an ancestor cursor.");
                if (id == 0) throw new ProtocolException("A non-null cursor requires a nonzero cursor identifier.");
                // Descendant fetches share the originating execution's resource budget.
                lock (_identifiers)
                {
                    if (_identifiers.Contains(id)) throw new ProtocolException("A cursor identifier is already owned by another result.");
                    if (_identifiers.Count == 4096) throw new ProtocolException("The execution exceeds the 4096 distinct cursor resource limit.");
                    _identifiers.Add(id);
                }
                var value = new OceanBaseRefCursor(this, id);
                _cursors.Add(id, value); _remaining++; return value;
            }
        }
        internal OceanBaseRefCursor Publish(uint id)
        {
            lock (_gate)
            {
                var cursor = Create(id); _published.Add(id); RetainSession(); return cursor;
            }
        }
        private void RetainSession()
        {
            if (_published.Count != 0 && _remaining != 0 && !_discardOnReturn)
            { Session.MarkDiscardOnReturn(); _discardOnReturn = true; }
        }
        internal void EndExecution()
        {
            lock (_gate)
            {
                if (_complete) return;
                _complete = true;
                foreach (var cursor in _cursors.Values) if (!_published.Contains(cursor.Id)) cursor.ReleaseResource();
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
            if (!_complete || _remaining != 0 || _closed) return;
            _closed = true;
            if (_statement.HasValue) { Session.QueueStatementClose(_statement.Value); _statement = null; }
            _parentCursor?.ReleaseResource();
            if (_discardOnReturn) { Session.ReleaseDiscardOnReturn(); _discardOnReturn = false; }
        }
    }
}
