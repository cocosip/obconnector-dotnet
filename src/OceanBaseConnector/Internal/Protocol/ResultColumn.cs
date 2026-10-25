using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class ResultColumn
    {
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private OceanBaseConnector.Internal.Session.LobSession? Lobs { get; set; }
        internal ComplexColumnMetadata? ComplexMetadata { get; private set; }
        internal OceanBaseComplexType? ComplexType { get; private set; }
        private bool _complexOutput;
        private OceanBaseConnector.Internal.Session.OracleCursorOwner? _cursors;
        internal void BindCursorResult(bool binary, OceanBaseConnector.Internal.Session.OracleCursorOwner? owner)
        {
            if (TypeCode != 163) return;
            if (!binary || Mode != OceanBaseCompatibilityMode.Oracle || owner?.HasParent != true)
                throw new NotSupportedException("Cursor columns require a binary Oracle result and its originating statement or cursor.");
            _cursors = owner;
        }
        internal void TrackCursor(byte[]? value)
        {
            if (TypeCode == 163 && value != null && _cursors != null) _cursors.Create(CursorId(value));
        }
        private static uint CursorId(byte[] value)
        {
            if (value.Length != 4) throw new ProtocolException("A cursor identifier requires four bytes.");
            return new ByteCursor(value).ReadUInt32();
        }
        internal void BindComplexOutput(OceanBaseComplexType expected)
        {
            var actual = ComplexType;
            if (TypeCode != 160 || actual == null || (expected.IsAnonymous
                ? !actual.IsAnonymous || expected.Members[0].ComplexType != null || actual.Members[0].Layout.TypeCode != expected.Members[0].Layout.TypeCode
                : !ReferenceEquals(actual, expected)))
                throw new ProtocolException("Complex output metadata must match its declared type.");
            ComplexType = expected; _complexOutput = true;
        }
        internal void RequireComplexResult(bool binary)
        {
            if (TypeCode != 160) return;
            if (!binary) throw new NotSupportedException("Complex columns require the binary result protocol.");
            _ = FieldType;
        }
        private ResultColumn(string database, string table, string originalTable, string name, string originalName,
            byte type, ushort charset, ushort flags, uint length, byte scale, byte precision, OceanBaseCompatibilityMode mode, string? sessionTimeZone, NationalCharacterSet? nationalCharacterSet, bool treatTinyAsBoolean)
        {
            Database = database; Table = table; OriginalTable = originalTable; OriginalName = originalName; Length = length; Flags = flags;
            SessionTimeZone = sessionTimeZone;
            NationalCharacterSet = nationalCharacterSet;
            Name = name; TypeCode = type; Charset = charset; Unsigned = (flags & 32) != 0; Mode = mode;
            IsTinyBoolean = treatTinyAsBoolean && mode == OceanBaseCompatibilityMode.MySql && type == 1 && !Unsigned && length == 1 && scale == 0;
            Scale = mode == OceanBaseCompatibilityMode.Oracle ? unchecked((sbyte)scale) : scale;
            Precision = mode == OceanBaseCompatibilityMode.Oracle ? precision : (long)length - (Unsigned ? 0 : 1) - (scale > 0 ? 1 : 0);
            if (mode == OceanBaseCompatibilityMode.Oracle && IsDecimal)
            {
                if (precision == 255) Precision = 0;
                if (Scale == -85) Scale = 0;
            }
            if (mode == OceanBaseCompatibilityMode.Oracle && type == 3 && precision == 0 && scale == 0) Precision = 10;
        }
        internal string? SessionTimeZone { get; private set; }
        private NationalCharacterSet? NationalCharacterSet { get; set; }
        internal Encoding TextEncoding => TypeCode == 207 || TypeCode == 208
            ? (NationalCharacterSet ?? throw new NotSupportedException("The result has no trusted national character set.")).ResultEncoding(Charset)
            : Charset != 63 && (TypeCode == 15 || TypeCode == 253 || TypeCode == 254 || TypeCode == 252)
                ? ResultCharacterSet.Resolve(Charset, out _) : Utf8;
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
        private bool IsTinyBoolean { get; }
        private bool HasUtf8Charset => ResultCharacterSet.IsUtf8(Charset);
        internal OceanBaseCompatibilityMode Mode { get; }
        internal long Precision { get; }
        internal int Scale { get; }
        internal bool IsDecimal => TypeCode == 0 || TypeCode == 246 || TypeCode == 3 && Mode == OceanBaseCompatibilityMode.Oracle;
        internal bool IsTemporal => TypeCode == 7 || TypeCode == 10 || TypeCode == 11 || TypeCode == 12 || TypeCode == 200 || TypeCode == 201 || TypeCode == 202;
        internal bool IsMySqlDate => Mode == OceanBaseCompatibilityMode.MySql && (TypeCode == 10 || TypeCode == 12 || TypeCode == 7);
        internal Type ProviderSpecificFieldType => IsMySqlDate ? TypeCode == 10 ? typeof(OceanBaseDate) : typeof(OceanBaseDateTime) : IsDecimal ? typeof(OceanBaseDecimal) : TypeCode == 202 ? typeof(OceanBaseTimestamp) : FieldType;

        internal OceanBaseTimestamp DecodeTimestamp(byte[] value)
        {
            _ = FieldType;
            return TemporalCodec.DecodeTimestamp(value, Scale);
        }

        internal static ResultColumn Parse(byte[] payload, OceanBaseCompatibilityMode mode, string? sessionTimeZone = null, NationalCharacterSet? nationalCharacterSet = null, OceanBaseConnector.Internal.Session.LobSession? lobs = null,
            IReadOnlyDictionary<(string, string), OceanBaseComplexType>? complexTypes = null,
            IReadOnlyDictionary<(string, string, string, string), OceanBaseComplexType>? sourceComplexTypes = null, bool treatTinyAsBoolean = true)
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
            var result = new ResultColumn(database, table, originalTable, name, originalName, type, charset, flags, length, scale, precision, mode, sessionTimeZone, nationalCharacterSet, treatTinyAsBoolean) { Lobs = lobs };
            if (type == 160)
            {
                if (mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("Complex columns require Oracle mode.");
                result.ComplexMetadata = ComplexColumnMetadata.Parse(cursor);
                if (result.ComplexMetadata.Name.Length == 0) result.ComplexType = result.ComplexMetadata.CreateAnonymousType();
                else if (complexTypes != null && complexTypes.TryGetValue((result.ComplexMetadata.Schema, result.ComplexMetadata.Name), out var description))
                    result.ComplexType = description;
                else if (result.ComplexMetadata.Schema.Length == 0 && sourceComplexTypes != null &&
                    sourceComplexTypes.TryGetValue((database, originalTable, originalName, result.ComplexMetadata.Name), out var sourceDescription))
                    result.ComplexType = sourceDescription;
            }
            if (cursor.HasRemaining) throw new NotSupportedException("Extended column metadata is not supported yet.");
            return result;
        }

        internal ResultColumn WithContext(OceanBaseConnector.Internal.Session.CommandExchange query)
        {
            var result = (ResultColumn)MemberwiseClone();
            result.Lobs = query.Lobs;
            result.SessionTimeZone = query.SessionTimeZone;
            result.NationalCharacterSet = query.NationalCharacterSet;
            var metadata = result.ComplexMetadata;
            if (metadata != null && metadata.Name.Length != 0)
            {
                if (query.ComplexTypes != null && query.ComplexTypes.TryGetValue((metadata.Schema, metadata.Name), out var description))
                    result.ComplexType = description;
                else if (metadata.Schema.Length == 0 && query.SourceComplexTypes != null &&
                    query.SourceComplexTypes.TryGetValue((Database, OriginalTable, OriginalName, metadata.Name), out var source))
                    result.ComplexType = source;
            }
            else if (metadata != null && query.SourceComplexTypes != null)
                foreach (var entry in query.SourceComplexTypes)
                    if (entry.Key.Item1 == Database && entry.Key.Item2 == OriginalTable && entry.Key.Item3 == OriginalName)
                    { result.ComplexType = entry.Value; break; }
            return result;
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
                    case 13: return "YEAR";
                    case 16: return "BIT";
                    case 245: return "JSON";
                    case 200: return "TIMESTAMP WITH TIME ZONE";
                    case 201: return "TIMESTAMP WITH LOCAL TIME ZONE";
                    case 203: return "RAW";
                    case 160: return ComplexType == null ? throw new NotSupportedException("The complex type layout is unavailable.") : ComplexType.IsAnonymous ? "ARRAY" : ComplexType.TypeName;
                    case 163: return "REF CURSOR";
                    case 210: return "BLOB";
                    case 211: return "CLOB";
                    case 204: return "INTERVAL YEAR TO MONTH";
                    case 205: return "INTERVAL DAY TO SECOND";
                    case 207: return "NVARCHAR2";
                    case 208: return "NCHAR";
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
                    var oracle = Mode == OceanBaseCompatibilityMode.Oracle;
                    if (Precision > (oracle ? 38 : 65) || Scale < (oracle ? -84 : 0) || Scale > (oracle ? 127 : 30) ||
                        !oracle && Precision > 0 && Scale > Precision)
                        throw new NotSupportedException("The declared decimal metadata exceeds the supported mode limits.");
                    return Precision >= 1 && Precision <= 28 && Scale >= 0 && Scale <= Precision ? typeof(decimal) : typeof(OceanBaseDecimal);
                }
                switch (TypeCode)
                {
                    case 1: return IsTinyBoolean ? typeof(bool) : Unsigned ? typeof(byte) : typeof(sbyte);
                    case 2: return Unsigned ? typeof(ushort) : typeof(short);
                    case 3:
                    case 9: return Unsigned ? typeof(uint) : typeof(int);
                    case 8: return Unsigned ? typeof(ulong) : typeof(long);
                    case 4: return typeof(float);
                    case 5: return typeof(double);
                    case 6: return typeof(DBNull);
                    case 13:
                        if (Mode != OceanBaseCompatibilityMode.MySql || Length != 4 || Scale != 0)
                            throw new NotSupportedException("Only four-digit MySQL YEAR columns are supported.");
                        return typeof(ushort);
                    case 16:
                        if (Mode != OceanBaseCompatibilityMode.MySql || Length < 1 || Length > 64 || Scale != 0)
                            throw new NotSupportedException("Only MySQL BIT columns with 1 through 64 bits and zero scale are supported.");
                        return typeof(ulong);
                    case 7:
                    case 10:
                    case 11:
                        if (Mode != OceanBaseCompatibilityMode.MySql) throw new NotSupportedException("The temporal type is not defined for this mode.");
                        if (Scale < 0 || Scale > (TypeCode == 10 ? 0 : 6)) throw new NotSupportedException("The temporal precision is not supported.");
                        return TypeCode == 11 ? typeof(TimeSpan) : typeof(DateTime);
                    case 12:
                        if (Scale < 0 || Scale > (Mode == OceanBaseCompatibilityMode.Oracle ? 0 : 6)) throw new NotSupportedException("The temporal precision is not supported.");
                        return typeof(DateTime);
                    case 200:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || Scale < 0 || Scale > 9) throw new NotSupportedException("The timestamp metadata is not supported.");
                        return typeof(OceanBaseTimestampWithTimeZone);
                    case 201:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || Scale < 0 || Scale > 9) throw new NotSupportedException("The timestamp metadata is not supported.");
                        return typeof(OceanBaseTimestampLocalTimeZone);
                    case 202:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || Scale < 0 || Scale > 9) throw new NotSupportedException("The timestamp metadata is not supported.");
                        return Scale <= 7 ? typeof(DateTime) : typeof(OceanBaseTimestamp);
                    case 163:
                        if (Mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("REF CURSOR requires Oracle mode.");
                        return typeof(OceanBaseRefCursor);
                    case 160:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || ComplexType == null)
                            throw new NotSupportedException("The complex layout is unavailable; preload named types and use a supported anonymous scalar element.");
                        return typeof(OceanBaseComplexValue);
                    case 210:
                    case 211:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || Lobs == null || TypeCode == 211 && !HasUtf8Charset)
                            throw new NotSupportedException("LOB locators require negotiated Oracle support and a supported CLOB character set.");
                        return typeof(OceanBaseLob);
                    case 203:
                        if (Mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("RAW is only defined in Oracle mode.");
                        return typeof(byte[]);
                    case 204:
                    case 205:
                        if (Mode != OceanBaseCompatibilityMode.Oracle || Scale < 0 || Scale > (TypeCode == 204 ? 9 : 99))
                            throw new NotSupportedException("The Oracle INTERVAL metadata is not supported.");
                        return TypeCode == 204 ? typeof(OceanBaseIntervalYearToMonth) : typeof(OceanBaseIntervalDayToSecond);
                    case 207:
                    case 208:
                        if (Mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("National character types require Oracle mode.");
                        return typeof(string);
                    case 245:
                        if (Mode != OceanBaseCompatibilityMode.MySql || Charset != 63 && !HasUtf8Charset)
                            throw new NotSupportedException("Only MySQL JSON text with a supported UTF-8 or binary collation is supported.");
                        return typeof(string);
                    case 15:
                    case 253:
                    case 254:
                    case 252:
                        if (Charset == 63) return typeof(byte[]);
                        _ = TextEncoding;
                        return typeof(string);
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
                    case 1: return IsTinyBoolean ? OceanBaseDbType.Boolean : Unsigned ? OceanBaseDbType.Byte : OceanBaseDbType.SByte;
                    case 2: return Unsigned ? OceanBaseDbType.UInt16 : OceanBaseDbType.Int16;
                    case 3:
                    case 9: return Unsigned ? OceanBaseDbType.UInt32 : OceanBaseDbType.Int32;
                    case 8: return Unsigned ? OceanBaseDbType.UInt64 : OceanBaseDbType.Int64;
                    case 4: return OceanBaseDbType.Single;
                    case 5: return OceanBaseDbType.Double;
                    case 6: return OceanBaseDbType.Null;
                    case 13: return OceanBaseDbType.Year;
                    case 16: return OceanBaseDbType.Bit;
                    case 245: return OceanBaseDbType.Json;
                    case 7:
                    case 202: return OceanBaseDbType.Timestamp;
                    case 10: return OceanBaseDbType.Date;
                    case 11: return OceanBaseDbType.Time;
                    case 12: return Mode == OceanBaseCompatibilityMode.Oracle ? OceanBaseDbType.OracleDate : OceanBaseDbType.DateTime;
                    case 200: return OceanBaseDbType.TimestampWithTimeZone;
                    case 201: return OceanBaseDbType.TimestampLocalTimeZone;
                    case 203: return OceanBaseDbType.Binary;
                    case 163: return OceanBaseDbType.RefCursor;
                    case 160: return ComplexType?.IsCollection == true ? OceanBaseDbType.Array : OceanBaseDbType.Struct;
                    case 210: return OceanBaseDbType.Blob;
                    case 211: return OceanBaseDbType.Clob;
                    case 204: return OceanBaseDbType.IntervalYearToMonth;
                    case 205: return OceanBaseDbType.IntervalDayToSecond;
                    case 207: return OceanBaseDbType.NVarchar2;
                    case 208: return OceanBaseDbType.NChar;
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
            RequireComplexResult(false);
            var fieldType = FieldType;
            if (value == null) return DBNull.Value;
            if (TypeCode == 163) throw new NotSupportedException("REF CURSOR columns require the binary Oracle result protocol.");
            if (TypeCode == 210 || TypeCode == 211) return new OceanBaseLob(Lobs!, value, TypeCode == 211);
            if (TypeCode == 13) return YearCodec.Decode(value, false);
            if (TypeCode == 16) return BitCodec.Decode(value, (int)Length);
            if (TypeCode == 204 || TypeCode == 205) return IntervalCodec.Decode(value, TypeCode == 205, Scale);
            if (IsDecimal)
            {
                var number = DecodeDecimal(value, false);
                return fieldType == typeof(decimal) ? (object)number.ToDecimal() : number;
            }
            if (IsMySqlDate) return TemporalCodec.DecodeMySqlDate(TypeCode, value, false, Scale).ToDateTime();
            if (TypeCode == 201)
            {
                if (SessionTimeZone == null) throw new NotSupportedException("The result has no trusted session time zone for LTZ values.");
                return new OceanBaseTimestampLocalTimeZone(TemporalCodec.DecodeTimestamp(value, Scale), SessionTimeZone);
            }
            if (TypeCode == 200) return TemporalCodec.DecodeTimestampWithTimeZone(value, Scale);
            if (TypeCode == 202) return TemporalCodec.DecodeBinary(TypeCode, Mode, value, Scale);
            if (IsTemporal) return TemporalCodec.DecodeText(TypeCode, Mode, Utf8.GetString(value));
            if (fieldType == typeof(byte[])) return (byte[])value.Clone();
            var text = TextEncoding.GetString(value);
            switch (TypeCode)
            {
                case 1:
                    if (IsTinyBoolean) return sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) != 0;
                    return Unsigned ? (object)byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
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
            if (TypeCode == 160) return ComplexValueCodec.ReadResultField(cursor, ComplexType!.Layout, Mode);
            if (IsTemporal) return TemporalCodec.ReadField(cursor, TypeCode);
            var length = BinaryFixedLength;
            return length < 0 ? cursor.ReadLengthEncodedBytes() : cursor.ReadBytes(length);
        }

        internal int BinaryFixedLength
        {
            get
            {
                switch (TypeCode)
                {
                    case 1: return 1;
                    case 2:
                    case 13: return 2;
                    case 3:
                    case 9:
                    case 163:
                    case 4: return 4;
                    case 8:
                    case 5: return 8;
                    case 6: throw new ProtocolException("A NULL column contained a non-NULL value.");
                    default: return -1;
                }
            }
        }

        internal object DecodeBinary(byte[]? value)
        {
            var fieldType = FieldType;
            if (value == null) return DBNull.Value;
            if (TypeCode == 160)
            {
                var complex = new ByteCursor(value);
                // Stored complex members retain the database encoding; PL outputs are converted to the session national encoding.
                var encoding = ComplexType!.Layout.HasNationalMembers && _complexOutput
                    ? (NationalCharacterSet ?? throw new NotSupportedException("The complex output has no trusted national character set.")).ResultEncoding(Charset)
                    : ComplexType.Context?.StoredNationalEncoding;
                var values = ComplexValueCodec.DecodeResult(complex, ComplexType.Layout, Mode, encoding);
                if (complex.HasRemaining) throw new ProtocolException("Trailing complex value bytes.");
                return OceanBaseComplexValue.FromResult(ComplexType, values);
            }
            if (TypeCode == 163) return (_cursors ?? throw new NotSupportedException("A REF CURSOR requires its originating execution.")).Publish(CursorId(value));
            if (TypeCode == 13) return YearCodec.Decode(value, true);
            if (TypeCode == 16) return BitCodec.Decode(value, (int)Length);
            if (IsDecimal)
            {
                var number = DecodeDecimal(value, true);
                return fieldType == typeof(decimal) ? (object)number.ToDecimal() : number;
            }
            if (IsMySqlDate) return TemporalCodec.DecodeMySqlDate(TypeCode, value, true, Scale).ToDateTime();
            if (TypeCode == 201)
            {
                if (SessionTimeZone == null) throw new NotSupportedException("The result has no trusted session time zone for LTZ values.");
                return new OceanBaseTimestampLocalTimeZone(TemporalCodec.DecodeTimestamp(value, Scale), SessionTimeZone);
            }
            if (TypeCode == 200) return TemporalCodec.DecodeTimestampWithTimeZone(value, Scale);
            if (IsTemporal) return TemporalCodec.DecodeBinary(TypeCode, Mode, value, Scale);
            var cursor = new ByteCursor(value);
            switch (TypeCode)
            {
                case 1:
                    if (IsTinyBoolean) return cursor.ReadUInt8() != 0;
                    return Unsigned ? (object)cursor.ReadUInt8() : unchecked((sbyte)cursor.ReadUInt8());
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

        internal object DecodeProviderDate(byte[] value, bool binary)
        {
            _ = FieldType;
            var date = TemporalCodec.DecodeMySqlDate(TypeCode, value, binary, Scale);
            return TypeCode == 10 ? (object)date.Date : date;
        }

        internal OceanBaseDecimal DecodeDecimal(byte[] value, bool binary, bool constrain = true)
        {
            _ = FieldType;
            OceanBaseDecimal number;
            if (TypeCode == 3 && binary)
            {
                var integer = new ByteCursor(value).ReadUInt32();
                number = new OceanBaseDecimal(Unsigned ? (decimal)integer : unchecked((int)integer));
            }
            else number = DecimalCodec.Parse(Utf8.GetString(value));
            // Oracle can report the same unknown precision/scale for binds and declared NUMBER(*,s).
            if (constrain && (Precision > 0 || Mode != OceanBaseCompatibilityMode.Oracle && Scale != 0))
                number = DecimalCodec.Constrain(number, Precision > 0 ? (int)Precision : Mode == OceanBaseCompatibilityMode.Oracle ? 38 : 65, Scale, Unsigned);
            DecimalCodec.Validate(number, Mode, Unsigned);
            return number;
        }
    }
}
