using System;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class ComplexColumnMetadata
    {
        private ComplexColumnMetadata(string schema, string name, long version, byte? element)
        { Schema = schema; Name = name; Version = version; ElementType = element; }
        internal string Schema { get; }
        internal string Name { get; }
        internal long Version { get; }
        internal byte? ElementType { get; }

        internal static ComplexColumnMetadata Parse(ByteCursor cursor)
        {
            var schema = ReadName(cursor); var name = ReadName(cursor); var version = cursor.ReadLengthEncodedInteger();
            return new ComplexColumnMetadata(schema, name, version, name.Length == 0 ? (byte?)cursor.ReadUInt8() : null);
        }
        private static string ReadName(ByteCursor cursor)
        {
            var length = cursor.ReadLengthEncodedInteger();
            if (length > 512) throw new ProtocolException("A complex type identifier exceeds 512 UTF-8 bytes.");
            var name = ResultColumn.Utf8.GetString(cursor.ReadBytes((int)length));
            if (name.IndexOf('\0') >= 0) throw new ProtocolException("A complex type identifier contains NUL.");
            return name;
        }
        internal bool Matches(ComplexColumnMetadata? other) => other != null && Schema == other.Schema && Name == other.Name &&
            Version == other.Version && ElementType == other.ElementType;
    }
}
