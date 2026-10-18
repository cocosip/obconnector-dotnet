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
        private TextQuery? _execution;
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
            var execution = Start(behavior, CancellationToken.None, out var request, out var connection);
            try
            {
                execution.Send(request);
                var result = new TextResult(execution);
                result.Start();
                return new OceanBaseDataReader(execution, result, connection, behavior);
            }
            catch { execution.Dispose(); throw; }
        }
        public override void Prepare() => throw Unsupported();
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
            var execution = Start(behavior, cancellationToken, out var request, out var connection);
            try
            {
                await execution.SendAsync(request).ConfigureAwait(false);
                var result = new TextResult(execution);
                await result.StartAsync(cancellationToken).ConfigureAwait(false);
                return new OceanBaseDataReader(execution, result, connection, behavior);
            }
            catch { execution.Dispose(); throw; }
        }
#if NET8_0_OR_GREATER
        public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.FromException(Unsupported());
#endif
        private TextQuery Start(CommandBehavior behavior, CancellationToken token, out byte[] request, out OceanBaseConnection connection)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                if (_execution != null) throw new InvalidOperationException("The command already has an active operation.");
                OceanBaseConnection.RejectAmbientTransaction();
                if (CommandType != CommandType.Text || _transaction != null)
                    throw new NotSupportedException("Only text commands without transactions are supported at this stage.");
                if ((behavior & ~CommandBehavior.CloseConnection) != 0)
                    throw new NotSupportedException("This CommandBehavior is not supported yet.");
                if (string.IsNullOrWhiteSpace(CommandText)) throw new InvalidOperationException("CommandText is required.");
                token.ThrowIfCancellationRequested();
                connection = _connection ?? throw new InvalidOperationException("A connection is required.");
                var session = connection.GetOpenSession();
                var length = TextColumn.Utf8.GetByteCount(CommandText);
                if (length >= PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Command text exceeds the packet size limit.");
                var bindings = ParameterBindingPlan.Create(CommandText, session.Info!.Mode, _parameters);
                if (bindings.Parameters.Length != 0)
                    throw new NotSupportedException("Parameterized execution requires server-side preparation, which is not implemented yet.");
                request = new byte[length + 1];
                request[0] = 3;
                TextColumn.Utf8.GetBytes(CommandText, 0, CommandText.Length, request, 1);
                TextQuery? execution = null;
                execution = new TextQuery(session, connection.Log, _timeout, token, () =>
                {
                    lock (_gate) { if (ReferenceEquals(_execution, execution)) _execution = null; }
                });
                return _execution = execution;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { lock (_gate) { _disposed = true; _execution?.Cancel(); } }
            base.Dispose(disposing);
        }
        private static NotSupportedException Unsupported() => new NotSupportedException("Command execution is not implemented by the current provider stage.");
    }
}
