using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    // Windows code pages admit single-byte extensions that are not OceanBase GBK/GB18030.
    internal sealed class ChineseEncoding : Encoding
    {
        private readonly Encoding _inner;
        private readonly bool _fourBytes;
        internal ChineseEncoding(int codePage) : base(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            _inner = CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException("The required character encoding is unavailable.");
            _fourBytes = codePage == 54936;
        }

        private int Validate(byte[] bytes, int index, int count, int state, bool flush)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (index < 0 || count < 0 || index > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            for (var i = index; i < index + count; i++)
            {
                var value = bytes[i];
                switch (state)
                {
                    case 0:
                        if (value < 0x80) continue;
                        if (value >= 0x81 && value <= 0xfe) { state = 1; continue; }
                        break;
                    case 1:
                        if (value >= 0x40 && value <= 0xfe && value != 0x7f) { state = 0; continue; }
                        if (_fourBytes && value >= 0x30 && value <= 0x39) { state = 2; continue; }
                        break;
                    case 2:
                        if (value >= 0x81 && value <= 0xfe) { state = 3; continue; }
                        break;
                    case 3:
                        if (value >= 0x30 && value <= 0x39) { state = 0; continue; }
                        break;
                }
                throw new DecoderFallbackException("Invalid Chinese character byte sequence.");
            }
            if (flush && state != 0) throw new DecoderFallbackException("Incomplete Chinese character byte sequence.");
            return state;
        }

        public override int GetCharCount(byte[] bytes, int index, int count)
        { Validate(bytes, index, count, 0, true); return _inner.GetCharCount(bytes, index, count); }
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            Validate(bytes, byteIndex, byteCount, 0, true);
            var count = _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            SwapCharacters(chars, charIndex, count);
            return count;
        }
        // OceanBase's GB18030-2005 exchanges these two BMP mappings relative to Windows CP54936.
        private void SwapCharacters(char[] chars, int index, int count)
        {
            if (!_fourBytes) return;
            for (var i = index; i < index + count; i++)
                if (chars[i] == '\u1e3f') chars[i] = '\ue7c7';
                else if (chars[i] == '\ue7c7') chars[i] = '\u1e3f';
        }
        public override int GetByteCount(char[] chars, int index, int count)
        {
            if (!_fourBytes) return _inner.GetByteCount(chars, index, count);
            var copy = new string(chars, index, count).ToCharArray();
            SwapCharacters(copy, 0, copy.Length);
            return _inner.GetByteCount(copy, 0, copy.Length);
        }
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            if (!_fourBytes) return _inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            var copy = new string(chars, charIndex, charCount).ToCharArray();
            SwapCharacters(copy, 0, copy.Length);
            return _inner.GetBytes(copy, 0, copy.Length, bytes, byteIndex);
        }
        public override int GetMaxByteCount(int charCount) => _inner.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => _inner.GetMaxCharCount(byteCount);
        public override Decoder GetDecoder() => new StrictDecoder(this);

        private sealed class StrictDecoder : Decoder
        {
            private readonly ChineseEncoding _encoding;
            private readonly Decoder _inner;
            private int _state;
            internal StrictDecoder(ChineseEncoding encoding) { _encoding = encoding; _inner = encoding._inner.GetDecoder(); }
            public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, false);
            public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
            { _encoding.Validate(bytes, index, count, _state, flush); return _inner.GetCharCount(bytes, index, count, flush); }
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
                => GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            {
                var state = _encoding.Validate(bytes, byteIndex, byteCount, _state, flush);
                var count = _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex, flush);
                _encoding.SwapCharacters(chars, charIndex, count);
                _state = state;
                return count;
            }
            public override void Reset() { _inner.Reset(); _state = 0; base.Reset(); }
        }
    }
}
