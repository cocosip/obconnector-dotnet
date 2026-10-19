using System;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class OldPassword
    {
        internal const string PluginName = "mysql_old_password";

        // The 3.23 plugin hashes UTF16 password characters, as in the fixed Java baseline.
        internal static byte[] Respond(string password, byte[] seed)
        {
            if (seed.Length < 8) throw new ProtocolException("Old password authentication requires an 8-byte challenge.");
            var challenge = new char[8];
            for (var i = 0; i < challenge.Length; i++)
            {
                if (seed[i] > 127) throw new ProtocolException("Old password authentication requires an ASCII challenge.");
                challenge[i] = (char)seed[i];
            }
            if (password.Length == 0) return Array.Empty<byte>();
            var passwordHash = Hash(password);
            var seedHash = Hash(new string(challenge));
            const long modulus = 0x3FFFFFFF;
            var first = (passwordHash.Item1 ^ seedHash.Item1) % modulus;
            var second = (passwordHash.Item2 ^ seedHash.Item2) % modulus;
            var response = new byte[9];
            byte Next()
            {
                first = (first * 3 + second) % modulus;
                second = (first + second + 33) % modulus;
                return (byte)(first * 31 / modulus);
            }
            for (var i = 0; i < 8; i++) response[i] = (byte)(Next() + 64);
            var mask = Next();
            for (var i = 0; i < 8; i++) response[i] ^= mask;
            return response;
        }

        private static Tuple<long, long> Hash(string text)
        {
            unchecked
            {
                long first = 1345345333, second = 0x12345671, add = 7;
                foreach (var character in text)
                {
                    if (character == ' ' || character == '\t') continue;
                    first ^= ((first & 63) + add) * character + (first << 8);
                    second += (second << 8) ^ first;
                    add += character;
                }
                return Tuple.Create(first & 0x7FFFFFFF, second & 0x7FFFFFFF);
            }
        }
    }
}
