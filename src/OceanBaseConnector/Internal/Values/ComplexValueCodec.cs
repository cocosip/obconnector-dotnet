using System;
using System.Globalization;
using System.IO;
using System.Text;
using OceanBaseConnector;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector.Internal.Values
{
    internal static class ComplexValueCodec
    {
        internal const int MaximumMembers = 65535;
        private const int MaximumDepth = 32;
        private const long MaximumBytes = PacketChannel.DefaultMaxPacketSize - 10;

        internal static object?[] DecodeResult(ByteCursor cursor, ComplexType type, OceanBaseCompatibilityMode mode, Encoding? nationalEncoding = null, LobSession? lobs = null, OracleCursorOwner? cursors = null)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers;
            return Read(cursor, type, 0, ref budget, cursor.Remaining, nationalEncoding: nationalEncoding, lobs: lobs, cursors: cursors);
        }
        internal static byte[] ReadResultField(ByteCursor cursor, ComplexType type, OceanBaseCompatibilityMode mode)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers; var position = cursor.Position;
            Read(cursor, type, 0, ref budget, cursor.Remaining, materialize: false);
            return cursor.CopyConsumed(position);
        }

        internal static void ValidateTextResult(OceanBaseComplexType type, OceanBaseCompatibilityMode mode)
        {
            if (mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("Complex values require Oracle mode.");
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.IsAnonymous || type.IsCollection || type.TypeName.Length == 0)
                throw new NotSupportedException("Complex text results require a named object type.");
            foreach (var member in type.Layout.Members)
                if (member.TypeCode != 0 && member.TypeCode != 253 && member.TypeCode != 254 && member.TypeCode != 207 && member.TypeCode != 208)
                    throw new NotSupportedException("The complex text result contains an unsupported member type.");
        }

        internal static object?[] DecodeTextResult(byte[] value, OceanBaseComplexType type, OceanBaseCompatibilityMode mode)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            ValidateTextResult(type, mode);
            if (value.Length > MaximumBytes) throw new ProtocolException("Complex text data exceeds the supported byte limit.");
            return new TextResultParser(ResultColumn.Utf8.GetString(value), type).Read();
        }

        internal static byte[] EncodeParameter(ComplexType type, object?[] values, OceanBaseCompatibilityMode mode, NationalCharacterSet? national = null, bool validateOnly = false, Encoding? clientEncoding = null, bool lobLocators = false)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, ResultColumn.Utf8, leaveOpen: true);
            WriteParameter(writer, type, values, mode, national, validateOnly, clientEncoding, lobLocators); return stream.ToArray();
        }

        internal static void WriteParameter(BinaryWriter writer, ComplexType type, object?[] values, OceanBaseCompatibilityMode mode, NationalCharacterSet? national = null, bool validateOnly = false, Encoding? clientEncoding = null, bool lobLocators = false)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers;
            Write(writer, type, values, 0, ref budget, national, validateOnly, clientEncoding ?? ResultColumn.Utf8, lobLocators);
        }

        private static void ValidateRoot(ComplexType type, OceanBaseCompatibilityMode mode)
        {
            if (mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("Complex values require Oracle mode.");
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.TypeCode != 160) throw new ArgumentException("A complex value requires an object or collection layout.", nameof(type));
        }

        private sealed class TextResultParser
        {
            private readonly string _text;
            private readonly OceanBaseComplexType _type;
            private int _position;

            internal TextResultParser(string text, OceanBaseComplexType type)
            { _text = text; _type = type; }

            internal object?[] Read()
            {
                SkipWhitespace();
                var name = ReadTypeName();
                if (!string.Equals(name, _type.TypeName, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("The complex text result type name does not match its metadata.");
                SkipWhitespace(); Expect('(');
                var values = new object?[_type.Layout.Members.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    SkipWhitespace(); values[i] = ReadMember(_type.Layout.Members[i]); SkipWhitespace();
                    if (i + 1 < values.Length) Expect(',');
                }
                SkipWhitespace(); Expect(')'); SkipWhitespace();
                if (_position != _text.Length) throw Invalid("Trailing complex text result data.");
                return values;
            }

            private string ReadTypeName()
            {
                var start = _position;
                while (_position < _text.Length && !char.IsWhiteSpace(_text[_position]) && _text[_position] != '(') _position++;
                if (start == _position) throw Invalid("A complex text result is missing its type name.");
                return _text.Substring(start, _position - start);
            }

            private object? ReadMember(ComplexType member)
            {
                if (ReadNull()) return null;
                switch (member.TypeCode)
                {
                    case 0:
                        var numberText = ReadBareToken();
                        try
                        {
                            var number = DecimalCodec.Parse(numberText);
                            DecimalCodec.Validate(number, OceanBaseCompatibilityMode.Oracle);
                            return number;
                        }
                        catch (Exception error) when (error is FormatException || error is OverflowException)
                        { throw new ProtocolException("Invalid complex text NUMBER member.", error); }
                    case 253:
                    case 254:
                    case 207:
                    case 208:
                        return ReadQuoted();
                    default: throw new NotSupportedException("The complex text result contains an unsupported member type.");
                }
            }

            private bool ReadNull()
            {
                const string token = "NULL";
                if (_position + token.Length > _text.Length || !string.Equals(_text.Substring(_position, token.Length), token, StringComparison.OrdinalIgnoreCase)) return false;
                var end = _position + token.Length;
                if (end < _text.Length && !char.IsWhiteSpace(_text[end]) && _text[end] != ',' && _text[end] != ')') return false;
                _position = end; return true;
            }

            private string ReadBareToken()
            {
                var start = _position;
                while (_position < _text.Length && _text[_position] != ',' && _text[_position] != ')') _position++;
                var token = _text.Substring(start, _position - start).Trim();
                if (token.Length == 0) throw Invalid("A complex text result is missing a scalar member.");
                return token;
            }

            private string ReadQuoted()
            {
                Expect('\''); var builder = new StringBuilder();
                while (_position < _text.Length)
                {
                    var character = _text[_position++];
                    if (character != '\'') { builder.Append(character); continue; }
                    if (IsClosingQuote()) return builder.ToString();
                    if (_position < _text.Length && _text[_position] == '\'')
                    {
                        var next = _position + 1;
                        while (next < _text.Length && char.IsWhiteSpace(_text[next])) next++;
                        if (next >= _text.Length || _text[next] != ',' && _text[next] != ')') _position++;
                        builder.Append('\'');
                        continue;
                    }
                    builder.Append('\'');
                }
                throw Invalid("An unterminated complex text character member was received.");
            }

            private bool IsClosingQuote()
            {
                var next = _position;
                while (next < _text.Length && char.IsWhiteSpace(_text[next])) next++;
                return next >= _text.Length || _text[next] == ',' || _text[next] == ')';
            }

            private void Expect(char expected)
            {
                if (_position >= _text.Length || _text[_position] != expected) throw Invalid("Invalid complex text result delimiters.");
                _position++;
            }

            private void SkipWhitespace()
            { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; }

            private ProtocolException Invalid(string message) => new ProtocolException(message);
        }

        private static object?[] Read(ByteCursor cursor, ComplexType type, int depth, ref int budget, int start, bool materialize = true, Encoding? nationalEncoding = null, LobSession? lobs = null, OracleCursorOwner? cursors = null)
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
                    var nested = Read(cursor, member, depth + 1, ref budget, start, materialize, nationalEncoding, lobs, cursors);
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
                        case 163:
                            if (cursors == null) throw new NotSupportedException("Complex cursor members require their originating execution.");
                            if (bytes.Length != 4) throw new ProtocolException("A cursor identifier requires four bytes.");
                            values[i] = cursors.Publish(new ByteCursor(bytes).ReadUInt32()); break;
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

        private static void Write(BinaryWriter writer, ComplexType type, object?[] values, int depth, ref int budget, NationalCharacterSet? national, bool validateOnly, Encoding clientEncoding, bool lobLocators = false)
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
                    case 210:
                    case 211:
                        if (lobLocators)
                        {
                            if (!(value is OceanBaseLob lob) || lob.IsCharacter != (member.TypeCode == 211))
                                throw new ArgumentException("An anonymous LOB array requires matching locator values.");
                            var locator = lob.BindingBytes(clientEncoding);
                            ReserveBytes(writer, locator.Length); PreparedParameters.WriteBytes(writer, locator);
                        }
                        else
                        {
                            // Named PL members use LONG_BLOB payloads, not the anonymous array locator protocol.
                            if (value is OceanBaseLob) throw new NotSupportedException("Named complex LOB members require BLOB bytes or CLOB text, not locators.");
                            if (member.TypeCode == 210 && value is byte[] payload)
                            { ReserveBytes(writer, payload.Length); PreparedParameters.WriteBytes(writer, payload); }
                            // LONG_BLOB payloads bypass the server's client charset conversion.
                            else if (member.TypeCode == 211 && value is string clob) WriteText(writer, clob);
                            else throw new ArgumentException("A named BLOB member requires bytes and a CLOB member requires text.");
                        }
                        break;
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
