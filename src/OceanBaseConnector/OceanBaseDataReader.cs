using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed class OceanBaseDataReader : DbDataReader, IDbColumnSchemaGenerator
    {
        private readonly CommandExchange _query;
        private readonly QueryResult _result;
        private readonly OceanBaseConnection _connection;
        private readonly bool _closeConnection;
        private bool _closed;
        internal OceanBaseDataReader(CommandExchange query, QueryResult result, OceanBaseConnection connection, CommandBehavior behavior)
        { _query = query; _result = result; _connection = connection; _closeConnection = (behavior & CommandBehavior.CloseConnection) != 0; }
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override int Depth => 0;
        public override int FieldCount { get { EnsureOpen(); return _result.Columns.Length; } }
        public override bool HasRows { get { EnsureOpen(); return _result.HasRows; } }
        public override bool IsClosed => _closed;
        public override int RecordsAffected => _result.RecordsAffected;
        public override string GetName(int ordinal) => Column(ordinal).Name;
        public override Type GetFieldType(int ordinal) => Column(ordinal).FieldType;
        public override string GetDataTypeName(int ordinal) => Column(ordinal).DataTypeName;

        public ReadOnlyCollection<DbColumn> GetColumnSchema()
        {
            EnsureOpen();
            var columns = new DbColumn[_result.Columns.Length];
            for (var i = 0; i < columns.Length; i++) columns[i] = new ResultDbColumn(_result.Columns[i], i);
            return Array.AsReadOnly(columns);
        }

        public override DataTable? GetSchemaTable()
        {
            var columns = GetColumnSchema();
            if (columns.Count == 0) return null;
            var table = new DataTable("SchemaTable") { Locale = CultureInfo.InvariantCulture };
            foreach (var entry in SchemaColumns) table.Columns.Add(entry.Name, entry.Type);
            foreach (var column in columns)
            {
                var row = table.NewRow();
                foreach (var entry in SchemaColumns) row[entry.Name] = column[entry.Name] ?? DBNull.Value;
                table.Rows.Add(row);
            }
            return table;
        }

        private static readonly (string Name, Type Type)[] SchemaColumns =
        {
            (SchemaTableColumn.ColumnName, typeof(string)), (SchemaTableColumn.ColumnOrdinal, typeof(int)),
            (SchemaTableColumn.ColumnSize, typeof(int)), (SchemaTableColumn.NumericPrecision, typeof(short)),
            (SchemaTableColumn.NumericScale, typeof(short)), (SchemaTableColumn.DataType, typeof(Type)),
            ("DataTypeName", typeof(string)), (SchemaTableOptionalColumn.ProviderSpecificDataType, typeof(Type)),
            (SchemaTableColumn.ProviderType, typeof(int)), (SchemaTableColumn.AllowDBNull, typeof(bool)),
            (SchemaTableColumn.IsAliased, typeof(bool)), (SchemaTableColumn.IsExpression, typeof(bool)),
            (SchemaTableColumn.IsKey, typeof(bool)), (SchemaTableColumn.IsUnique, typeof(bool)),
            (SchemaTableColumn.IsLong, typeof(bool)), (SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool)),
            (SchemaTableOptionalColumn.IsReadOnly, typeof(bool)), (SchemaTableOptionalColumn.IsHidden, typeof(bool)),
            (SchemaTableOptionalColumn.BaseCatalogName, typeof(string)), (SchemaTableColumn.BaseSchemaName, typeof(string)),
            (SchemaTableColumn.BaseTableName, typeof(string)), (SchemaTableColumn.BaseColumnName, typeof(string)),
            (SchemaTableOptionalColumn.BaseServerName, typeof(string))
        };
        public override int GetOrdinal(string name)
        {
            EnsureOpen();
            if (name == null) throw new ArgumentNullException(nameof(name));
            for (var i = 0; i < FieldCount; i++) if (_result.Columns[i].Name == name) return i;
            for (var i = 0; i < FieldCount; i++) if (string.Equals(_result.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException("The requested column was not found.");
        }
        public override object GetValue(int ordinal)
        {
            var column = Column(ordinal);
            if (_result.Row == null) throw new InvalidOperationException("Read must position the reader on a row.");
            return _result.IsBinary ? column.DecodeBinary(_result.Row[ordinal]) : column.Decode(_result.Row[ordinal]);
        }
        public override int GetValues(object[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) values[i] = GetValue(i);
            return count;
        }
        public override Type GetProviderSpecificFieldType(int ordinal)
        {
            var column = Column(ordinal);
            _ = column.FieldType;
            return column.ProviderSpecificFieldType;
        }
        public override object GetProviderSpecificValue(int ordinal)
        {
            var column = Column(ordinal);
            _ = column.FieldType;
            if (_result.Row == null) throw new InvalidOperationException("Read must position the reader on a row.");
            var value = _result.Row[ordinal];
            if (column.TypeCode == 202 && value != null) return column.DecodeTimestamp(value);
            return column.IsDecimal && value != null ? column.DecodeDecimal(value, _result.IsBinary) : GetValue(ordinal);
        }
        public override int GetProviderSpecificValues(object[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) values[i] = GetProviderSpecificValue(i);
            return count;
        }
        public override T GetFieldValue<T>(int ordinal) => typeof(T) == typeof(OceanBaseDecimal) || typeof(T) == typeof(OceanBaseTimestamp)
            ? (T)GetProviderSpecificValue(ordinal) : typeof(T) == typeof(decimal) ? (T)(object)GetDecimal(ordinal)
            : typeof(T) == typeof(DateTime) ? (T)(object)GetDateTime(ordinal) : (T)GetValue(ordinal);
        public override bool IsDBNull(int ordinal)
        {
            Column(ordinal);
            if (_result.Row == null) throw new InvalidOperationException("Read must position the reader on a row.");
            return _result.Row[ordinal] == null;
        }
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override DateTime GetDateTime(int ordinal)
        {
            var value = GetValue(ordinal);
            return value is OceanBaseTimestamp timestamp ? timestamp.ToDateTime() : (DateTime)value;
        }
        public override decimal GetDecimal(int ordinal)
        {
            var value = GetValue(ordinal);
            return value is OceanBaseDecimal number ? number.ToDecimal() : (decimal)value;
        }
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            Copy((byte[])GetValue(ordinal), dataOffset, buffer, bufferOffset, length);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            Copy(GetString(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
        private static long Copy<T>(T[] value, long offset, T[]? buffer, int bufferOffset, int length)
        {
            if (offset < 0 || offset > value.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer == null) return value.Length;
            if (bufferOffset < 0 || length < 0 || bufferOffset > buffer.Length - length) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            var count = (int)Math.Min(length, value.Length - offset);
            Array.Copy(value, (int)offset, buffer, bufferOffset, count);
            return count;
        }
        public override bool NextResult() { EnsureOpen(); return _result.NextResult(); }
        public override bool Read() { EnsureOpen(); return _result.Read(); }
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        { EnsureOpen(); return _result.ReadAsync(cancellationToken); }
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        { EnsureOpen(); return _result.NextResultAsync(cancellationToken); }
        public override void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _query.Dispose(); }
            finally { if (_closeConnection) _connection.Close(); }
        }
        protected override void Dispose(bool disposing) { if (disposing) Close(); base.Dispose(disposing); }
#if NET8_0_OR_GREATER
        public override async Task CloseAsync()
        {
            if (_closed) return;
            _closed = true;
            try { _query.Dispose(); }
            finally { if (_closeConnection) await _connection.CloseAsync().ConfigureAwait(false); }
        }
        public override async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
            base.Dispose(true);
            GC.SuppressFinalize(this);
        }
#endif
        private ResultColumn Column(int ordinal)
        {
            EnsureOpen();
            if ((uint)ordinal >= (uint)_result.Columns.Length) throw new IndexOutOfRangeException();
            return _result.Columns[ordinal];
        }
        private void EnsureOpen()
        {
            if (_closed) throw new InvalidOperationException("The reader is closed.");
            _result.EnsureValid();
        }
    }
}
