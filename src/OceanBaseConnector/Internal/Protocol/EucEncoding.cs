using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    // OceanBase's EUC tables use explicit byte ranges that Windows code pages do not reject.
    internal sealed class EucEncoding : Encoding
    {
        private readonly Encoding _inner;
        internal EucEncoding() : base(51949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            _inner = CodePagesEncodingProvider.Instance.GetEncoding(51949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException("The required character encoding is unavailable.");
        }

        private int Validate(byte[] bytes, int index, int count, int state, bool flush)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (index < 0 || count < 0 || index > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            for (var i = index; i < index + count; i++)
            {
                var value = bytes[i];
                if (state == 0 && value < 0x80) continue;
                if (state == 0 && value >= 0x81 && value <= 0xfe) { state = 1; continue; }
                if (state == 1 && (value >= 0x41 && value <= 0x5a || value >= 0x61 && value <= 0x7a || value >= 0x81 && value <= 0xfe))
                { state = 0; continue; }
                throw new DecoderFallbackException("Invalid EUC-KR character byte sequence.");
            }
            if (flush && state != 0) throw new DecoderFallbackException("Incomplete EUC-KR character byte sequence.");
            return state;
        }

        private byte[] Encode(char[] chars, int index, int count)
        {
            if (chars == null) throw new ArgumentNullException(nameof(chars));
            if (index < 0 || count < 0 || index > chars.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            var original = new string(chars, index, count);
            var bytes = _inner.GetBytes(chars, index, count);
            try
            {
                Validate(bytes, 0, bytes.Length, 0, true);
                if (!string.Equals(original, _inner.GetString(bytes), StringComparison.Ordinal))
                    throw new EncoderFallbackException("Character is not representable in the OceanBase EUC encoding.");
            }
            catch (DecoderFallbackException error)
            { throw new EncoderFallbackException("Character is not representable in the OceanBase EUC encoding.", error); }
            return bytes;
        }

        public override int GetByteCount(string s) => Encode(s.ToCharArray(), 0, s.Length).Length;
        public override byte[] GetBytes(string s) => Encode(s.ToCharArray(), 0, s.Length);
        public override int GetByteCount(char[] chars, int index, int count) => Encode(chars, index, count).Length;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (byteIndex < 0 || byteIndex > bytes.Length) throw new ArgumentOutOfRangeException(nameof(byteIndex));
            var encoded = Encode(chars, charIndex, charCount);
            if (encoded.Length > bytes.Length - byteIndex) throw new ArgumentException("The byte buffer is too small.", nameof(bytes));
            Buffer.BlockCopy(encoded, 0, bytes, byteIndex, encoded.Length); return encoded.Length;
        }
        public override int GetCharCount(byte[] bytes, int index, int count) { Validate(bytes, index, count, 0, true); return _inner.GetCharCount(bytes, index, count); }
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            Validate(bytes, byteIndex, byteCount, 0, true);
            return _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        }
        public override int GetMaxByteCount(int charCount) => _inner.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => _inner.GetMaxCharCount(byteCount);
        public override Decoder GetDecoder() => new EucDecoder(this);

        private sealed class EucDecoder : Decoder
        {
            private readonly EucEncoding _encoding;
            private readonly Decoder _inner;
            private int _state;
            internal EucDecoder(EucEncoding encoding) { _encoding = encoding; _inner = encoding._inner.GetDecoder(); }
            public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, false);
            public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
            { _encoding.Validate(bytes, index, count, _state, flush); return _inner.GetCharCount(bytes, index, count, flush); }
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
                => GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            {
                var state = _encoding.Validate(bytes, byteIndex, byteCount, _state, flush);
                var count = _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex, flush);
                _state = state; return count;
            }
            public override void Reset() { _inner.Reset(); _state = 0; base.Reset(); }
        }
    }
}
