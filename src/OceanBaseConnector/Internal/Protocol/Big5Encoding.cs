using System;
using System.Collections.Generic;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class Big5Encoding : Encoding
    {
        // Numeric assignments for C6A1–C7FC from OceanBase v4.3.5_CE; Windows950 maps these to PUA.
        private const string ExtendedCharacters =
            "\u30fe\u309d\u309e\u3005\u3041\u3042\u3043\u3044\u3045\u3046\u3047\u3048\u3049\u304a\u304b\u304c" +
            "\u304d\u304e\u304f\u3050\u3051\u3052\u3053\u3054\u3055\u3056\u3057\u3058\u3059\u305a\u305b\u305c" +
            "\u305d\u305e\u305f\u3060\u3061\u3062\u3063\u3064\u3065\u3066\u3067\u3068\u3069\u306a\u306b\u306c" +
            "\u306d\u306e\u306f\u3070\u3071\u3072\u3073\u3074\u3075\u3076\u3077\u3078\u3079\u307a\u307b\u307c" +
            "\u307d\u307e\u307f\u3080\u3081\u3082\u3083\u3084\u3085\u3086\u3087\u3088\u3089\u308a\u308b\u308c" +
            "\u308d\u308e\u308f\u3090\u3091\u3092\u3093\u30a1\u30a2\u30a3\u30a4\u30a5\u30a6\u30a7\u30a8\u30a9" +
            "\u30aa\u30ab\u30ac\u30ad\u30ae\u30af\u30b0\u30b1\u30b2\u30b3\u30b4\u30b5\u30b6\u30b7\u30b8\u30b9" +
            "\u30ba\u30bb\u30bc\u30bd\u30be\u30bf\u30c0\u30c1\u30c2\u30c3\u30c4\u30c5\u30c6\u30c7\u30c8\u30c9" +
            "\u30ca\u30cb\u30cc\u30cd\u30ce\u30cf\u30d0\u30d1\u30d2\u30d3\u30d4\u30d5\u30d6\u30d7\u30d8\u30d9" +
            "\u30da\u30db\u30dc\u30dd\u30de\u30df\u30e0\u30e1\u30e2\u30e3\u30e4\u30e5\u30e6\u30e7\u30e8\u30e9" +
            "\u30ea\u30eb\u30ec\u30ed\u30ee\u30ef\u30f0\u30f1\u30f2\u30f3\u30f4\u30f5\u30f6\u0414\u0415\u0401" +
            "\u0416\u0417\u0418\u0419\u041a\u041b\u041c\u0423\u0424\u0425\u0426\u0427\u0428\u0429\u042a\u042b" +
            "\u042c\u042d\u042e\u042f\u0430\u0431\u0432\u0433\u0434\u0435\u0451\u0436\u0437\u0438\u0439\u043a" +
            "\u043b\u043c\u043d\u043e\u043f\u0440\u0441\u0442\u0443\u0444\u0445\u0446\u0447\u0448\u0449\u044a" +
            "\u044b\u044c\u044d\u044e\u044f\u2460\u2461\u2462\u2463\u2464\u2465\u2466\u2467\u2468\u2469\u2474" +
            "\u2475\u2476\u2477\u2478\u2479\u247a\u247b\u247c\u247d";

        private readonly char[] _characters = new char[0xf9dc - 0xa140 + 1];
        private readonly Dictionary<char, ushort> _codes = new Dictionary<char, ushort>();
        internal Big5Encoding() : base(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            var windows = CodePagesEncodingProvider.Instance.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException("The required character encoding is unavailable.");
            var pair = new byte[2]; var decoded = new char[2];
            for (var code = 0xa140; code <= 0xf9dc; code++)
            {
                var trail = code & 255;
                if (code > 0xc7fc && code < 0xc940 || trail < 0x40 || trail > 0xfe || trail > 0x7e && trail < 0xa1 ||
                    code >= 0xa3c0 && code <= 0xa3fe) continue;
                // The server's seven FFFD placeholders are undefined under the strict result policy.
                if (code == 0xa15a || code == 0xa1c3 || code == 0xa1c5 || code == 0xa1fe ||
                    code == 0xa240 || code == 0xa2cc || code == 0xa2ce) continue;
                var character = code switch
                {
                    0xa145 => '\u2022', 0xa14e => '\uff64', 0xa1c2 => '\u203e', 0xa1e3 => '\u223c',
                    0xa1f2 => '\u2641', 0xa1f3 => '\u2609', 0xa241 => '\uff0f', 0xa242 => '\uff3c',
                    0xa244 => '\u00a5', 0xa246 => '\u00a2', 0xa247 => '\u00a3',
                    0xa2a4 => '\u2550', 0xa2a5 => '\u255e', 0xa2a6 => '\u256a', 0xa2a7 => '\u2561',
                    _ => '\0',
                };
                if (code >= 0xc6a1 && code <= 0xc7fc)
                    character = ExtendedCharacters[code <= 0xc6fe ? code - 0xc6a1 : 94 + (trail <= 0x7e ? trail - 0x40 : 63 + trail - 0xa1)];
                if (character == 0)
                {
                    pair[0] = (byte)(code >> 8); pair[1] = (byte)trail;
                    if (windows.GetChars(pair, 0, 2, decoded, 0) != 1)
                        throw new NotSupportedException("The required Big5 mapping is unavailable.");
                    character = decoded[0];
                }
                _characters[code - 0xa140] = character; _codes.Add(character, (ushort)code);
            }
        }

        private static void CheckRange<T>(T[] array, int index, int count, string name)
        {
            if (array == null) throw new ArgumentNullException(name);
            if (index < 0 || count < 0 || index > array.Length - count) throw new ArgumentOutOfRangeException(nameof(index));
        }
        private int Scan(byte[] bytes, int index, int count, char[]? chars, int charIndex, int pending, bool flush, out int next)
        {
            CheckRange(bytes, index, count, nameof(bytes)); var written = 0;
            for (var i = index; i < index + count; i++)
            {
                var value = bytes[i]; char character;
                if (pending >= 0)
                {
                    var position = (pending << 8 | value) - 0xa140;
                    character = position >= 0 && position < _characters.Length ? _characters[position] : '\0';
                    if (character == 0) throw new DecoderFallbackException("Undefined Big5 result byte sequence.");
                    pending = -1;
                }
                else if (value < 128) character = (char)value;
                else if (value >= 0xa1 && value <= 0xc7 || value >= 0xc9 && value <= 0xf9)
                { pending = value; continue; }
                else throw new DecoderFallbackException("Invalid Big5 result byte sequence.");
                if (chars != null) chars[charIndex + written] = character;
                written++;
            }
            if (flush && pending >= 0) throw new DecoderFallbackException("Incomplete Big5 result byte sequence.");
            next = pending; return written;
        }
        private int ReadChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, int pending, bool flush, out int next)
        {
            var count = Scan(bytes, byteIndex, byteCount, null, 0, pending, flush, out _);
            CheckRange(chars, charIndex, 0, nameof(chars));
            if (count > chars.Length - charIndex) throw new ArgumentException("The character buffer is too small.", nameof(chars));
            return Scan(bytes, byteIndex, byteCount, chars, charIndex, pending, flush, out next);
        }
        public override int GetCharCount(byte[] bytes, int index, int count) => Scan(bytes, index, count, null, 0, -1, true, out _);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => ReadChars(bytes, byteIndex, byteCount, chars, charIndex, -1, true, out _);
        private ushort Encode(char value)
        {
            if (value < 128) return value;
            if (_codes.TryGetValue(value, out var code)) return code;
            throw new EncoderFallbackException("Character is not representable in Big5 results.");
        }
        public override int GetByteCount(char[] chars, int index, int count)
        {
            CheckRange(chars, index, count, nameof(chars)); var length = 0;
            for (var i = index; i < index + count; i++) length = checked(length + (Encode(chars[i]) < 256 ? 1 : 2));
            return length;
        }
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            var length = GetByteCount(chars, charIndex, charCount); CheckRange(bytes, byteIndex, 0, nameof(bytes));
            if (length > bytes.Length - byteIndex) throw new ArgumentException("The byte buffer is too small.", nameof(bytes));
            var offset = byteIndex;
            for (var i = charIndex; i < charIndex + charCount; i++)
            {
                var code = Encode(chars[i]);
                if (code >= 256) bytes[offset++] = (byte)(code >> 8);
                bytes[offset++] = (byte)code;
            }
            return length;
        }
        public override int GetMaxByteCount(int charCount) => charCount >= 0 && charCount <= int.MaxValue / 2
            ? charCount * 2 : throw new ArgumentOutOfRangeException(nameof(charCount));
        public override int GetMaxCharCount(int byteCount) => byteCount >= 0 ? byteCount : throw new ArgumentOutOfRangeException(nameof(byteCount));
        public override Decoder GetDecoder() => new Big5Decoder(this);

        private sealed class Big5Decoder : Decoder
        {
            private readonly Big5Encoding _encoding;
            private int _pending = -1;
            internal Big5Decoder(Big5Encoding encoding) => _encoding = encoding;
            public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, false);
            public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
                => _encoding.Scan(bytes, index, count, null, 0, _pending, flush, out _);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
                => GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            {
                var count = _encoding.ReadChars(bytes, byteIndex, byteCount, chars, charIndex, _pending, flush, out var pending);
                _pending = pending; return count;
            }
            public override void Reset() { _pending = -1; base.Reset(); }
        }
    }
}
