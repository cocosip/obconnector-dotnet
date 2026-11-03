using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    /// <summary>A single-use Oracle output or result-column cursor, bound to its original connection and transaction.</summary>
    public sealed class OceanBaseRefCursor : IDisposable
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        , IAsyncDisposable
#endif
    {
        private readonly object _gate = new object();
        private readonly OracleCursorOwner _owner;
        private CommandExchange? _exchange;
        private bool _used;
        private bool _disposed;
        private int _released;
        internal uint Id { get; }
        internal OracleCursorOwner Owner => _owner;
        internal OceanBaseRefCursor(OracleCursorOwner owner, uint id) { _owner = owner; Id = id; }

        /// <summary>Opens one reader. Dispose the cursor if it will not be read; unread reader closure destroys its session.</summary>
        public OceanBaseDataReader GetDataReader(int fetchSize = 128, CommandBehavior behavior = CommandBehavior.Default)
            => GetDataReader(fetchSize, false, behavior);

        /// <summary>Opens one forward reader with optional Oracle offset FETCH and validated position tails.</summary>
        public OceanBaseDataReader GetDataReader(int fetchSize, bool useCursorOffset, CommandBehavior behavior)
        {
            var exchange = Start(fetchSize, behavior, CancellationToken.None);
            try
            {
                if (useCursorOffset) exchange.EnsureCursorOffset();
                exchange.EnsureSessionTimeZone(); exchange.EnsureNationalCharacterSet(); exchange.CaptureCursorSession();
                var result = Result(exchange, fetchSize, behavior, useCursorOffset);
                exchange.Send(QueryResult.CursorFetchPacket(Id, fetchSize, useCursorOffset), executesSql: false);
                result.Start();
                return new OceanBaseDataReader(exchange, result, _owner.Connection, _owner.Session, behavior);
            }
            catch (Exception error) { throw exchange.Fail(error); }
        }

        /// <summary>Asynchronously opens the cursor's single reader using the original command timeout.</summary>
        public Task<OceanBaseDataReader> GetDataReaderAsync(int fetchSize = 128, CommandBehavior behavior = CommandBehavior.Default, CancellationToken cancellationToken = default)
            => GetDataReaderAsync(fetchSize, false, behavior, cancellationToken);

        /// <summary>Asynchronously opens one forward reader with optional Oracle offset FETCH.</summary>
        public async Task<OceanBaseDataReader> GetDataReaderAsync(int fetchSize, bool useCursorOffset, CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var exchange = Start(fetchSize, behavior, cancellationToken);
            try
            {
                if (useCursorOffset) await exchange.EnsureCursorOffsetAsync(cancellationToken).ConfigureAwait(false);
                await exchange.EnsureSessionTimeZoneAsync(cancellationToken).ConfigureAwait(false);
                await exchange.EnsureNationalCharacterSetAsync(cancellationToken).ConfigureAwait(false); exchange.CaptureCursorSession();
                var result = Result(exchange, fetchSize, behavior, useCursorOffset);
                await exchange.SendAsync(QueryResult.CursorFetchPacket(Id, fetchSize, useCursorOffset), executesSql: false).ConfigureAwait(false);
                await result.StartAsync(cancellationToken).ConfigureAwait(false);
                return new OceanBaseDataReader(exchange, result, _owner.Connection, _owner.Session, behavior);
            }
            catch (Exception error) { throw exchange.Fail(error); }
        }

        private QueryResult Result(CommandExchange exchange, int fetchSize, CommandBehavior behavior, bool useCursorOffset)
            => new QueryResult(exchange, binary: true, sequential: (behavior & CommandBehavior.SequentialAccess) != 0, cursorId: Id, fetchSize: fetchSize, refCursor: true, cursorOffset: useCursorOffset);

        private CommandExchange Start(int fetchSize, CommandBehavior behavior, CancellationToken token)
        {
            if (fetchSize <= 0) throw new ArgumentOutOfRangeException(nameof(fetchSize));
            if ((behavior & ~(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection | CommandBehavior.SingleRow | CommandBehavior.SingleResult)) != 0)
                throw new NotSupportedException("This cursor CommandBehavior is not supported.");
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseRefCursor));
                if (_used) throw new InvalidOperationException("A REF CURSOR can only be opened once.");
                var children = new OracleCursorOwner(this);
                var exchange = _owner.Connection.StartRefCursor(_owner.Session, _owner.Transaction, _owner.Epoch, _owner.Timeout, token, children.EndExecution);
                exchange.CursorColumns = children;
                _used = true; _exchange = exchange; exchange.OwnCursor(refCursor: true); exchange.StartDiagnostics("cursor.fetch");
                return exchange;
            }
        }

        internal void ReleaseResource()
        { if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(Id); }

        private void Close()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_exchange == null) ReleaseResource(); else _exchange.Dispose();
            }
        }
        public void Dispose() { Close(); PreparedStatement.CloseIdle(_owner.Session, _owner.Timeout); }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public async ValueTask DisposeAsync()
        { Close(); await PreparedStatement.CloseIdleAsync(_owner.Session, _owner.Timeout).ConfigureAwait(false); }
#endif
    }
}
