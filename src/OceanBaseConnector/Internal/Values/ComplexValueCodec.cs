using System;
using System.Globalization;
using System.IO;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector.Internal.Values
{
    internal static class ComplexValueCodec
    {
        internal const int MaximumMembers = 65535;
        private const int MaximumDepth = 32;
        private const long MaximumBytes = PacketChannel.DefaultMaxPacketSize - 10;

        internal static object?[] DecodeResult(ByteCursor cursor, ComplexType type, OceanBaseCompatibilityMode mode, Encoding? nationalEncoding = null, LobSession? lobs = null)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers;
            return Read(cursor, type, 0, ref budget, cursor.Remaining, nationalEncoding: nationalEncoding, lobs: lobs);
        }
        internal static byte[] ReadResultField(ByteCursor cursor, ComplexType type, OceanBaseCompatibilityMode mode)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers; var position = cursor.Position;
            Read(cursor, type, 0, ref budget, cursor.Remaining, materialize: false);
            return cursor.CopyConsumed(position);
        }

        internal static byte[] EncodeParameter(ComplexType type, object?[] values, OceanBaseCompatibilityMode mode, NationalCharacterSet? national = null, bool validateOnly = false, Encoding? clientEncoding = null)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, ResultColumn.Utf8, leaveOpen: true);
            WriteParameter(writer, type, values, mode, national, validateOnly, clientEncoding); return stream.ToArray();
        }

        internal static void WriteParameter(BinaryWriter writer, ComplexType type, object?[] values, OceanBaseCompatibilityMode mode, NationalCharacterSet? national = null, bool validateOnly = false, Encoding? clientEncoding = null)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers;
            Write(writer, type, values, 0, ref budget, national, validateOnly, clientEncoding ?? ResultColumn.Utf8);
        }

        private static void ValidateRoot(ComplexType type, OceanBaseCompatibilityMode mode)
        {
            if (mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("Complex values require Oracle mode.");
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.TypeCode != 160) throw new ArgumentException("A complex value requires an object or collection layout.", nameof(type));
        }

        private static object?[] Read(ByteCursor cursor, ComplexType type, int depth, ref int budget, int start, bool materialize = true, Encoding? nationalEncoding = null, LobSession? lobs = null)
        {
            if (depth >= MaximumDepth) throw new ProtocolException("Complex nesting exceeds the supported depth.");
            var count = type.IsCollection ? cursor.ReadLengthEncodedInteger() : type.Members.Count;
            if (count > budget) throw new ProtocolException("Complex member count exceeds the supported limit.");
            budget -= (int)count;
            // Result bitmaps reserve two bits at every nesting level, unlike parameter bitmaps.
            var bitmap = ReadBytes(cursor, ((int)count + 9) / 8, start);
            var values = materialize ? new object?[count] : System.Array.Empty<object?>();
            for (var i = 0; i < count; i++)
            {
                if ((bitmap[(i + 2) / 8] & 1 << ((i + 2) % 8)) != 0) continue;
                var member = type.Members[type.IsCollection ? 0 : i];
                if (member.TypeCode == 160)
                {
                    var nested = Read(cursor, member, depth + 1, ref budget, start, materialize, nationalEncoding, lobs);
                    if (materialize) values[i] = nested;
                }
                else
                {
                    var length = cursor.ReadLengthEncodedInteger();
                    if (length > int.MaxValue || member.TypeCode == 0 && length > 512)
                        throw new ProtocolException("Invalid complex scalar length.");
                    if (member.TypeCode == 12 || member.TypeCode == 202) TemporalCodec.ValidateFieldLength(member.TypeCode, length);
                    if (!materialize)
                    {
                        if (length > MaximumBytes - (start - cursor.Remaining)) throw new ProtocolException("Complex data exceeds the supported byte limit.");
                        cursor.Skip((int)length); continue;
                    }
                    var bytes = ReadBytes(cursor, (int)length, start);
                    switch (member.TypeCode)
                    {
                        case 0:
                            var number = DecimalCodec.Parse(ResultColumn.Utf8.GetString(bytes).Trim());
                            DecimalCodec.Validate(number, OceanBaseCompatibilityMode.Oracle); values[i] = number; break;
                        case 253: case 254: values[i] = ResultColumn.Utf8.GetString(bytes); break;
                        case 207:
                        case 208:
                            values[i] = (nationalEncoding ?? throw new NotSupportedException("The complex result has no trusted national character set.")).GetString(bytes); break;
                        case 203: values[i] = bytes; break;
                        case 12: values[i] = TemporalCodec.DecodeBinary(12, OceanBaseCompatibilityMode.Oracle, bytes, 0); break;
                        case 202:
                            values[i] = TemporalCodec.DecodeTimestamp(bytes, bytes[11]); break;
                        case 210:
                        case 211:
                            values[i] = new OceanBaseLob(lobs ?? throw new NotSupportedException("Complex LOB members require negotiated Oracle LOB locators."), bytes, member.TypeCode == 211); break;
                        default: throw new NotSupportedException("The complex scalar type is not supported.");
                    }
                }
            }
            return values;
        }

        private static byte[] ReadBytes(ByteCursor cursor, int count, int start)
        {
            if (count > MaximumBytes - (start - cursor.Remaining)) throw new ProtocolException("Complex data exceeds the supported byte limit.");
            return cursor.ReadBytes(count);
        }

        private static void Write(BinaryWriter writer, ComplexType type, object?[] values, int depth, ref int budget, NationalCharacterSet? national, bool validateOnly, Encoding clientEncoding)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (depth >= MaximumDepth || values.Length > budget) throw new NotSupportedException("Complex nesting or member count exceeds the supported limit.");
            if (!type.IsCollection && values.Length != type.Members.Count) throw new ArgumentException("The object member count does not match its type.", nameof(values));
            budget -= values.Length;
            var prefix = type.IsCollection ? (values.Length < 251 ? 1 : 3) : 0;
            Reserve(writer, (values.Length + 7) / 8 + prefix);
            if (type.IsCollection)
            {
                if (values.Length < 251) writer.Write((byte)values.Length);
                else { writer.Write((byte)252); writer.Write((ushort)values.Length); }
            }
            var bitmap = new byte[(values.Length + 7) / 8];
            for (var i = 0; i < values.Length; i++) if (values[i] == null || values[i] == DBNull.Value) bitmap[i / 8] |= (byte)(1 << (i % 8));
            writer.Write(bitmap);
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i]; if (value == null || value == DBNull.Value) continue;
                var member = type.Members[type.IsCollection ? 0 : i];
                if (member.TypeCode == 160)
                {
                    if (!(value is object?[] nested)) throw new ArgumentException("A nested complex value requires an object array.");
                    Write(writer, member, nested, depth + 1, ref budget, national, validateOnly, clientEncoding); continue;
                }
                switch (member.TypeCode)
                {
                    case 0:
                        OceanBaseDecimal number;
                        if (value is OceanBaseDecimal precise) number = precise;
                        else if (value is decimal || value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
                            number = new OceanBaseDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                        else throw new ArgumentException("A NUMBER member requires an exact numeric value.");
                        DecimalCodec.Validate(number, OceanBaseCompatibilityMode.Oracle); WriteText(writer, DecimalCodec.Format(number)); break;
                    case 253:
                    case 254:
                        if (!(value is string text)) throw new ArgumentException("A character member requires a string.");
                        WriteText(writer, text, clientEncoding); break;
                    case 207:
                    case 208:
                        if (!(value is string nationalText)) throw new ArgumentException("A national character member requires a string.");
                        WriteText(writer, nationalText, validateOnly ? ResultColumn.Utf8 :
                            (national ?? throw new NotSupportedException("The complex parameter has no trusted connection character set.")).ComplexParameterEncoding); break;
                    case 203:
                        if (!(value is byte[] bytes)) throw new ArgumentException("A RAW member requires bytes.");
                        ReserveBytes(writer, bytes.Length); PreparedParameters.WriteBytes(writer, bytes); break;
                    case 12:
                        if (!(value is DateTime date)) throw new ArgumentException("A DATE member requires an unzoned DateTime.");
                        Reserve(writer, 8); TemporalCodec.WriteDate(writer, 12, date, 0); break;
                    case 202:
                        if (!(value is OceanBaseTimestamp timestamp)) throw new ArgumentException("A TIMESTAMP member requires an exact timestamp.");
                        Reserve(writer, 13); TemporalCodec.WriteTimestamp(writer, timestamp, null); break;
                    default: throw new NotSupportedException("The complex scalar type is not supported.");
                }
            }
        }

        private static void WriteText(BinaryWriter writer, string text, Encoding? encoding = null)
        { encoding ??= ResultColumn.Utf8; ReserveBytes(writer, encoding.GetByteCount(text)); PreparedParameters.WriteBytes(writer, encoding.GetBytes(text)); }
        private static void ReserveBytes(BinaryWriter writer, int count)
            => Reserve(writer, (long)count + (count < 251 ? 1 : count <= ushort.MaxValue ? 3 : count <= 0xFFFFFF ? 4 : 9));
        private static void Reserve(BinaryWriter writer, long count)
        { if (count > MaximumBytes - writer.BaseStream.Position) throw new ArgumentException("Complex parameter data exceeds the packet size limit."); }
    }
}
