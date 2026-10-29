using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    // Windows code pages admit single-byte extensions that are not OceanBase GBK/GB18030.
    internal sealed class ChineseEncoding : Encoding
    {
        private readonly Encoding _inner;
        private readonly bool _fourBytes;
        private readonly bool _revision2022;
        private readonly bool _gb2312;
        internal ChineseEncoding(int codePage, bool revision2022 = false, bool gb2312 = false) : base(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            if (revision2022 && codePage != 54936) throw new ArgumentException("The 2022 revision requires GB18030.", nameof(codePage));
            if (gb2312 && codePage != 20936) throw new ArgumentException("The GB2312 mapping requires code page 20936.", nameof(codePage));
            _inner = CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException("The required character encoding is unavailable.");
            _fourBytes = codePage == 54936;
            _revision2022 = revision2022;
            _gb2312 = gb2312;
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
                        if (_gb2312 ? value >= 0xa1 && value <= 0xf7 : value >= 0x81 && value <= 0xfe)
                        { state = 1; continue; }
                        break;
                    case 1:
                        if (_gb2312 ? value >= 0xa1 && value <= 0xfe : value >= 0x40 && value <= 0xfe && value != 0x7f)
                        { state = 0; continue; }
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
            {
                if (chars[i] == '\u1e3f') chars[i] = '\ue7c7';
                else if (chars[i] == '\ue7c7') chars[i] = '\u1e3f';
                // OceanBase v4.3.5_CE init_gb18030_2022 swaps both scalar assignments in18 pairs.
                else if (_revision2022) chars[i] = chars[i] switch
                {
                    '\ue81e' => '\u9fb4', '\u9fb4' => '\ue81e',
                    '\ue826' => '\u9fb5', '\u9fb5' => '\ue826',
                    '\ue82b' => '\u9fb6', '\u9fb6' => '\ue82b',
                    '\ue82c' => '\u9fb7', '\u9fb7' => '\ue82c',
                    '\ue832' => '\u9fb8', '\u9fb8' => '\ue832',
                    '\ue843' => '\u9fb9', '\u9fb9' => '\ue843',
                    '\ue854' => '\u9fba', '\u9fba' => '\ue854',
                    '\ue864' => '\u9fbb', '\u9fbb' => '\ue864',
                    '\ue78d' => '\ufe10', '\ufe10' => '\ue78d',
                    '\ue78e' => '\ufe12', '\ufe12' => '\ue78e',
                    '\ue78f' => '\ufe11', '\ufe11' => '\ue78f',
                    '\ue790' => '\ufe13', '\ufe13' => '\ue790',
                    '\ue791' => '\ufe14', '\ufe14' => '\ue791',
                    '\ue792' => '\ufe15', '\ufe15' => '\ue792',
                    '\ue793' => '\ufe16', '\ufe16' => '\ue793',
                    '\ue794' => '\ufe17', '\ufe17' => '\ue794',
                    '\ue795' => '\ufe18', '\ufe18' => '\ue795',
                    '\ue796' => '\ufe19', '\ufe19' => '\ue796',
                    _ => chars[i],
                };
            }
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
