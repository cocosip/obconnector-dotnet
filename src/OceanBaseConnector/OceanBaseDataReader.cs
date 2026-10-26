using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
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
        internal OceanBaseArrayBindingResult? ArrayBindingResult => _result.ArrayBindingResult;
        private readonly OceanBaseConnection _connection;
        private readonly PhysicalSession _session;
        private readonly bool _closeConnection;
        private readonly bool _sequential;
        private readonly bool _singleRow;
        private readonly bool _singleResult;
        private readonly bool _schemaOnly;
        private bool _singleRowReturned;
        private bool _closed;
        private KeyInfoMetadata? _keyInfo;
        internal OceanBaseDataReader(CommandExchange query, QueryResult result, OceanBaseConnection connection, PhysicalSession session, CommandBehavior behavior, KeyInfoMetadata? keyInfo = null)
        {
            _query = query; _result = result; _connection = connection; _session = session;
            _keyInfo = keyInfo;
            _closeConnection = (behavior & CommandBehavior.CloseConnection) != 0;
            _sequential = (behavior & CommandBehavior.SequentialAccess) != 0;
            _singleRow = (behavior & CommandBehavior.SingleRow) != 0;
            _singleResult = (behavior & (CommandBehavior.SingleResult | CommandBehavior.SingleRow)) != 0;
            _schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
        }
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override int Depth => 0;
        public override int FieldCount { get { EnsureOpen(); return _result.Columns.Length; } }
        public override int VisibleFieldCount { get { EnsureOpen(); return _keyInfo?.VisibleCount ?? _result.Columns.Length; } }
        public override bool HasRows { get { EnsureOpen(); return !_schemaOnly && _result.HasRows; } }
        public override bool IsClosed => _closed;
        public override int RecordsAffected => _result.RecordsAffected;
        public override string GetName(int ordinal) => Column(ordinal).Name;
        public override Type GetFieldType(int ordinal) => Column(ordinal).FieldType;
        public override string GetDataTypeName(int ordinal) => Column(ordinal).DataTypeName;

        public ReadOnlyCollection<DbColumn> GetColumnSchema()
        {
            EnsureOpen();
            var columns = new DbColumn[_result.Columns.Length];
            for (var i = 0; i < columns.Length; i++) columns[i] = new ResultDbColumn(_result.Columns[i], i, _keyInfo?.Columns[i]);
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
            return DecodeValue(column, FieldBytes(ordinal), false);
        }
        public override int GetValues(object[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) { RequireCursorCast(i, values.GetType().GetElementType()!); values[i] = GetValue(i); }
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
            return DecodeValue(column, FieldBytes(ordinal), true);
        }
        public override int GetProviderSpecificValues(object[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) { RequireCursorCast(i, values.GetType().GetElementType()!); values[i] = GetProviderSpecificValue(i); }
            return count;
        }
        public override T GetFieldValue<T>(int ordinal)
        {
            RequireCursorCast(ordinal, typeof(T));
            return typeof(T) == typeof(Stream) ? (T)(object)GetStream(ordinal)
                : typeof(T) == typeof(TextReader) ? (T)(object)GetTextReader(ordinal)
                : ConvertValue<T>(IsProviderValue<T>() ? GetProviderSpecificValue(ordinal) : GetValue(ordinal));
        }

        private void RequireCursorCast(int ordinal, Type type)
        {
            if (Column(ordinal).TypeCode == 163 && !type.IsAssignableFrom(typeof(OceanBaseRefCursor)))
                throw new InvalidCastException("The cursor field has a different type.");
        }

        private static bool IsProviderValue<T>() => typeof(T) == typeof(OceanBaseDecimal) || typeof(T) == typeof(OceanBaseTimestamp) || typeof(T) == typeof(OceanBaseDate) || typeof(T) == typeof(OceanBaseDateTime);
        private static T ConvertValue<T>(object value)
        {
            if (typeof(T) == typeof(decimal) && value is OceanBaseDecimal number) return (T)(object)number.ToDecimal();
            if (typeof(T) == typeof(DateTime))
            {
                if (value is OceanBaseTimestamp timestamp) return (T)(object)timestamp.ToDateTime();
                if (value is OceanBaseTimestampLocalTimeZone local) return (T)(object)local.ToDateTime();
            }
            if (typeof(T) == typeof(DateTimeOffset) && value is OceanBaseTimestampWithTimeZone zoned) return (T)(object)zoned.ToDateTimeOffset();
            return (T)value;
        }

        public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken = default)
        {
            if (!_sequential) return await base.GetFieldValueAsync<T>(ordinal, cancellationToken).ConfigureAwait(false);
            var column = Column(ordinal); cancellationToken.ThrowIfCancellationRequested(); RequireCursorCast(ordinal, typeof(T)); var row = Sequential;
            if (typeof(T) == typeof(Stream) || typeof(T) == typeof(TextReader))
            {
                RequireFieldType(ordinal, typeof(T) == typeof(Stream) ? typeof(byte[]) : typeof(string));
                await row.MoveToAsync(ordinal, cancellationToken).ConfigureAwait(false);
                return typeof(T) == typeof(Stream) ? (T)(object)row.GetStream(ordinal) : (T)(object)row.GetTextReader(ordinal);
            }
            var bytes = await row.MaterializeAsync(ordinal, cancellationToken).ConfigureAwait(false);
            return ConvertValue<T>(DecodeValue(column, bytes, IsProviderValue<T>()));
        }

        private object DecodeValue(ResultColumn column, byte[]? bytes, bool provider)
        {
            if (column.TypeCode == 210 || column.TypeCode == 211)
            {
                try { return _result.IsBinary ? column.DecodeBinary(bytes) : column.Decode(bytes); }
                catch (ProtocolException error) { throw _query.Fail(error); }
            }
            if (provider && bytes != null)
            {
                if (column.IsMySqlDate) return column.DecodeProviderDate(bytes, _result.IsBinary);
                if (column.TypeCode == 202) return column.DecodeTimestamp(bytes);
                if (column.IsDecimal) return column.DecodeDecimal(bytes, _result.IsBinary);
            }
            return _result.IsBinary ? column.DecodeBinary(bytes) : column.Decode(bytes);
        }

        private SequentialRow Sequential => _result.Sequential ?? throw new InvalidOperationException("Read must position the reader on a row.");
        private byte[]? FieldBytes(int ordinal)
        {
            if (_sequential) return Sequential.Materialize(ordinal);
            if (_result.Row == null) throw new InvalidOperationException("Read must position the reader on a row.");
            return _result.Row[ordinal];
        }
        public override bool IsDBNull(int ordinal)
        {
            Column(ordinal);
            if (_sequential) return Sequential.IsNull(ordinal);
            if (_result.Row == null) throw new InvalidOperationException("Read must position the reader on a row.");
            return _result.Row[ordinal] == null;
        }
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken = default)
        {
            if (!_sequential) return base.IsDBNullAsync(ordinal, cancellationToken);
            Column(ordinal); cancellationToken.ThrowIfCancellationRequested(); return Sequential.IsNullAsync(ordinal, cancellationToken);
        }
        public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);
        public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);
        public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);
        public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);
        public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);
        public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);
        public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);
        public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);
        public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);
        public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);
        public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);
        public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            if (!_sequential) return Copy(GetFieldValue<byte[]>(ordinal), dataOffset, buffer, bufferOffset, length);
            RequireFieldType(ordinal, typeof(byte[])); return Sequential.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        }
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            if (!_sequential) return Copy(GetString(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
            RequireFieldType(ordinal, typeof(string)); return Sequential.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        }
        public override Stream GetStream(int ordinal)
        {
            if (!_sequential) return base.GetStream(ordinal);
            RequireFieldType(ordinal, typeof(byte[])); return Sequential.GetStream(ordinal);
        }
        public override TextReader GetTextReader(int ordinal)
        {
            if (!_sequential) return base.GetTextReader(ordinal);
            RequireFieldType(ordinal, typeof(string)); return Sequential.GetTextReader(ordinal);
        }
        private void RequireFieldType(int ordinal, Type type)
        { if (Column(ordinal).FieldType != type) throw new InvalidCastException("The field has a different type."); }
        private static long Copy<T>(T[] value, long offset, T[]? buffer, int bufferOffset, int length)
        {
            if (offset < 0 || offset > value.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer == null) return value.Length;
            if (bufferOffset < 0 || length < 0 || bufferOffset > buffer.Length - length) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            var count = (int)Math.Min(length, value.Length - offset);
            Array.Copy(value, (int)offset, buffer, bufferOffset, count);
            return count;
        }
        public override bool NextResult()
        {
            EnsureOpen();
            _keyInfo = null;
            if (_singleResult) { DrainAllResults(); return false; }
            if (_singleRow && _singleRowReturned) DrainCurrentResult();
            var next = _result.NextResult();
            _singleRowReturned = false;
            return next;
        }
        public override bool Read()
        {
            EnsureOpen();
            if (_schemaOnly) { DrainCurrentResult(); return false; }
            if (_singleRow && _singleRowReturned) { DrainCurrentResult(); return false; }
            var read = _result.Read();
            if (read && _singleRow) _singleRowReturned = true;
            return read;
        }
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            EnsureOpen();
            if (_schemaOnly) { await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false); return false; }
            if (_singleRow && _singleRowReturned) { await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false); return false; }
            var read = await _result.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read && _singleRow) _singleRowReturned = true;
            return read;
        }
        public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            EnsureOpen();
            _keyInfo = null;
            if (_singleResult) { await DrainAllResultsAsync(cancellationToken).ConfigureAwait(false); return false; }
            if (_singleRow && _singleRowReturned) await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false);
            var next = await _result.NextResultAsync(cancellationToken).ConfigureAwait(false);
            _singleRowReturned = false;
            return next;
        }

        private void DrainCurrentResult()
        {
            while (_result.Read()) { }
        }

        private async Task DrainCurrentResultAsync(CancellationToken cancellationToken)
        {
            while (await _result.ReadAsync(cancellationToken).ConfigureAwait(false)) { }
        }

        private void DrainAllResults()
        {
            DrainCurrentResult();
            while (_result.NextResult()) DrainCurrentResult();
        }

        private async Task DrainAllResultsAsync(CancellationToken cancellationToken)
        {
            await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false);
            while (await _result.NextResultAsync(cancellationToken).ConfigureAwait(false))
                await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false);
        }
        public override void Close()
        {
            if (_closed) return;
            _closed = true;
            try { if (_result.DrainOnClose) DrainAllResults(); }
            finally
            {
                try { _query.Dispose(); }
                finally { _result.InvalidateRows(); if (_closeConnection) _connection.CloseSession(_session, _query.OpenGeneration); }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) Close(); base.Dispose(disposing); }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async Task CloseAsync()
        {
            if (_closed) return;
            _closed = true;
            try { if (_result.DrainOnClose) await DrainAllResultsAsync(CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                try { _query.Dispose(); }
                finally { _result.InvalidateRows(); if (_closeConnection) await _connection.CloseSessionAsync(_session, _query.OpenGeneration).ConfigureAwait(false); }
            }
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
