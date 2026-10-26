using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class SjisEncoding : Encoding
    {
        private readonly Encoding _inner = CodePagesEncodingProvider.Instance.GetEncoding(932,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? throw new NotSupportedException("The required character encoding is unavailable.");
        internal SjisEncoding() : base(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) { }

        private static bool Validate(byte[] bytes, int index, int count, bool pending, bool flush)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (index < 0 || count < 0 || index > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            for (var i = index; i < index + count; i++)
            {
                var value = bytes[i];
                if (pending)
                {
                    if (value >= 0x40 && value <= 0xfc && value != 0x7f) { pending = false; continue; }
                }
                else
                {
                    if (value < 0x80 || value >= 0xa1 && value <= 0xdf) continue;
                    // CP932's NEC/IBM/private-use extensions are absent from the pinned SJIS table.
                    if (value >= 0x81 && value <= 0x9f && value != 0x87 || value >= 0xe0 && value <= 0xea)
                    { pending = true; continue; }
                }
                throw new DecoderFallbackException("Invalid SJIS result byte sequence.");
            }
            if (flush && pending) throw new DecoderFallbackException("Incomplete SJIS result byte sequence.");
            return pending;
        }

        // OceanBase v4.3.5_CE ob_ctype_sjis.cc differs from CP932 at these seven positions.
        private static void MapCharacters(char[] chars, int index, int count, bool encoding)
        {
            for (var i = index; i < index + count; i++)
                chars[i] = encoding ? chars[i] switch
                {
                    '\u005c' => '\uff3c', '\u301c' => '\uff5e', '\u2016' => '\u2225',
                    '\u2212' => '\uff0d', '\u00a2' => '\uffe0', '\u00a3' => '\uffe1', '\u00ac' => '\uffe2',
                    _ => chars[i],
                } : chars[i] switch
                {
                    '\uff3c' => '\u005c', '\uff5e' => '\u301c', '\u2225' => '\u2016',
                    '\uff0d' => '\u2212', '\uffe0' => '\u00a2', '\uffe1' => '\u00a3', '\uffe2' => '\u00ac',
                    _ => chars[i],
                };
        }
        public override int GetCharCount(byte[] bytes, int index, int count)
        { Validate(bytes, index, count, false, true); return _inner.GetCharCount(bytes, index, count); }
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            Validate(bytes, byteIndex, byteCount, false, true);
            var count = _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            MapCharacters(chars, charIndex, count, false); return count;
        }

        private byte[] Encode(char[] chars, int index, int count)
        {
            if (chars == null) throw new ArgumentNullException(nameof(chars));
            if (index < 0 || count < 0 || index > chars.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            var original = new string(chars, index, count); var copy = original.ToCharArray();
            MapCharacters(copy, 0, copy.Length, true); var bytes = _inner.GetBytes(copy);
            try
            {
                // Reject Windows-only mappings and aliases instead of silently changing a character.
                if (!string.Equals(original, GetString(bytes), StringComparison.Ordinal))
                    throw new EncoderFallbackException("Character is not representable in SJIS results.");
            }
            catch (DecoderFallbackException error)
            { throw new EncoderFallbackException("Character is not representable in SJIS results.", error); }
            return bytes;
        }
        public override int GetByteCount(char[] chars, int index, int count) => Encode(chars, index, count).Length;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (byteIndex < 0 || byteIndex > bytes.Length) throw new ArgumentOutOfRangeException(nameof(byteIndex));
            var encoded = Encode(chars, charIndex, charCount);
            if (encoded.Length > bytes.Length - byteIndex) throw new ArgumentException("The byte buffer is too small.", nameof(bytes));
            Buffer.BlockCopy(encoded, 0, bytes, byteIndex, encoded.Length); return encoded.Length;
        }
        public override int GetMaxByteCount(int charCount) => _inner.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => _inner.GetMaxCharCount(byteCount);
        public override Decoder GetDecoder() => new SjisDecoder(this);

        private sealed class SjisDecoder : Decoder
        {
            private readonly Decoder _inner;
            private bool _pending;
            internal SjisDecoder(SjisEncoding encoding) => _inner = encoding._inner.GetDecoder();
            public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, false);
            public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
            { Validate(bytes, index, count, _pending, flush); return _inner.GetCharCount(bytes, index, count, flush); }
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
                => GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            {
                var pending = Validate(bytes, byteIndex, byteCount, _pending, flush);
                var count = _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex, flush);
                MapCharacters(chars, charIndex, count, false); _pending = pending; return count;
            }
            public override void Reset() { _inner.Reset(); _pending = false; base.Reset(); }
        }
    }
}
