#if NET8_0_OR_GREATER
using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    /// <summary>Executes commands sequentially by default, with optional MySQL INSERT rewriting.
    /// No implicit transaction or automatic replay is added.</summary>
    public sealed partial class OceanBaseBatch : DbBatch
    {
        internal object Gate { get; } = new object();
        private readonly OceanBaseBatchCommandCollection _commands;
        private OceanBaseConnection? _connection;
        private OceanBaseTransaction? _transaction;
        private int _timeout = OceanBaseConnectionStringBuilder.DefaultCommandTimeoutSeconds;
        private bool _disposed;
        private bool _rewriteBatchedInserts;
        private readonly bool _ownsConnection;
        private Operation? _operation;
        public OceanBaseBatch() => _commands = new OceanBaseBatchCommandCollection(this);
        internal OceanBaseBatch(OceanBaseConnection connection) : this() { _connection = connection; _ownsConnection = true; }
        protected override DbBatchCommandCollection DbBatchCommands => _commands;
        public override int Timeout { get => _timeout; set { lock (Gate) { CheckMutable(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _timeout = value; } } }
        /// <summary>Combine eligible adjacent MySQL INSERT VALUES commands during ExecuteNonQuery.
        /// Disabled by default; merged rows share one statement's execution and failure semantics.</summary>
        public bool RewriteBatchedInserts { get => _rewriteBatchedInserts; set { lock (Gate) { CheckMutable(); _rewriteBatchedInserts = value; } } }
        protected override DbConnection? DbConnection
        {
            get => _ownsConnection ? throw new NotSupportedException("A data source batch owns its connection.") : _connection;
            set { lock (Gate) { CheckMutable(); if (_ownsConnection) throw new NotSupportedException("A data source batch owns its connection."); _connection = value == null ? null : value as OceanBaseConnection ?? throw new ArgumentException("An OceanBaseConnection is required.", nameof(value)); } }
        }
        protected override DbTransaction? DbTransaction
        {
            get => _ownsConnection ? throw new NotSupportedException("A data source batch owns its transaction context.") : _transaction;
            set { lock (Gate) { CheckMutable(); if (_ownsConnection) throw new NotSupportedException("A data source batch owns its transaction context."); _transaction = value == null ? null : value as OceanBaseTransaction ?? throw new ArgumentException("An OceanBaseTransaction is required.", nameof(value)); } }
        }
        protected override DbBatchCommand CreateDbBatchCommand() { lock (Gate) { if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseBatch)); return new OceanBaseBatchCommand(); } }
        internal void CheckMutable()
        {
            lock (Gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseBatch));
                if (_operation != null) throw new InvalidOperationException("The batch already has an active operation.");
            }
        }
        private Operation Start(CommandBehavior behavior, CancellationToken token)
        {
            lock (Gate)
            {
                CheckMutable(); token.ThrowIfCancellationRequested();
                if ((behavior & ~(CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess | CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo | CommandBehavior.SingleRow | CommandBehavior.SingleResult)) != 0) throw new NotSupportedException("This CommandBehavior is not supported.");
                var connection = _connection ?? throw new InvalidOperationException("A connection is required.");
                connection.EnsureAmbientTransaction();
                var transaction = connection.ResolveCommandTransaction(_transaction);
                var operation = new Operation(this, connection, token, behavior);
                try
                {
                    operation.Session = connection.ReserveBatch(transaction, operation, out var openGeneration);
                    operation.OpenGeneration = openGeneration;
                    operation.Items = new OceanBaseBatchCommand[_commands.Count]; operation.Commands = new OceanBaseCommand[_commands.Count]; operation.Counts = new int[_commands.Count];
                    for (var i = 0; i < _commands.Count; i++)
                    {
                        var item = (OceanBaseBatchCommand)_commands[i]; item.Affected = -1; operation.Items[i] = item; operation.Counts[i] = -1;
                        operation.Commands[i] = item.Snapshot(connection, transaction, _timeout, operation, operation.Session);
                    }
                    _operation = operation; return operation;
                }
                catch { operation.Dispose(); throw; }
            }
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (_ownsConnection) _connection!.Open();
            try
            {
                var operation = Start(_ownsConnection ? behavior | CommandBehavior.CloseConnection : behavior, CancellationToken.None);
                var reader = new OceanBaseBatchReader(operation); operation.Reader = reader;
                try { reader.Start(); return reader; } catch { reader.Close(); throw; }
            }
            catch
            {
                // Preserve the execution failure if owned-connection cleanup also fails.
                try { if (_ownsConnection) _connection!.Close(); } catch { }
                throw;
            }
        }
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            if (_ownsConnection) await _connection!.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var operation = Start(_ownsConnection ? behavior | CommandBehavior.CloseConnection : behavior, cancellationToken);
                var reader = new OceanBaseBatchReader(operation); operation.Reader = reader;
                try { await reader.StartAsync().ConfigureAwait(false); return reader; } catch { await reader.CloseAsync().ConfigureAwait(false); throw; }
            }
            catch
            {
                // Preserve the execution failure if owned-connection cleanup also fails.
                try { if (_ownsConnection) await _connection!.CloseAsync().ConfigureAwait(false); } catch { }
                throw;
            }
        }
        public override int ExecuteNonQuery()
        {
            if (_rewriteBatchedInserts) return ExecuteRewrittenNonQuery(false, CancellationToken.None).GetAwaiter().GetResult();
            using var reader = ExecuteReader(); do { while (reader.Read()) { } } while (reader.NextResult()); return reader.RecordsAffected;
        }
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
        {
            if (_rewriteBatchedInserts) return await ExecuteRewrittenNonQuery(true, cancellationToken).ConfigureAwait(false);
            await using var reader = await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)); return reader.RecordsAffected;
        }
        public override object? ExecuteScalar()
        {
            using var reader = ExecuteReader(); while (reader.FieldCount == 0 && reader.NextResult()) { }
            var value = reader.FieldCount != 0 && reader.Read() ? reader.GetValue(0) : null;
            do { while (reader.Read()) { } } while (reader.NextResult()); return value;
        }
        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
        {
            await using var reader = await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) { }
            var value = reader.FieldCount != 0 && await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetValue(0) : null;
            do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)); return value;
        }
        public override void Prepare()
        {
            if (_ownsConnection) throw new NotSupportedException("A data source batch cannot be prepared independently of a connection.");
            using var operation = Start(CommandBehavior.Default, CancellationToken.None);
            foreach (var command in operation.Commands) { operation.ThrowIfCanceled(); command.PrepareCore(operation.Token); }
        }
        public override async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            if (_ownsConnection) throw new NotSupportedException("A data source batch cannot be prepared independently of a connection.");
            using var operation = Start(CommandBehavior.Default, cancellationToken);
            foreach (var command in operation.Commands) { operation.ThrowIfCanceled(); await command.PrepareAsync(operation.Token).ConfigureAwait(false); }
        }
        public override void Cancel() { lock (Gate) _operation?.Cancel(); }
        public override void Dispose()
        {
            Operation? operation;
            lock (Gate) { if (_disposed) return; _disposed = true; operation = _operation; }
            try
            {
                operation?.Reader?.Close(); operation?.Dispose();
                foreach (OceanBaseBatchCommand command in _commands) command.Release();
            }
            finally { if (_ownsConnection) _connection!.Dispose(); }
            GC.SuppressFinalize(this);
        }
        public override async ValueTask DisposeAsync()
        {
            Operation? operation;
            lock (Gate) { if (_disposed) return; _disposed = true; operation = _operation; }
            try
            {
                if (operation?.Reader != null) await operation.Reader.CloseAsync().ConfigureAwait(false);
                operation?.Dispose();
                foreach (OceanBaseBatchCommand command in _commands) await command.ReleaseAsync().ConfigureAwait(false);
            }
            finally { if (_ownsConnection) await _connection!.DisposeAsync().ConfigureAwait(false); }
            GC.SuppressFinalize(this);
        }

        internal sealed class Operation : IDisposable
        {
            private readonly OceanBaseBatch _batch;
            private readonly CancellationTokenSource _cancel;
            private bool _ended;
            private OceanBaseCommand? _active;
            private bool _ownsActive;
            internal Operation(OceanBaseBatch batch, OceanBaseConnection connection, CancellationToken token, CommandBehavior behavior)
            { _batch = batch; Connection = connection; Behavior = behavior; CallerToken = token; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); Token = _cancel.Token; }
            internal OceanBaseConnection Connection { get; }
            internal CommandBehavior Behavior { get; }
            internal CancellationToken Token { get; }
            internal CancellationToken CallerToken { get; }
            internal CancellationToken ReaderToken { get; set; }
            internal CancellationToken CancellationOrigin() => CallerToken.IsCancellationRequested || !ReaderToken.CanBeCanceled ? CallerToken : ReaderToken;
            internal void ThrowIfCanceled()
            {
                if (!Token.IsCancellationRequested) return;
                CancellationOrigin().ThrowIfCancellationRequested();
                throw new OperationCanceledException("Batch canceled.");
            }
            internal long OpenGeneration { get; set; }
            internal PhysicalSession? Session { get; set; }
            internal OceanBaseBatchCommand[] Items { get; set; } = Array.Empty<OceanBaseBatchCommand>();
            internal OceanBaseCommand[] Commands { get; set; } = Array.Empty<OceanBaseCommand>();
            internal int[] Counts { get; set; } = Array.Empty<int>();
            internal OceanBaseBatchReader? Reader { get; set; }
            internal void Activate(OceanBaseCommand command, bool owned)
            {
                lock (_batch.Gate)
                {
                    if (_ended) throw new ObjectDisposedException(nameof(OceanBaseBatch));
                    _active = command; _ownsActive = owned;
                }
            }
            internal void Deactivate(OceanBaseCommand command)
            { lock (_batch.Gate) { if (ReferenceEquals(_active, command)) _active = null; } }
            internal void Fail() { Reader?.Abort(); Dispose(); }
            internal void Cancel() { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } }
            public void Dispose()
            {
                OceanBaseCommand? active; bool owned;
                lock (_batch.Gate)
                {
                    if (_ended) return; _ended = true;
                    active = _active; owned = _ownsActive || _batch._disposed; _active = null;
                }
                // Cancel the reachable command before releasing its reservation or linked token source.
                try { if (owned) active?.Dispose(); else active?.Cancel(); }
                finally
                {
                    lock (_batch.Gate)
                    {
                        Session?.ReleaseReservation(this);
                        foreach (var command in Commands)
                        {
                            if (command == null || !ReferenceEquals(command.SessionOwner, this)) continue;
                            command.SessionOwner = null; command.RequiredSession = null; command.CancellationOrigin = null;
                            command.BatchFailed = null; command.BatchCommand = null;
                        }
                        if (ReferenceEquals(_batch._operation, this)) _batch._operation = null;
                        _cancel.Dispose();
                    }
                }
            }
        }
    }
}
#endif
