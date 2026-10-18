using System;
using System.Globalization;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class TextColumn
    {
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private TextColumn(string name, byte type, ushort charset, ushort flags, OceanBaseCompatibilityMode mode)
        { Name = name; TypeCode = type; Charset = charset; Unsigned = (flags & 32) != 0; Mode = mode; }
        internal string Name { get; }
        internal byte TypeCode { get; }
        internal ushort Charset { get; }
        internal bool Unsigned { get; }
        internal OceanBaseCompatibilityMode Mode { get; }

        internal static TextColumn Parse(byte[] payload, OceanBaseCompatibilityMode mode)
        {
            var cursor = new ByteCursor(payload);
            for (var i = 0; i < 4; i++) cursor.ReadLengthEncodedBytes();
            var name = Utf8.GetString(cursor.ReadLengthEncodedBytes());
            cursor.ReadLengthEncodedBytes();
            if (cursor.ReadLengthEncodedInteger() != 12) throw new ProtocolException("Unsupported column metadata layout.");
            var charset = cursor.ReadUInt16();
            cursor.ReadUInt32();
            var type = cursor.ReadUInt8();
            var flags = cursor.ReadUInt16();
            // Oracle uses the last two bytes for precision and parameter direction.
            cursor.Skip(3);
            if (cursor.HasRemaining) throw new NotSupportedException("Extended column metadata is not supported yet.");
            return new TextColumn(name, type, charset, flags, mode);
        }

        internal Type FieldType
        {
            get
            {
                if (TypeCode == 3 && Mode == OceanBaseCompatibilityMode.Oracle)
                    throw new NotSupportedException("Oracle NUMBER decoding requires the B05 precise numeric implementation.");
                switch (TypeCode)
                {
                    case 1: return Unsigned ? typeof(byte) : typeof(sbyte);
                    case 2: return Unsigned ? typeof(ushort) : typeof(short);
                    case 3:
                    case 9: return Unsigned ? typeof(uint) : typeof(int);
                    case 8: return Unsigned ? typeof(ulong) : typeof(long);
                    case 4: return typeof(float);
                    case 5: return typeof(double);
                    case 6: return typeof(DBNull);
                    case 15:
                    case 253:
                    case 254:
                        if (Charset == 63) return typeof(byte[]);
                        // Only the negotiated UTF-8 families are decoded; never substitute another charset.
                        if (Charset == 33 || Charset == 45 || Charset == 46 || Charset == 83 || Charset == 224 || Charset == 255)
                            return typeof(string);
                        throw new NotSupportedException("The column character set is not supported yet.");
                    default: throw new NotSupportedException("The column type is not supported by the text reader yet.");
                }
            }
        }

        internal object Decode(byte[]? value)
        {
            var fieldType = FieldType;
            if (value == null) return DBNull.Value;
            if (fieldType == typeof(byte[])) return (byte[])value.Clone();
            var text = Utf8.GetString(value);
            switch (TypeCode)
            {
                case 1: return Unsigned ? (object)byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                case 2: return Unsigned ? (object)ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                case 3:
                case 9: return Unsigned ? (object)uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                case 8: return Unsigned ? (object)ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                case 4: return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                case 5: return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                case 6: throw new ProtocolException("A NULL column contained a non-NULL value.");
                default: return text;
            }
        }
    }
}
