using System;
using System.Collections.Generic;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class Cp932Encoding : Encoding
    {
        private readonly char[] _characters = new char[0xfcfc - 0x8140 + 1];
        private readonly Dictionary<char, ushort> _codes = new Dictionary<char, ushort>();
        internal Cp932Encoding() : base(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        {
            var windows = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException("The required character encoding is unavailable.");
            var pair = new byte[2]; var decoded = new char[2];
            for (var code = 0x8140; code <= 0xfcfc; code++)
            {
                var lead = code >> 8; var trail = code & 255;
                if (!(lead >= 0x81 && lead <= 0x9f || lead >= 0xe0 && lead <= 0xfc) ||
                    trail < 0x40 || trail > 0xfc || trail == 0x7f) continue;
                pair[0] = (byte)lead; pair[1] = (byte)trail;
                try
                {
                    if (windows.GetChars(pair, 0, 2, decoded, 0) != 1)
                        throw new NotSupportedException("The required CP932 mapping is unavailable.");
                }
                catch (DecoderFallbackException) { continue; }
                _characters[code - 0x8140] = decoded[0]; _codes.Add(decoded[0], (ushort)code);
            }
            // MySQL 8.0.36 ctype-cp932.cc defines these 398 duplicates; strict .NET932 rejects them.
            foreach (var (start, characters) in new[]
            {
                (0x8790, "\u2252\u2261\u222b"),
                (0x8795, "\u221a\u22a5\u2220"),
                (0x879a, "\u2235\u2229\u222a"),
                (0xed40, "\u7e8a\u891c\u9348\u9288\u84dc\u4fc9\u70bb\u6631\u68c8\u92f9\u66fb\u5f45\u4e28\u4ee1\u4efc\u4f00" +
                    "\u4f03\u4f39\u4f56\u4f92\u4f8a\u4f9a\u4f94\u4fcd\u5040\u5022\u4fff\u501e\u5046\u5070\u5042\u5094" +
                    "\u50f4\u50d8\u514a\u5164\u519d\u51be\u51ec\u5215\u529c\u52a6\u52c0\u52db\u5300\u5307\u5324\u5372" +
                    "\u5393\u53b2\u53dd\ufa0e\u549c\u548a\u54a9\u54ff\u5586\u5759\u5765\u57ac\u57c8\u57c7\ufa0f"),
                (0xed80, "\ufa10\u589e\u58b2\u590b\u5953\u595b\u595d\u5963\u59a4\u59ba\u5b56\u5bc0\u752f\u5bd8\u5bec\u5c1e" +
                    "\u5ca6\u5cba\u5cf5\u5d27\u5d53\ufa11\u5d42\u5d6d\u5db8\u5db9\u5dd0\u5f21\u5f34\u5f67\u5fb7\u5fde" +
                    "\u605d\u6085\u608a\u60de\u60d5\u6120\u60f2\u6111\u6137\u6130\u6198\u6213\u62a6\u63f5\u6460\u649d" +
                    "\u64ce\u654e\u6600\u6615\u663b\u6609\u662e\u661e\u6624\u6665\u6657\u6659\ufa12\u6673\u6699\u66a0" +
                    "\u66b2\u66bf\u66fa\u670e\uf929\u6766\u67bb\u6852\u67c0\u6801\u6844\u68cf\ufa13\u6968\ufa14\u6998" +
                    "\u69e2\u6a30\u6a6b\u6a46\u6a73\u6a7e\u6ae2\u6ae4\u6bd6\u6c3f\u6c5c\u6c86\u6c6f\u6cda\u6d04\u6d87" +
                    "\u6d6f\u6d96\u6dac\u6dcf\u6df8\u6df2\u6dfc\u6e39\u6e5c\u6e27\u6e3c\u6ebf\u6f88\u6fb5\u6ff5\u7005" +
                    "\u7007\u7028\u7085\u70ab\u710f\u7104\u715c\u7146\u7147\ufa15\u71c1\u71fe\u72b1"),
                (0xee40, "\u72be\u7324\ufa16\u7377\u73bd\u73c9\u73d6\u73e3\u73d2\u7407\u73f5\u7426\u742a\u7429\u742e\u7462" +
                    "\u7489\u749f\u7501\u756f\u7682\u769c\u769e\u769b\u76a6\ufa17\u7746\u52af\u7821\u784e\u7864\u787a" +
                    "\u7930\ufa18\ufa19\ufa1a\u7994\ufa1b\u799b\u7ad1\u7ae7\ufa1c\u7aeb\u7b9e\ufa1d\u7d48\u7d5c\u7db7" +
                    "\u7da0\u7dd6\u7e52\u7f47\u7fa1\ufa1e\u8301\u8362\u837f\u83c7\u83f6\u8448\u84b4\u8553\u8559"),
                (0xee80, "\u856b\ufa1f\u85b0\ufa20\ufa21\u8807\u88f5\u8a12\u8a37\u8a79\u8aa7\u8abe\u8adf\ufa22\u8af6\u8b53" +
                    "\u8b7f\u8cf0\u8cf4\u8d12\u8d76\ufa23\u8ecf\ufa24\ufa25\u9067\u90de\ufa26\u9115\u9127\u91da\u91d7" +
                    "\u91de\u91ed\u91ee\u91e4\u91e5\u9206\u9210\u920a\u923a\u9240\u923c\u924e\u9259\u9251\u9239\u9267" +
                    "\u92a7\u9277\u9278\u92e7\u92d7\u92d9\u92d0\ufa27\u92d5\u92e0\u92d3\u9325\u9321\u92fb\ufa28\u931e" +
                    "\u92ff\u931d\u9302\u9370\u9357\u93a4\u93c6\u93de\u93f8\u9431\u9445\u9448\u9592\uf9dc\ufa29\u969d" +
                    "\u96af\u9733\u973b\u9743\u974d\u974f\u9751\u9755\u9857\u9865\ufa2a\ufa2b\u9927\ufa2c\u999e\u9a4e" +
                    "\u9ad9\u9adc\u9b75\u9b72\u9b8f\u9bb1\u9bbb\u9c00\u9d70\u9d6b\ufa2d\u9e19\u9ed1"),
                (0xeeef, "\u2170\u2171\u2172\u2173\u2174\u2175\u2176\u2177\u2178\u2179\uffe2\uffe4\uff07\uff02"),
                (0xfa4a, "\u2160\u2161\u2162\u2163\u2164\u2165\u2166\u2167\u2168\u2169\uffe2"),
                (0xfa58, "\u3231\u2116\u2121\u2235"),
            })
                for (var i = 0; i < characters.Length; i++) _characters[start + i - 0x8140] = characters[i];
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
                    var position = (pending << 8 | value) - 0x8140;
                    character = position >= 0 && position < _characters.Length ? _characters[position] : '\0';
                    if (character == 0) throw new DecoderFallbackException("Undefined CP932 result byte sequence.");
                    pending = -1;
                }
                else if (value < 128) character = (char)value;
                else if (value >= 0xa1 && value <= 0xdf) character = (char)(0xff61 + value - 0xa1);
                else if (value >= 0x81 && value <= 0x9f || value >= 0xe0 && value <= 0xfc)
                { pending = value; continue; }
                else throw new DecoderFallbackException("Invalid CP932 result byte sequence.");
                if (chars != null) chars[charIndex + written] = character;
                written++;
            }
            if (flush && pending >= 0) throw new DecoderFallbackException("Incomplete CP932 result byte sequence.");
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
            if (value >= 0xff61 && value <= 0xff9f) return (ushort)(0xa1 + value - 0xff61);
            if (_codes.TryGetValue(value, out var code)) return code;
            throw new EncoderFallbackException("Character is not representable in CP932 results.");
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
        public override Decoder GetDecoder() => new Cp932Decoder(this);

        private sealed class Cp932Decoder : Decoder
        {
            private readonly Cp932Encoding _encoding;
            private int _pending = -1;
            internal Cp932Decoder(Cp932Encoding encoding) => _encoding = encoding;
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
