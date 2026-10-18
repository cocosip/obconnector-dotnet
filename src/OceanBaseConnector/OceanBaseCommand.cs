using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    public sealed class OceanBaseCommand : DbCommand
    {
        private readonly OceanBaseParameterCollection _parameters = new OceanBaseParameterCollection();
        private OceanBaseConnection? _connection;
        private OceanBaseTransaction? _transaction;
        private int _timeout = OceanBaseConnectionStringBuilder.DefaultCommandTimeoutSeconds;
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
        public override int ExecuteNonQuery() => throw Unsupported();
        public override object? ExecuteScalar() => throw Unsupported();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw Unsupported();
        public override void Prepare() => throw Unsupported();
        public override void Cancel() => throw Unsupported();
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromException<int>(Unsupported());
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromException<object?>(Unsupported());
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => Task.FromException<DbDataReader>(Unsupported());
#if NET8_0_OR_GREATER
        public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.FromException(Unsupported());
#endif
        private static NotSupportedException Unsupported() => new NotSupportedException("Command execution is not implemented by the current provider stage.");
    }
}
