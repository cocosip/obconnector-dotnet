using System;
using System.IO;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    // Client SQL needs literal ASCII syntax bytes, even when a result encoder has other aliases.
    internal sealed class ClientCharacterSet : Encoding
    {
        private readonly Encoding _inner;
        internal string Name { get; }
        internal bool Managed => Name != "utf8mb4";
        private ClientCharacterSet(string name, ushort collation)
        { Name = name; _inner = ResultCharacterSet.Resolve(collation, out _); }

        internal static ClientCharacterSet Parse(string name)
        {
            name = name.Trim().ToLowerInvariant();
            if (name == "utf8") name = "utf8mb3";
            ushort collation = name switch
            {
                "utf8mb4" => 45,
                "utf8mb3" => 33,
                "ascii" => 11,
                "latin1" => 8,
                "gbk" => 28,
                "gb18030" => 248,
                "gb18030_2022" => 216,
                "dec8" => 3,
                "tis620" => 18,
                "sjis" => 13,
                "big5" => 1,
                "hkscs" => 152,
                "hkscs31" => 153,
                _ => throw new ArgumentException("Unsupported client Character Set.", nameof(name)),
            };
            return new ClientCharacterSet(name, collation);
        }

        internal bool Matches(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase) ||
            Name == "utf8mb3" && (string.Equals(name, "utf8", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "utf8mb4", StringComparison.OrdinalIgnoreCase));

        internal bool MatchesCollation(string name)
        {
            foreach (var c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false;
            return name.Length <= 128 && (name.StartsWith(Name + "_", StringComparison.OrdinalIgnoreCase) ||
                Name == "utf8mb3" && (name.StartsWith("utf8_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("utf8mb4_", StringComparison.OrdinalIgnoreCase)));
        }

        private byte[] Encode(string text)
        {
            if (Name == "utf8mb3")
                foreach (var c in text)
                    if (char.IsSurrogate(c)) throw new EncoderFallbackException("Supplementary characters cannot be sent through UTF8MB3.");
            using var stream = new MemoryStream();
            for (var i = 0; i < text.Length;)
            {
                if (text[i] < 128) { stream.WriteByte((byte)text[i++]); continue; }
                var start = i++;
                while (i < text.Length && text[i] >= 128) i++;
                var bytes = _inner.GetBytes(text.Substring(start, i - start));
                stream.Write(bytes, 0, bytes.Length);
            }
            var encoded = stream.ToArray();
            try
            {
                if (!string.Equals(text, _inner.GetString(encoded), StringComparison.Ordinal))
                    throw new EncoderFallbackException("Client characters do not round trip through the server character set.");
            }
            catch (DecoderFallbackException error)
            { throw new EncoderFallbackException("Client characters are outside the server character set.", error); }
            return encoded;
        }

        public override int GetByteCount(string s) => Encode(s).Length;
        public override byte[] GetBytes(string s) => Encode(s);
        public override int GetByteCount(char[] chars, int index, int count) => Encode(new string(chars, index, count)).Length;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var encoded = Encode(new string(chars, charIndex, charCount));
            if (byteIndex < 0 || byteIndex > bytes.Length) throw new ArgumentOutOfRangeException(nameof(byteIndex));
            if (encoded.Length > bytes.Length - byteIndex) throw new ArgumentException("The byte buffer is too small.", nameof(bytes));
            Buffer.BlockCopy(encoded, 0, bytes, byteIndex, encoded.Length); return encoded.Length;
        }
        public override int GetCharCount(byte[] bytes, int index, int count) => _inner.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override int GetMaxByteCount(int charCount) => _inner.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => _inner.GetMaxCharCount(byteCount);
        public override Decoder GetDecoder() => _inner.GetDecoder();
    }
}
