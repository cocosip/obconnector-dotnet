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
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get => _timeout; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _timeout = value; } }
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
        {
            var operation = Start(behavior, CancellationToken.None, false);
            var execution = operation.Exchange;
            try
            {
                var zone = execution.EnsureSessionTimeZone();
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var statement = operation.Binary ? EnsurePrepared(operation) : null;
                execution.Send(statement == null ? SqlPacket(operation.Bindings.CommandText, 3) : PreparedParameters.Execute(statement.Id, operation.Parameters));
                var result = new QueryResult(execution, operation.Binary);
                result.Start();
                return new OceanBaseDataReader(execution, result, operation.Connection, behavior);
            }
            catch (Exception error) { throw execution.Fail(error); }
        }
        public override void Prepare()
        {
            var operation = Start(CommandBehavior.Default, CancellationToken.None, true);
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
            try
            {
                var zone = await execution.EnsureSessionTimeZoneAsync(cancellationToken).ConfigureAwait(false);
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var statement = operation.Binary ? await EnsurePreparedAsync(operation, cancellationToken).ConfigureAwait(false) : null;
                await execution.SendAsync(statement == null ? SqlPacket(operation.Bindings.CommandText, 3) : PreparedParameters.Execute(statement.Id, operation.Parameters)).ConfigureAwait(false);
                var result = new QueryResult(execution, operation.Binary);
                await result.StartAsync(cancellationToken).ConfigureAwait(false);
                return new OceanBaseDataReader(execution, result, operation.Connection, behavior);
            }
            catch (Exception error) { throw execution.Fail(error); }
        }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            var operation = Start(CommandBehavior.Default, cancellationToken, true);
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
        private (CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary)
            Start(CommandBehavior behavior, CancellationToken token, bool prepareOnly)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                if (_execution != null) throw new InvalidOperationException("The command already has an active operation.");
                OceanBaseConnection.RejectAmbientTransaction();
                if (CommandType != CommandType.Text)
                    throw new NotSupportedException("Only text commands are supported at this stage.");
                if ((behavior & ~CommandBehavior.CloseConnection) != 0)
                    throw new NotSupportedException("This CommandBehavior is not supported yet.");
                var text = CommandText;
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("CommandText is required.");
                token.ThrowIfCancellationRequested();
                var connection = _connection ?? throw new InvalidOperationException("A connection is required.");
                var session = connection.GetCommandSession(_transaction);
                var length = ResultColumn.Utf8.GetByteCount(text);
                if (length >= PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Command text exceeds the packet size limit.");
                var bindings = ParameterBindingPlan.Create(text, session.Info!.Mode, _parameters);
                if (prepareOnly) _explicitPreparation = true;
                var binary = _explicitPreparation || bindings.Parameters.Length != 0;
                var parameters = binary && !prepareOnly ? PreparedParameters.Encode(bindings.Parameters, session.Info.Mode) : Array.Empty<byte>();
                if (_prepared != null && (!binary || !ReferenceEquals(_prepared.Session, session) || _prepared.CommandText != bindings.CommandText))
                { _prepared.Release(); _prepared = null; }
                if (_prepared != null && _prepared.ParameterCount != bindings.Parameters.Length) throw ParameterCountMismatch();
                CommandExchange? execution = null;
                execution = connection.StartCommand(session, _transaction, _timeout, token, () =>
                {
                    lock (_gate) { if (ReferenceEquals(_execution, execution)) _execution = null; }
                });
                _execution = execution;
                return (execution, session, connection, bindings, parameters, binary);
            }
        }

        private PreparedStatement EnsurePrepared((CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary) operation)
        {
            if (_prepared != null) return _prepared;
            operation.Exchange.Send(SqlPacket(operation.Bindings.CommandText, 22), executesSql: false);
            return PublishPrepared(PreparedStatement.Read(operation.Exchange, operation.Session, operation.Bindings.CommandText), operation.Bindings.Parameters.Length);
        }

        private async Task<PreparedStatement> EnsurePreparedAsync((CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary) operation, CancellationToken token)
        {
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
