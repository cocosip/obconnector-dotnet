using System;
using System.Collections.Generic;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class UjisEncoding : Encoding
    {
        private const int PlaneSize = 94 * 94;
        private readonly char[] _characters = new char[PlaneSize * 2];
        private readonly Dictionary<char, int> _codes = new Dictionary<char, int>();
        internal UjisEncoding() : base(51932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            var sjis = new SjisEncoding(); var pair = new byte[2]; var decoded = new char[1];
            for (var lead = 0xa1; lead <= 0xf4; lead++)
                for (var trail = 0xa1; trail <= 0xfe; trail++)
                {
                    var row = lead - 0x80; var cell = trail - 0x80;
                    var sjisLead = (row - 0x21) / 2 + 0x81;
                    if (sjisLead > 0x9f) sjisLead += 0x40;
                    var sjisTrail = row % 2 == 0 ? cell + 0x7e : cell + 0x1f + (cell >= 0x60 ? 1 : 0);
                    pair[0] = (byte)sjisLead; pair[1] = (byte)sjisTrail;
                    try { sjis.GetChars(pair, 0, 2, decoded, 0); }
                    catch (DecoderFallbackException) { continue; }
                    Add(0, lead << 8 | trail, decoded[0]);
                }
            for (var i = 0; i < UjisMappings.Jis0212.Length; i++)
            {
                var position = UjisMappings.FirstPosition + i;
                Add(1, (position / 94 + 0xa1) << 8 | position % 94 + 0xa1, UjisMappings.Jis0212[i]);
            }
            // The pinned server table appends 940 PUA positions to each JIS plane.
            for (var plane = 0; plane < 2; plane++)
                for (var lead = 0xf5; lead <= 0xfe; lead++)
                    for (var trail = 0xa1; trail <= 0xfe; trail++)
                        Add(plane, lead << 8 | trail, (char)(0xe000 + plane * 940 + (lead - 0xf5) * 94 + trail - 0xa1));

            void Add(int plane, int code, char character)
            {
                if (character == 0) return;
                _characters[plane * PlaneSize + Position(code >> 8, code & 255)] = character;
                _codes.Add(character, code | (plane == 0 ? 0 : 0x8f0000));
            }
        }
        private static int Position(int lead, int trail) => (lead - 0xa1) * 94 + trail - 0xa1;

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
                if (pending == 0x8f)
                {
                    if (value < 0xa1 || value > 0xfe) throw new DecoderFallbackException("Invalid UJIS triple byte sequence.");
                    pending = 0x8f00 | value; continue;
                }
                if (pending == 0x8e)
                {
                    if (value < 0xa1 || value > 0xdf) throw new DecoderFallbackException("Invalid UJIS halfwidth byte sequence.");
                    character = (char)(0xfec0 + value);
                }
                else if (pending >= 0)
                {
                    if (value < 0xa1 || value > 0xfe) throw new DecoderFallbackException("Invalid UJIS trail byte.");
                    character = _characters[(pending >= 256 ? PlaneSize : 0) + Position(pending & 255, value)];
                    if (character == 0) throw new DecoderFallbackException("Undefined UJIS result byte sequence.");
                }
                else if (value < 128) character = (char)value;
                else if (value == 0x8e || value == 0x8f || value >= 0xa1 && value <= 0xfe)
                { pending = value; continue; }
                else throw new DecoderFallbackException("Invalid UJIS result byte sequence.");
                pending = -1;
                if (chars != null) chars[charIndex + written] = character;
                written++;
            }
            if (flush && pending >= 0) throw new DecoderFallbackException("Incomplete UJIS result byte sequence.");
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
        private int Encode(char value)
        {
            if (value < 128) return value;
            if (value >= 0xff61 && value <= 0xff9f) return 0x8e00 | value - 0xfec0;
            if (_codes.TryGetValue(value, out var code)) return code;
            throw new EncoderFallbackException("Character is not representable in UJIS results.");
        }
        public override int GetByteCount(char[] chars, int index, int count)
        {
            CheckRange(chars, index, count, nameof(chars)); var length = 0;
            for (var i = index; i < index + count; i++)
            {
                var code = Encode(chars[i]); length = checked(length + (code < 256 ? 1 : code < 65536 ? 2 : 3));
            }
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
                if (code >= 65536) bytes[offset++] = (byte)(code >> 16);
                if (code >= 256) bytes[offset++] = (byte)(code >> 8);
                bytes[offset++] = (byte)code;
            }
            return length;
        }
        public override int GetMaxByteCount(int charCount) => charCount >= 0 && charCount <= int.MaxValue / 3
            ? charCount * 3 : throw new ArgumentOutOfRangeException(nameof(charCount));
        public override int GetMaxCharCount(int byteCount) => byteCount >= 0 ? byteCount : throw new ArgumentOutOfRangeException(nameof(byteCount));
        public override Decoder GetDecoder() => new UjisDecoder(this);

        private sealed class UjisDecoder : Decoder
        {
            private readonly UjisEncoding _encoding;
            private int _pending = -1;
            internal UjisDecoder(UjisEncoding encoding) => _encoding = encoding;
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
