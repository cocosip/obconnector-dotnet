using System;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class ComplexColumnMetadata
    {
        private ComplexColumnMetadata(string schema, string name, long? version, byte? element)
        { Schema = schema; Name = name; Version = version; ElementType = element; }
        internal string Schema { get; }
        internal string Name { get; }
        internal long? Version { get; }
        internal byte? ElementType { get; }

        internal OceanBaseComplexType? CreateAnonymousType()
        {
            if (Name.Length != 0) return null;
            byte code; string name;
            switch (ElementType)
            {
                case 0: case 3: case 246: code = 0; name = "NUMBER"; break;
                case 253: code = 253; name = "VARCHAR2"; break;
                case 254: code = 254; name = "CHAR"; break;
                case 207: code = 207; name = "NVARCHAR2"; break;
                case 208: code = 208; name = "NCHAR"; break;
                case 203: code = 203; name = "RAW"; break;
                case 12: code = 12; name = "DATE"; break;
                case 202: code = 202; name = "TIMESTAMP"; break;
                default: return null;
            }
            return new OceanBaseComplexType(null, Schema, "", true,
                new[] { new OceanBaseComplexMember(null, null, name, null, new ComplexType(code)) });
        }

        internal static ComplexColumnMetadata Parse(ByteCursor cursor)
        {
            var schema = ReadName(cursor); var name = ReadName(cursor); var version = cursor.ReadNullableLengthEncodedInteger();
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
