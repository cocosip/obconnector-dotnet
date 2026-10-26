using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class SingleByteResultEncoding : Encoding
    {
        // OceanBase v4.3.5_CE: ob_ctype_extra.cc and ob_ctype_tis620_tab.h; FFFF marks undefined slots.
        internal static readonly Encoding Dec8 = new SingleByteResultEncoding(
            "\u00a0\u00a1\u00a2\u00a3\uffff\u00a5\uffff\u00a7\u00a4\u00a9\u00aa\u00ab\uffff\uffff\uffff\uffff" +
            "\u00b0\u00b1\u00b2\u00b3\uffff\u00b5\u00b6\u00b7\uffff\u00b9\u00ba\u00bb\u00bc\u00bd\uffff\u00bf" +
            "\u00c0\u00c1\u00c2\u00c3\u00c4\u00c5\u00c6\u00c7\u00c8\u00c9\u00ca\u00cb\u00cc\u00cd\u00ce\u00cf" +
            "\uffff\u00d1\u00d2\u00d3\u00d4\u00d5\u00d6\u0152\u00d8\u00d9\u00da\u00db\u00dc\u0178\uffff\u00df" +
            "\u00e0\u00e1\u00e2\u00e3\u00e4\u00e5\u00e6\u00e7\u00e8\u00e9\u00ea\u00eb\u00ec\u00ed\u00ee\u00ef" +
            "\uffff\u00f1\u00f2\u00f3\u00f4\u00f5\u00f6\u0153\u00f8\u00f9\u00fa\u00fb\u00fc\u00ff\uffff\uffff");
        internal static readonly Encoding Tis620 = new SingleByteResultEncoding(
            "\uffff\u0e01\u0e02\u0e03\u0e04\u0e05\u0e06\u0e07\u0e08\u0e09\u0e0a\u0e0b\u0e0c\u0e0d\u0e0e\u0e0f" +
            "\u0e10\u0e11\u0e12\u0e13\u0e14\u0e15\u0e16\u0e17\u0e18\u0e19\u0e1a\u0e1b\u0e1c\u0e1d\u0e1e\u0e1f" +
            "\u0e20\u0e21\u0e22\u0e23\u0e24\u0e25\u0e26\u0e27\u0e28\u0e29\u0e2a\u0e2b\u0e2c\u0e2d\u0e2e\u0e2f" +
            "\u0e30\u0e31\u0e32\u0e33\u0e34\u0e35\u0e36\u0e37\u0e38\u0e39\u0e3a\uffff\uffff\uffff\uffff\u0e3f" +
            "\u0e40\u0e41\u0e42\u0e43\u0e44\u0e45\u0e46\u0e47\u0e48\u0e49\u0e4a\u0e4b\u0e4c\u0e4d\u0e4e\u0e4f" +
            "\u0e50\u0e51\u0e52\u0e53\u0e54\u0e55\u0e56\u0e57\u0e58\u0e59\u0e5a\u0e5b\uffff\uffff\uffff\uffff");

        private readonly string _highCharacters;
        private SingleByteResultEncoding(string highCharacters)
            : base(0, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) => _highCharacters = highCharacters;
        public override bool IsSingleByte => true;

        private char Decode(byte value)
        {
            var result = value < 0xa0 ? (char)value : _highCharacters[value - 0xa0];
            if (result == '\uffff') throw new DecoderFallbackException("Undefined single-byte result character.");
            return result;
        }
        private byte Encode(char value)
        {
            if (value < 0xa0) return (byte)value;
            var index = value == '\uffff' ? -1 : _highCharacters.IndexOf(value);
            if (index < 0) throw new EncoderFallbackException("Character is not representable in this result encoding.");
            return (byte)(index + 0xa0);
        }
        private static void CheckRange<T>(T[] array, int index, int count, string name)
        {
            if (array == null) throw new ArgumentNullException(name);
            if (index < 0 || count < 0 || index > array.Length - count) throw new ArgumentOutOfRangeException(nameof(index));
        }

        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            CheckRange(bytes, index, count, nameof(bytes));
            for (var i = index; i < index + count; i++) Decode(bytes[i]);
            return count;
        }
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            CheckRange(bytes, byteIndex, byteCount, nameof(bytes)); CheckRange(chars, charIndex, 0, nameof(chars));
            if (byteCount > chars.Length - charIndex) throw new ArgumentException("The character buffer is too small.", nameof(chars));
            for (var i = 0; i < byteCount; i++) chars[charIndex + i] = Decode(bytes[byteIndex + i]);
            return byteCount;
        }
        public override int GetByteCount(char[] chars, int index, int count)
        {
            CheckRange(chars, index, count, nameof(chars));
            for (var i = index; i < index + count; i++) Encode(chars[i]);
            return count;
        }
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            CheckRange(chars, charIndex, charCount, nameof(chars)); CheckRange(bytes, byteIndex, 0, nameof(bytes));
            if (charCount > bytes.Length - byteIndex) throw new ArgumentException("The byte buffer is too small.", nameof(bytes));
            for (var i = 0; i < charCount; i++) bytes[byteIndex + i] = Encode(chars[charIndex + i]);
            return charCount;
        }
        public override int GetMaxByteCount(int charCount) => charCount >= 0 ? charCount : throw new ArgumentOutOfRangeException(nameof(charCount));
        public override int GetMaxCharCount(int byteCount) => byteCount >= 0 ? byteCount : throw new ArgumentOutOfRangeException(nameof(byteCount));
    }
}
