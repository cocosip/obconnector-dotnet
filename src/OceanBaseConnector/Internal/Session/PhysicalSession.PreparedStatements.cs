using System;
using System.Collections.Generic;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class PhysicalSession
    {
        private Dictionary<string, LinkedListNode<PreparedStatement>>? _statementCache;
        private LinkedList<PreparedStatement>? _statementLru;
        private long _statementCacheEpoch;

        internal bool StatementCachingEnabled => _options.StatementCacheSize != 0;
        internal long StatementCacheEpoch { get { lock (_gate) return _statementCacheEpoch; } }

        internal PreparedStatement? TakePreparedStatement(string sql)
        {
            lock (_gate)
            {
                if (_state != SessionState.Busy || _statementCache == null || !_statementCache.TryGetValue(sql, out var node)) return null;
                _statementCache.Remove(sql);
                _statementLru!.Remove(node);
                return node.Value;
            }
        }

        internal void ReturnPreparedStatement(PreparedStatement statement)
        {
            lock (_gate)
            {
                if (!StatementCachingEnabled || !statement.Cacheable || _xaDedicated ||
                    (_state != SessionState.Open && _state != SessionState.Busy) ||
                    statement.Generation != _generation || statement.CacheEpoch != _statementCacheEpoch || statement.Database != _database)
                {
                    statement.Release();
                    return;
                }
                _statementCache ??= new Dictionary<string, LinkedListNode<PreparedStatement>>(StringComparer.Ordinal);
                _statementLru ??= new LinkedList<PreparedStatement>();
                if (_statementCache.TryGetValue(statement.CommandText, out var duplicate))
                {
                    _statementLru.Remove(duplicate);
                    _statementCache.Remove(statement.CommandText);
                    duplicate.Value.Release();
                }
                _statementCache.Add(statement.CommandText, _statementLru.AddLast(statement));
                if (_statementCache.Count > _options.StatementCacheSize)
                {
                    var oldest = _statementLru.First!;
                    _statementLru.RemoveFirst();
                    _statementCache.Remove(oldest.Value.CommandText);
                    oldest.Value.Release();
                }
            }
        }

        internal void InvalidatePreparedStatements()
        {
            lock (_gate) ClearPreparedStatementCache(closeStatements: true);
        }

        // The server already closed all IDs after reset or transport loss.
        private void ClearPreparedStatementCache(bool closeStatements)
        {
            if (!StatementCachingEnabled) return;
            if (closeStatements && _statementLru != null)
                foreach (var statement in _statementLru) statement.Release();
            _statementLru?.Clear();
            _statementCache?.Clear();
            _statementCacheEpoch++;
        }
    }
}
