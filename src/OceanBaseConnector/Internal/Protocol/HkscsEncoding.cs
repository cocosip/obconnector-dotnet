using System;
using System.Collections.Generic;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class HkscsEncoding : Encoding
    {
        private readonly int[] _characters = new int[65536];
        private readonly Dictionary<int, ushort> _codes = new Dictionary<int, ushort>();
        private readonly bool _revised;

        internal HkscsEncoding(bool revised) : base(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            _revised = revised;
            var windows = CodePagesEncodingProvider.Instance.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException("The required character encoding is unavailable.");
            var pair = new byte[2]; var decoded = new char[2];
            for (var lead = 0x81; lead <= 0xfe; lead++)
            {
                for (var trail = 0x40; trail <= 0xfe; trail++)
                {
                    if (trail > 0x7e && trail < 0xa1) continue;
                    pair[0] = (byte)lead; pair[1] = (byte)trail;
                    try
                    {
                        if (windows.GetChars(pair, 0, 2, decoded, 0) != 1)
                            throw new NotSupportedException("The required HKSCS baseline mapping is unavailable.");
                        _characters[lead << 8 | trail] = decoded[0];
                    }
                    // CP950 rejects some duplicate wire codes restored by the pinned corrections below.
                    catch (DecoderFallbackException) { }
                }
            }
            foreach (var code in HkscsMappings.UndefinedCodes) _characters[code] = 0;
            for (var i = 0; i < HkscsMappings.BmpCorrections.Length; i += 2)
                _characters[HkscsMappings.BmpCorrections[i]] = HkscsMappings.BmpCorrections[i + 1];
            if (revised)
                for (var i = 0; i < HkscsMappings.Plane2Corrections.Length; i += 2)
                    _characters[HkscsMappings.Plane2Corrections[i]] = 0x20000 + HkscsMappings.Plane2Corrections[i + 1];
            for (var code = 256; code < _characters.Length; code++)
            {
                var scalar = _characters[code];
                if (scalar != 0 && !_codes.ContainsKey(scalar)) _codes.Add(scalar, (ushort)code);
            }
            foreach (var code in HkscsMappings.PreferredCodes) _codes[_characters[code]] = code;
            // The source encodes U+2593 as F9FE, whose decoded scalar is U+FFED.
            _codes[0x2593] = 0xf9fe;
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
                var value = bytes[i]; int scalar;
                if (pending >= 0)
                {
                    scalar = _characters[pending << 8 | value];
                    if (scalar == 0) throw new DecoderFallbackException("Undefined HKSCS result byte sequence.");
                    pending = -1;
                }
                else if (value < 128) scalar = value;
                else if (value >= 0x81 && value <= 0xfe) { pending = value; continue; }
                else throw new DecoderFallbackException("Invalid HKSCS result byte sequence.");
                if (scalar <= 65535)
                {
                    if (chars != null) chars[charIndex + written] = (char)scalar;
                    written++;
                }
                else
                {
                    if (chars != null)
                    {
                        chars[charIndex + written] = (char)(0xd800 + ((scalar - 0x10000) >> 10));
                        chars[charIndex + written + 1] = (char)(0xdc00 + ((scalar - 0x10000) & 1023));
                    }
                    written += 2;
                }
            }
            if (flush && pending >= 0) throw new DecoderFallbackException("Incomplete HKSCS result byte sequence.");
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

        private ushort Encode(char[] chars, ref int index, int end)
        {
            int scalar = chars[index];
            if (scalar < 128) return (ushort)scalar;
            if (char.IsHighSurrogate(chars[index]) && index + 1 < end && char.IsLowSurrogate(chars[index + 1]))
                scalar = char.ConvertToUtf32(chars[index], chars[++index]);
            if (_codes.TryGetValue(scalar, out var code)) return code;
            throw new EncoderFallbackException("Character is not representable in HKSCS results.");
        }
        public override int GetByteCount(char[] chars, int index, int count)
        {
            CheckRange(chars, index, count, nameof(chars)); var length = 0; var end = index + count;
            for (var i = index; i < end; i++) length = checked(length + (Encode(chars, ref i, end) < 256 ? 1 : 2));
            return length;
        }
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            var length = GetByteCount(chars, charIndex, charCount); CheckRange(bytes, byteIndex, 0, nameof(bytes));
            if (length > bytes.Length - byteIndex) throw new ArgumentException("The byte buffer is too small.", nameof(bytes));
            var offset = byteIndex; var end = charIndex + charCount;
            for (var i = charIndex; i < end; i++)
            {
                var code = Encode(chars, ref i, end);
                if (code >= 256) bytes[offset++] = (byte)(code >> 8);
                bytes[offset++] = (byte)code;
            }
            return length;
        }
        public override int GetMaxByteCount(int charCount) => charCount >= 0 && charCount <= int.MaxValue / 2
            ? charCount * 2 : throw new ArgumentOutOfRangeException(nameof(charCount));
        public override int GetMaxCharCount(int byteCount) => byteCount >= 0 && (!_revised || byteCount < int.MaxValue)
            ? byteCount + (_revised ? 1 : 0) : throw new ArgumentOutOfRangeException(nameof(byteCount));
        public override Decoder GetDecoder() => new HkscsDecoder(this);

        private sealed class HkscsDecoder : Decoder
        {
            private readonly HkscsEncoding _encoding;
            private int _pending = -1;
            internal HkscsDecoder(HkscsEncoding encoding) => _encoding = encoding;
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
