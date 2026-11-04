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
        internal static void DisposeLobValues(object? value)
        {
            if (value is OceanBaseLob lob) lob.Dispose();
            else if (value is OceanBaseComplexValue complex) complex.DisposeLobValues();
            else if (value is object?[] values)
                foreach (var member in values) DisposeLobValues(member);
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
            ValidateTextObject(type, 0);
        }

        private static void ValidateTextObject(OceanBaseComplexType type, int depth)
        {
            if (depth >= MaximumDepth) throw new NotSupportedException("Complex text result nesting exceeds the supported depth.");
            if (type.IsAnonymous || type.IsCollection || type.TypeName.Length == 0)
                throw new NotSupportedException("Complex text results require named object types.");
            foreach (var member in type.Members)
            {
                if (member.Layout.TypeCode == 160)
                {
                    if (member.ComplexType == null) throw new NotSupportedException("The nested complex text descriptor is unavailable.");
                    ValidateTextObject(member.ComplexType, depth + 1);
                }
                else if (member.Layout.TypeCode == 204 || member.Layout.TypeCode == 205)
                    IntervalCodec.GetTextPrecisions(member.TypeName, member.Layout.TypeCode == 205);
                else if (member.Layout.TypeCode != 0 && member.Layout.TypeCode != 253 && member.Layout.TypeCode != 254 &&
                    member.Layout.TypeCode != 207 && member.Layout.TypeCode != 208)
                    throw new NotSupportedException("The complex text result contains an unsupported member type.");
            }
        }

        internal static object?[] DecodeTextResult(byte[] value, OceanBaseComplexType type, OceanBaseCompatibilityMode mode)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            ValidateTextResult(type, mode);
            if (value.Length > MaximumBytes) throw new ProtocolException("Complex text data exceeds the supported byte limit.");
            return new TextResultParser(ResultColumn.Utf8.GetString(value), type).Read();
        }

        internal static byte[] EncodeParameter(ComplexType type, object?[] values, OceanBaseCompatibilityMode mode, NationalCharacterSet? national = null, bool validateOnly = false, Encoding? clientEncoding = null, bool lobLocators = false, bool allowNamedLobLocators = false)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, ResultColumn.Utf8, leaveOpen: true);
            WriteParameter(writer, type, values, mode, national, validateOnly, clientEncoding, lobLocators, allowNamedLobLocators); return stream.ToArray();
        }

        internal static void WriteParameter(BinaryWriter writer, ComplexType type, object?[] values, OceanBaseCompatibilityMode mode, NationalCharacterSet? national = null, bool validateOnly = false, Encoding? clientEncoding = null, bool lobLocators = false, bool allowNamedLobLocators = false)
        {
            ValidateRoot(type, mode); var budget = MaximumMembers;
            Write(writer, type, values, 0, ref budget, national, validateOnly, clientEncoding ?? ResultColumn.Utf8, lobLocators, allowNamedLobLocators);
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
            private int _memberBudget = MaximumMembers;
            private int _position;

            internal TextResultParser(string text, OceanBaseComplexType type)
            { _text = text; _type = type; }

            internal object?[] Read()
            {
                var values = ReadObject(_type, 0);
                SkipWhitespace();
                if (_position != _text.Length) throw Invalid("Trailing complex text result data.");
                return values;
            }

            private object?[] ReadObject(OceanBaseComplexType type, int depth, bool nameRead = false)
            {
                if (depth >= MaximumDepth) throw Invalid("Complex text result nesting exceeds the supported depth.");
                if (type.IsCollection || type.IsAnonymous || type.TypeName.Length == 0)
                    throw new NotSupportedException("Complex text results require named object types.");
                if (!nameRead && !TryReadTypeName(type.TypeName))
                    throw Invalid("The complex text result type name does not match its metadata.");
                SkipWhitespace(); Expect('(');
                var members = type.Members.Count;
                if (members > _memberBudget) throw Invalid("Complex text result member count exceeds the supported limit.");
                _memberBudget -= members;
                var values = new object?[members];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = ReadMember(type.Members[i], depth + 1); SkipWhitespace();
                    if (i + 1 < values.Length) Expect(',');
                }
                SkipWhitespace(); Expect(')');
                return values;
            }

            private bool TryReadTypeName(string name)
            {
                // Quoted SQL type names arrive unquoted and may themselves contain spaces or parentheses.
                var position = _position;
                while (position < _text.Length)
                {
                    if (name.Length <= _text.Length - position &&
                        string.Compare(_text, position, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        var next = position + name.Length;
                        while (next < _text.Length && char.IsWhiteSpace(_text[next])) next++;
                        if (next < _text.Length && _text[next] == '(')
                        { _position = position + name.Length; return true; }
                        return false;
                    }
                    if (!char.IsWhiteSpace(_text[position])) break;
                    position++;
                }
                return false;
            }

            private object? ReadMember(OceanBaseComplexMember member, int depth)
            {
                if (member.Layout.TypeCode == 160 && member.ComplexType != null && TryReadTypeName(member.ComplexType.TypeName))
                    return ReadObject(member.ComplexType, depth, nameRead: true);
                SkipWhitespace();
                if (ReadNull()) return null;
                switch (member.Layout.TypeCode)
                {
                    case 160:
                        if (member.ComplexType == null) throw new NotSupportedException("The nested complex text descriptor is unavailable.");
                        throw Invalid("The complex text result type name does not match its metadata.");
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
                    case 204:
                    case 205:
                        return IntervalCodec.DecodeText(ReadQuoted(), member.Layout.TypeCode == 205, member.TypeName);
                    default: throw new NotSupportedException("The complex text result contains an unsupported member type.");
                }
            }

            private bool ReadNull()
            {
                const string token = "NULL";
                if (_position + token.Length > _text.Length || !string.Equals(_text.Substring(_position, token.Length), token, StringComparison.OrdinalIgnoreCase)) return false;
                var end = _position + token.Length;
                var next = end;
                while (next < _text.Length && char.IsWhiteSpace(_text[next])) next++;
                if (next < _text.Length && _text[next] != ',' && _text[next] != ')') return false;
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
            try
            {
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
                        if (member.TypeCode == 204 && length != 7 || member.TypeCode == 205 && length != 14)
                            throw new ProtocolException("Invalid complex INTERVAL length.");
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
                            case 204:
                            case 205:
                                // Complex members carry precision in the payload, without separate column metadata.
                                values[i] = IntervalCodec.Decode(bytes, member.TypeCode == 205,
                                    member.TypeCode == 204 ? bytes[6] : bytes[12] * 10 + bytes[13]); break;
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
            catch { DisposeLobValues(values); throw; }
        }

        private static byte[] ReadBytes(ByteCursor cursor, int count, int start)
        {
            if (count > MaximumBytes - (start - cursor.Remaining)) throw new ProtocolException("Complex data exceeds the supported byte limit.");
            return cursor.ReadBytes(count);
        }

        private static void Write(BinaryWriter writer, ComplexType type, object?[] values, int depth, ref int budget, NationalCharacterSet? national, bool validateOnly, Encoding clientEncoding, bool lobLocators = false, bool allowNamedLobLocators = false)
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
                    Write(writer, member, nested, depth + 1, ref budget, national, validateOnly, clientEncoding, allowNamedLobLocators: allowNamedLobLocators); continue;
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
                            // Named PL members use LONG_BLOB payloads at execution; validation may retain a locator for SQL constructor rewriting.
                            if (value is OceanBaseLob lob)
                            {
                                if (!allowNamedLobLocators || lob.IsCharacter != (member.TypeCode == 211))
                                    throw new NotSupportedException("Named complex LOB members require BLOB bytes or CLOB text, not locators.");
                                if (validateOnly) break;
                                throw new NotSupportedException("Named complex LOB locators require constructor binding.");
                            }
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
                    case 204:
                    case 205:
                        Reserve(writer, member.TypeCode == 204 ? 8 : 15);
                        IntervalCodec.Write(writer, value, member.TypeCode == 205, 0, null); break;
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
