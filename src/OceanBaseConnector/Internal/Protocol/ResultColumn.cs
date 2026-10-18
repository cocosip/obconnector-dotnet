using System;
using System.Globalization;
using System.Text;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class ResultColumn
    {
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private ResultColumn(string database, string table, string originalTable, string name, string originalName,
            byte type, ushort charset, ushort flags, uint length, byte scale, byte precision, OceanBaseCompatibilityMode mode)
        {
            Database = database; Table = table; OriginalTable = originalTable; OriginalName = originalName; Length = length; Flags = flags;
            Name = name; TypeCode = type; Charset = charset; Unsigned = (flags & 32) != 0; Mode = mode;
            Scale = mode == OceanBaseCompatibilityMode.Oracle ? unchecked((sbyte)scale) : scale;
            Precision = mode == OceanBaseCompatibilityMode.Oracle ? precision : (long)length - (Unsigned ? 0 : 1) - (scale > 0 ? 1 : 0);
            if (mode == OceanBaseCompatibilityMode.Oracle && type == 3 && precision == 0 && scale == 0) Precision = 10;
        }
        internal string Name { get; }
        internal string Database { get; }
        internal string Table { get; }
        internal string OriginalTable { get; }
        internal string OriginalName { get; }
        internal uint Length { get; }
        internal ushort Flags { get; }
        internal byte TypeCode { get; }
        internal ushort Charset { get; }
        internal bool Unsigned { get; }
        internal OceanBaseCompatibilityMode Mode { get; }
        internal long Precision { get; }
        internal int Scale { get; }
        internal bool IsDecimal => TypeCode == 0 || TypeCode == 246 || TypeCode == 3 && Mode == OceanBaseCompatibilityMode.Oracle;
        internal bool IsTemporal => TypeCode == 7 || TypeCode == 10 || TypeCode == 11 || TypeCode == 12 || TypeCode == 202;

        internal static ResultColumn Parse(byte[] payload, OceanBaseCompatibilityMode mode)
        {
            var cursor = new ByteCursor(payload);
            cursor.ReadLengthEncodedBytes(); // Protocol catalog marker, not the database catalog name.
            var database = Utf8.GetString(cursor.ReadLengthEncodedBytes());
            var table = Utf8.GetString(cursor.ReadLengthEncodedBytes());
            var originalTable = Utf8.GetString(cursor.ReadLengthEncodedBytes());
            var name = Utf8.GetString(cursor.ReadLengthEncodedBytes());
            var originalName = Utf8.GetString(cursor.ReadLengthEncodedBytes());
            if (cursor.ReadLengthEncodedInteger() != 12) throw new ProtocolException("Unsupported column metadata layout.");
            var charset = cursor.ReadUInt16();
            var length = cursor.ReadUInt32();
            var type = cursor.ReadUInt8();
            var flags = cursor.ReadUInt16();
            // Oracle uses the last two bytes for precision and parameter direction.
            var scale = cursor.ReadUInt8();
            var precision = cursor.ReadUInt8();
            cursor.ReadUInt8();
            if (cursor.HasRemaining) throw new NotSupportedException("Extended column metadata is not supported yet.");
            return new ResultColumn(database, table, originalTable, name, originalName, type, charset, flags, length, scale, precision, mode);
        }

        internal string DataTypeName
        {
            get
            {
                switch (TypeCode)
                {
                    case 0:
                    case 246: return Mode == OceanBaseCompatibilityMode.Oracle ? "NUMBER" : "DECIMAL";
                    case 1: return "TINYINT";
                    case 2: return "SMALLINT";
                    case 3: return Mode == OceanBaseCompatibilityMode.Oracle ? "NUMBER" : "INT";
                    case 9: return "MEDIUMINT";
                    case 8: return "BIGINT";
                    case 4: return Mode == OceanBaseCompatibilityMode.Oracle ? "BINARY_FLOAT" : "FLOAT";
                    case 5: return Mode == OceanBaseCompatibilityMode.Oracle ? "BINARY_DOUBLE" : "DOUBLE";
                    case 6: return "NULL";
                    case 7:
                    case 202: return "TIMESTAMP";
                    case 10: return "DATE";
                    case 11: return "TIME";
                    case 12: return Mode == OceanBaseCompatibilityMode.Oracle ? "DATE" : "DATETIME";
                    case 203: return "RAW";
                    case 15: return Charset == 63 ? "VARBINARY" : "VARCHAR";
                    case 253: return Charset == 63 ? "VARBINARY" : Mode == OceanBaseCompatibilityMode.Oracle ? "VARCHAR2" : "VARCHAR";
                    case 254: return Charset == 63 ? "BINARY" : "CHAR";
                    case 252: return Charset == 63 ? "BLOB" : "TEXT";
                    default: throw new NotSupportedException("The column type is not supported yet.");
                }
            }
        }

        internal Type FieldType
        {
            get
            {
                if (IsDecimal)
                {
                    if (Precision < 1 || Precision > 28 || Scale < 0 || Scale > Precision)
                        throw new NotSupportedException("The declared decimal precision or scale requires the C03 implementation.");
                    return typeof(decimal);
                }
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
                    case 7:
                    case 10:
                    case 11:
                        if (Mode != OceanBaseCompatibilityMode.MySql) throw new NotSupportedException("The temporal type is not defined for this mode.");
                        if (Scale < 0 || Scale > 6) throw new NotSupportedException("The temporal precision is not supported.");
                        return TypeCode == 11 ? typeof(TimeSpan) : typeof(DateTime);
                    case 12:
                        if (Scale < 0 || Scale > (Mode == OceanBaseCompatibilityMode.Oracle ? 0 : 6)) throw new NotSupportedException("The temporal precision is not supported.");
                        return typeof(DateTime);
                    case 202:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || Scale < 0 || Scale > 7) throw new NotSupportedException("The timestamp precision requires C03.");
                        return typeof(DateTime);
                    case 203:
                        if (Mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("RAW is only defined in Oracle mode.");
                        return typeof(byte[]);
                    case 15:
                    case 253:
                    case 254:
                    case 252:
                        if (Charset == 63) return typeof(byte[]);
                        // Only the negotiated UTF-8 families are decoded; never substitute another charset.
                        if (Charset == 33 || Charset == 45 || Charset == 46 || Charset == 83 || Charset == 224 || Charset == 255)
                            return typeof(string);
                        throw new NotSupportedException("The column character set is not supported yet.");
                    default: throw new NotSupportedException("The column type is not supported by the text reader yet.");
                }
            }
        }

        internal OceanBaseDbType ProviderType
        {
            get
            {
                if (IsDecimal) return OceanBaseDbType.Decimal;
                switch (TypeCode)
                {
                    case 1: return Unsigned ? OceanBaseDbType.Byte : OceanBaseDbType.SByte;
                    case 2: return Unsigned ? OceanBaseDbType.UInt16 : OceanBaseDbType.Int16;
                    case 3:
                    case 9: return Unsigned ? OceanBaseDbType.UInt32 : OceanBaseDbType.Int32;
                    case 8: return Unsigned ? OceanBaseDbType.UInt64 : OceanBaseDbType.Int64;
                    case 4: return OceanBaseDbType.Single;
                    case 5: return OceanBaseDbType.Double;
                    case 6: return OceanBaseDbType.Null;
                    case 7:
                    case 202: return OceanBaseDbType.Timestamp;
                    case 10: return OceanBaseDbType.Date;
                    case 11: return OceanBaseDbType.Time;
                    case 12: return Mode == OceanBaseCompatibilityMode.Oracle ? OceanBaseDbType.OracleDate : OceanBaseDbType.DateTime;
                    case 203: return OceanBaseDbType.Binary;
                    case 15:
                    case 253:
                    case 254:
                    case 252: return Charset == 63 ? OceanBaseDbType.Binary : OceanBaseDbType.String;
                    default: throw new NotSupportedException("The column type has no supported provider identity.");
                }
            }
        }

        internal object Decode(byte[]? value)
        {
            var fieldType = FieldType;
            if (value == null) return DBNull.Value;
            if (IsDecimal) return DecodeDecimal(value, false).ToDecimal();
            if (TypeCode == 202) return TemporalCodec.DecodeBinary(TypeCode, Mode, value, Scale);
            if (IsTemporal) return TemporalCodec.DecodeText(TypeCode, Mode, Utf8.GetString(value));
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

        internal byte[] ReadBinaryField(ByteCursor cursor)
        {
            _ = FieldType;
            if (IsTemporal) return TemporalCodec.ReadField(cursor, TypeCode);
            switch (TypeCode)
            {
                case 1: return cursor.ReadBytes(1);
                case 2: return cursor.ReadBytes(2);
                case 3:
                case 9:
                case 4: return cursor.ReadBytes(4);
                case 8:
                case 5: return cursor.ReadBytes(8);
                case 6: throw new ProtocolException("A NULL column contained a non-NULL value.");
                default: return cursor.ReadLengthEncodedBytes();
            }
        }

        internal object DecodeBinary(byte[]? value)
        {
            _ = FieldType;
            if (value == null) return DBNull.Value;
            if (IsDecimal) return DecodeDecimal(value, true).ToDecimal();
            if (IsTemporal) return TemporalCodec.DecodeBinary(TypeCode, Mode, value, Scale);
            var cursor = new ByteCursor(value);
            switch (TypeCode)
            {
                case 1: return Unsigned ? (object)cursor.ReadUInt8() : unchecked((sbyte)cursor.ReadUInt8());
                case 2: return Unsigned ? (object)cursor.ReadUInt16() : unchecked((short)cursor.ReadUInt16());
                case 3:
                case 9: return Unsigned ? (object)cursor.ReadUInt32() : unchecked((int)cursor.ReadUInt32());
                case 8: return Unsigned ? (object)cursor.ReadUInt64() : unchecked((long)cursor.ReadUInt64());
                case 4:
                case 5:
                    if (!BitConverter.IsLittleEndian) { value = (byte[])value.Clone(); Array.Reverse(value); }
                    return TypeCode == 4 ? (object)BitConverter.ToSingle(value, 0) : BitConverter.ToDouble(value, 0);
                default: return Decode(value);
            }
        }

        internal OceanBaseDecimal DecodeDecimal(byte[] value, bool binary)
        {
            _ = FieldType;
            if (TypeCode == 3 && binary)
            {
                var integer = new ByteCursor(value).ReadUInt32();
                var number = Unsigned ? (decimal)integer : unchecked((int)integer);
                return DecimalCodec.Constrain(new OceanBaseDecimal(number), (int)Precision, Scale, Unsigned);
            }
            return DecimalCodec.Parse(Utf8.GetString(value), (int)Precision, Scale, Unsigned);
        }
    }
}
