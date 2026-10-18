using System;
using System.Security.Cryptography;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// mysql_native_password scramble:
    /// token = SHA1(seed || SHA1(SHA1(password))) XOR SHA1(password).
    /// An empty password produces an empty token (no bytes).
    /// </summary>
    internal static class NativePassword
    {
        public static byte[] EncryptPassword(string password, byte[] seed)
        {
            if (password is null)
            {
                throw new ArgumentNullException(nameof(password));
            }

            if (seed is null)
            {
                throw new ArgumentNullException(nameof(seed));
            }

            if (password.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var passwordBytes = Encoding.UTF8.GetBytes(password);
            using var sha1 = SHA1.Create();
            var stage1 = sha1.ComputeHash(passwordBytes);
            var stage2 = sha1.ComputeHash(stage1);

            var seedAndStage2 = new byte[seed.Length + stage2.Length];
            Buffer.BlockCopy(seed, 0, seedAndStage2, 0, seed.Length);
            Buffer.BlockCopy(stage2, 0, seedAndStage2, seed.Length, stage2.Length);
            var stage3 = sha1.ComputeHash(seedAndStage2);

            var token = new byte[stage1.Length];
            for (var i = 0; i < token.Length; i++)
            {
                token[i] = (byte)(stage3[i] ^ stage1[i]);
            }

            return token;
        }
    }
}
