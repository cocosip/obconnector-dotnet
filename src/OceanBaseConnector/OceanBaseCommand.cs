using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector
{
    public sealed class OceanBaseCommand : DbCommand
    {
        private readonly OceanBaseParameterCollection _parameters = new OceanBaseParameterCollection();
        private OceanBaseConnection? _connection;
        private OceanBaseTransaction? _transaction;
        private int _timeout = OceanBaseConnectionStringBuilder.DefaultCommandTimeoutSeconds;
        private readonly object _gate = new object();
        private CommandExchange? _execution;
        private PreparedStatement? _prepared;
        private bool _explicitPreparation;
        private bool _disposed;
        private int _fetchSize;
        internal PhysicalSession? RequiredSession { get; set; }
        internal object? SessionOwner { get; set; }
        internal Func<CancellationToken>? CancellationOrigin { get; set; }
#if NET8_0_OR_GREATER
        internal DbBatchCommand? BatchCommand { get; set; }
        internal Action? BatchFailed { get; set; }
#endif
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get => _timeout; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _timeout = value; } }
        /// <summary>Rows requested per server cursor fetch; zero uses ordinary result streaming.</summary>
        public int FetchSize { get => _fetchSize; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _fetchSize = value; } }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;
        protected override DbConnection? DbConnection
        {
            get => _connection;
            set => _connection = value == null ? null : value as OceanBaseConnection ?? throw new ArgumentException("An OceanBaseConnection is required.", nameof(value));
        }
        protected override DbTransaction? DbTransaction
        {
            get => _transaction;
            set => _transaction = value == null ? null : value as OceanBaseTransaction ?? throw new ArgumentException("An OceanBaseTransaction is required.", nameof(value));
        }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbParameter CreateDbParameter() => new OceanBaseParameter();
        public override int ExecuteNonQuery()
        {
            using var reader = ExecuteDbDataReader(CommandBehavior.Default);
            do { while (reader.Read()) { } } while (reader.NextResult());
            return reader.RecordsAffected;
        }
        public override object? ExecuteScalar()
        {
            using var reader = ExecuteDbDataReader(CommandBehavior.Default);
            while (reader.FieldCount == 0 && reader.NextResult()) { }
            var value = reader.Read() && reader.FieldCount != 0 ? reader.GetValue(0) : null;
            do { while (reader.Read()) { } } while (reader.NextResult());
            return value;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => ExecuteReaderCore(behavior, CancellationToken.None);
        internal DbDataReader ExecuteReaderCore(CommandBehavior behavior, CancellationToken token)
        {
            var operation = Start(behavior, token, false);
            var execution = operation.Exchange;
            execution.StartDiagnostics("execute");
            try
            {
                var zone = execution.EnsureSessionTimeZone();
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var national = execution.EnsureNationalCharacterSet();
                var parameters = HasNationalParameters(operation.Bindings.Parameters)
                    ? PreparedParameters.Encode(operation.Bindings.Parameters, execution.Mode, national) : operation.Parameters;
                var statement = operation.Binary ? EnsurePrepared(operation) : null;
                ValidateCursorStatement(operation.FetchSize, statement);
                execution.Send(statement == null ? SqlPacket(operation.Bindings.CommandText, 3) : PreparedParameters.Execute(statement.Id, parameters, operation.FetchSize > 0));
                var result = new QueryResult(execution, operation.Binary, sequential: (behavior & CommandBehavior.SequentialAccess) != 0,
                    cursorId: operation.FetchSize > 0 ? statement!.Id : (uint?)null, fetchSize: operation.FetchSize);
                result.Start();
                return new OceanBaseDataReader(execution, result, operation.Connection, operation.Session, behavior);
            }
            catch (Exception error) { throw execution.Fail(error); }
        }
        public override void Prepare()
            => PrepareCore(CancellationToken.None);
        internal void PrepareCore(CancellationToken token)
        {
            var operation = Start(CommandBehavior.Default, token, true);
            operation.Exchange.StartDiagnostics("prepare");
            try { EnsurePrepared(operation); operation.Exchange.Complete(); }
            catch (Exception error) { throw operation.Exchange.Fail(error); }
        }
        public override void Cancel() { lock (_gate) _execution?.Cancel(); }
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
            do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            return reader.RecordsAffected;
        }
        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) { }
            var value = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount != 0 ? reader.GetValue(0) : null;
            do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            return value;
        }
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var operation = Start(behavior, cancellationToken, false);
            var execution = operation.Exchange;
            execution.StartDiagnostics("execute");
            try
            {
                var zone = await execution.EnsureSessionTimeZoneAsync(cancellationToken).ConfigureAwait(false);
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var national = await execution.EnsureNationalCharacterSetAsync(cancellationToken).ConfigureAwait(false);
                var parameters = HasNationalParameters(operation.Bindings.Parameters)
                    ? PreparedParameters.Encode(operation.Bindings.Parameters, execution.Mode, national) : operation.Parameters;
                var statement = operation.Binary ? await EnsurePreparedAsync(operation, cancellationToken).ConfigureAwait(false) : null;
                ValidateCursorStatement(operation.FetchSize, statement);
                await execution.SendAsync(statement == null ? SqlPacket(operation.Bindings.CommandText, 3) : PreparedParameters.Execute(statement.Id, parameters, operation.FetchSize > 0)).ConfigureAwait(false);
                var result = new QueryResult(execution, operation.Binary, sequential: (behavior & CommandBehavior.SequentialAccess) != 0,
                    cursorId: operation.FetchSize > 0 ? statement!.Id : (uint?)null, fetchSize: operation.FetchSize);
                await result.StartAsync(cancellationToken).ConfigureAwait(false);
                return new OceanBaseDataReader(execution, result, operation.Connection, operation.Session, behavior);
            }
            catch (Exception error) { throw execution.Fail(error); }
        }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            var operation = Start(CommandBehavior.Default, cancellationToken, true);
            operation.Exchange.StartDiagnostics("prepare");
            try { await EnsurePreparedAsync(operation, cancellationToken).ConfigureAwait(false); operation.Exchange.Complete(); }
            catch (Exception error) { throw operation.Exchange.Fail(error); }
        }

        public override async ValueTask DisposeAsync()
        {
            var statement = DetachStatement();
            if (statement != null) await PreparedStatement.CloseIdleAsync(statement.Session, _timeout).ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif
        private (CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary, int FetchSize)
            Start(CommandBehavior behavior, CancellationToken token, bool prepareOnly)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                if (_execution != null) throw new InvalidOperationException("The command already has an active operation.");
                OceanBaseConnection.RejectAmbientTransaction();
                if (CommandType != CommandType.Text)
                    throw new NotSupportedException("Only text commands are supported at this stage.");
                if ((behavior & ~(CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess)) != 0)
                    throw new NotSupportedException("This CommandBehavior is not supported yet.");
                var text = CommandText;
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("CommandText is required.");
                if (token.IsCancellationRequested) { CancellationOrigin?.Invoke().ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested(); }
                var connection = _connection ?? throw new InvalidOperationException("A connection is required.");
                var session = connection.GetCommandSession(_transaction, SessionOwner);
                if (RequiredSession != null && !ReferenceEquals(session, RequiredSession))
                    throw new InvalidOperationException("The connection's physical session changed before the operation started.");
                var length = ResultColumn.Utf8.GetByteCount(text);
                if (length >= PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Command text exceeds the packet size limit.");
                var bindings = ParameterBindingPlan.Create(text, session.Info!.Mode, _parameters);
                var fetchSize = prepareOnly ? 0 : _fetchSize;
                if (prepareOnly) _explicitPreparation = true;
                var binary = _explicitPreparation || bindings.Parameters.Length != 0 || fetchSize > 0 || _prepared != null && _prepared.CommandText == bindings.CommandText;
                var parameters = binary && !prepareOnly ? PreparedParameters.Encode(bindings.Parameters, session.Info.Mode, validateOnly: true) : Array.Empty<byte>();
                if (_prepared != null && (!binary || !ReferenceEquals(_prepared.Session, session) || _prepared.CommandText != bindings.CommandText))
                { _prepared.Release(); _prepared = null; }
                if (_prepared != null && _prepared.ParameterCount != bindings.Parameters.Length) throw ParameterCountMismatch();
                CommandExchange? execution = null;
                execution = connection.StartCommand(session, _transaction, _timeout, token, () =>
                {
                    lock (_gate) { if (ReferenceEquals(_execution, execution)) _execution = null; }
                }, SessionOwner, CancellationOrigin);
#if NET8_0_OR_GREATER
                execution.BatchCommand = BatchCommand;
                execution.BatchFailed = BatchFailed;
#endif
                _execution = execution;
                return (execution, session, connection, bindings, parameters, binary, fetchSize);
            }
        }

        private static bool HasNationalParameters(OceanBaseParameter[] parameters)
        {
            foreach (var parameter in parameters)
                if (parameter.OceanBaseDbType == OceanBaseDbType.NChar || parameter.OceanBaseDbType == OceanBaseDbType.NVarchar2) return true;
            return false;
        }

        private PreparedStatement EnsurePrepared((CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary, int FetchSize) operation)
        {
            InvalidatePreparedDatabase(operation.Session);
            if (_prepared != null) return _prepared;
            operation.Exchange.Send(SqlPacket(operation.Bindings.CommandText, 22), executesSql: false);
            return PublishPrepared(PreparedStatement.Read(operation.Exchange, operation.Session, operation.Bindings.CommandText), operation.Bindings.Parameters.Length);
        }

        private async Task<PreparedStatement> EnsurePreparedAsync((CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary, int FetchSize) operation, CancellationToken token)
        {
            InvalidatePreparedDatabase(operation.Session);
            if (_prepared != null) return _prepared;
            await operation.Exchange.SendAsync(SqlPacket(operation.Bindings.CommandText, 22), executesSql: false).ConfigureAwait(false);
            var statement = await PreparedStatement.ReadAsync(operation.Exchange, operation.Session, operation.Bindings.CommandText, token).ConfigureAwait(false);
            return PublishPrepared(statement, operation.Bindings.Parameters.Length);
        }

        private PreparedStatement PublishPrepared(PreparedStatement statement, int count)
        {
            lock (_gate)
            {
                if (_disposed || statement.ParameterCount != count)
                {
                    statement.Release();
                    if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                    throw ParameterCountMismatch();
                }
                return _prepared = statement;
            }
        }

        private void InvalidatePreparedDatabase(PhysicalSession session)
        {
            if (_prepared == null || _prepared.Database == session.Database) return;
            _prepared.Release(); _prepared = null;
        }

        private static void ValidateCursorStatement(int fetchSize, PreparedStatement? statement)
        {
            if (fetchSize > 0 && (statement == null || statement.ColumnCount == 0))
                throw new NotSupportedException("Server cursors require a prepared statement with result columns.");
        }

        internal static byte[] SqlPacket(string text, byte code)
        {
            var bytes = ResultColumn.Utf8.GetBytes(text);
            var packet = new byte[bytes.Length + 1];
            packet[0] = code;
            Buffer.BlockCopy(bytes, 0, packet, 1, bytes.Length);
            return packet;
        }

        private PreparedStatement? DetachStatement()
        {
            lock (_gate)
            {
                _disposed = true;
                _execution?.Cancel();
                var statement = _prepared;
                _prepared = null;
                statement?.Release();
                return statement;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var statement = DetachStatement();
                if (statement != null) PreparedStatement.CloseIdle(statement.Session, _timeout);
            }
            base.Dispose(disposing);
        }
        private static ArgumentException ParameterCountMismatch() => new ArgumentException("The server's prepared parameter count does not match the supplied bindings.");
    }
}
