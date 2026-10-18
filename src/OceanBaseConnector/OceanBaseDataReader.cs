using System;
using System.Collections;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    // Command execution cannot return a reader until result decoding is implemented.
    public sealed class OceanBaseDataReader : DbDataReader
    {
        private OceanBaseDataReader() { }
        public override object this[int ordinal] => throw Unsupported();
        public override object this[string name] => throw Unsupported();
        public override int Depth => throw Unsupported();
        public override int FieldCount => throw Unsupported();
        public override bool HasRows => throw Unsupported();
        public override bool IsClosed => true;
        public override int RecordsAffected => throw Unsupported();
        public override bool GetBoolean(int ordinal) => throw Unsupported();
        public override byte GetByte(int ordinal) => throw Unsupported();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw Unsupported();
        public override char GetChar(int ordinal) => throw Unsupported();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw Unsupported();
        public override string GetDataTypeName(int ordinal) => throw Unsupported();
        public override DateTime GetDateTime(int ordinal) => throw Unsupported();
        public override decimal GetDecimal(int ordinal) => throw Unsupported();
        public override double GetDouble(int ordinal) => throw Unsupported();
        public override Type GetFieldType(int ordinal) => throw Unsupported();
        public override float GetFloat(int ordinal) => throw Unsupported();
        public override Guid GetGuid(int ordinal) => throw Unsupported();
        public override short GetInt16(int ordinal) => throw Unsupported();
        public override int GetInt32(int ordinal) => throw Unsupported();
        public override long GetInt64(int ordinal) => throw Unsupported();
        public override string GetName(int ordinal) => throw Unsupported();
        public override int GetOrdinal(string name) => throw Unsupported();
        public override string GetString(int ordinal) => throw Unsupported();
        public override object GetValue(int ordinal) => throw Unsupported();
        public override int GetValues(object[] values) => throw Unsupported();
        public override bool IsDBNull(int ordinal) => throw Unsupported();
        public override bool NextResult() => throw Unsupported();
        public override bool Read() => throw Unsupported();
        public override IEnumerator GetEnumerator() => throw Unsupported();
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromException<bool>(Unsupported());
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromException<bool>(Unsupported());
        private static NotSupportedException Unsupported() => new NotSupportedException("Result readers are not implemented by the current provider stage.");
    }
}
