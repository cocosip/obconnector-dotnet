using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed class OceanBaseDataReader : DbDataReader
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
        public override string GetDataTypeName(int ordinal)
        {
            var column = Column(ordinal);
            switch (column.TypeCode)
            {
                case 1: return "TINYINT";
                case 2: return "SMALLINT";
                case 3: return column.Mode == OceanBaseCompatibilityMode.Oracle ? "NUMBER" : "INT";
                case 9: return "MEDIUMINT";
                case 8: return "BIGINT";
                case 4: return column.Mode == OceanBaseCompatibilityMode.Oracle ? "BINARY_FLOAT" : "FLOAT";
                case 5: return column.Mode == OceanBaseCompatibilityMode.Oracle ? "BINARY_DOUBLE" : "DOUBLE";
                case 6: return "NULL";
                case 15: return column.Charset == 63 ? "VARBINARY" : "VARCHAR";
                case 253: return column.Charset == 63 ? "VARBINARY" : column.Mode == OceanBaseCompatibilityMode.Oracle ? "VARCHAR2" : "VARCHAR";
                case 254: return column.Charset == 63 ? "BINARY" : "CHAR";
                case 252: return column.Charset == 63 ? "BLOB" : "TEXT";
                default: throw new NotSupportedException("The column type is not supported yet.");
            }
        }
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
        public override bool IsDBNull(int ordinal)
        {
            Column(ordinal);
            if (_result.Row == null) throw new InvalidOperationException("Read must position the reader on a row.");
            return _result.Row[ordinal] == null;
        }
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
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
        public override Task CloseAsync() { Close(); return Task.CompletedTask; }
        public override ValueTask DisposeAsync() { Dispose(); return default; }
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
