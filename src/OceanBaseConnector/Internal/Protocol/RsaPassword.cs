using System;
using System.Security.Cryptography;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class RsaPassword
    {
        private const string Header = "-----BEGIN PUBLIC KEY-----";
        private const string Footer = "-----END PUBLIC KEY-----";
        private static readonly byte[] RsaOid = { 42, 134, 72, 134, 247, 13, 1, 1, 1 };

        internal static byte[] Encrypt(byte[] terminatedPassword, byte[] seed, string pem)
        {
            if (seed.Length != 20) throw new ProtocolException("RSA authentication requires a 20-byte challenge.");
            try
            {
                if (pem.Length > 16384) throw new ProtocolException("RSA public key exceeds 16 KiB.");
                pem = pem.Trim();
                if (!pem.StartsWith(Header, StringComparison.Ordinal) || !pem.EndsWith(Footer, StringComparison.Ordinal))
                    throw new ProtocolException("Expected an RSA SubjectPublicKeyInfo PUBLIC KEY PEM.");
                var document = new ByteCursor(Convert.FromBase64String(pem.Substring(Header.Length, pem.Length - Header.Length - Footer.Length)));
                var sequence = Element(document, 0x30); End(document);
                var algorithm = Element(sequence, 0x30);
                var oid = Element(algorithm, 6).ReadRemaining();
                if (oid.Length != RsaOid.Length) throw new ProtocolException("Public key is not RSA.");
                for (var i = 0; i < oid.Length; i++)
                    if (oid[i] != RsaOid[i]) throw new ProtocolException("Public key is not RSA.");
                if (algorithm.HasRemaining) End(Element(algorithm, 5));
                End(algorithm);
                var bits = Element(sequence, 3); End(sequence);
                if (bits.ReadUInt8() != 0) throw new ProtocolException("Invalid RSA public key bit string.");
                var key = Element(bits, 0x30); End(bits);
                var modulus = Integer(key); var exponent = Integer(key); End(key);
                if (modulus.Length < 256 || modulus.Length > 1024 || (modulus.Length == 256 && modulus[0] < 128) ||
                    exponent.Length > 8 || (exponent[exponent.Length - 1] & 1) == 0 || (exponent.Length == 1 && exponent[0] < 3))
                    throw new ProtocolException("RSA authentication requires a 2048–8192 bit key and an odd exponent of 3 or greater, at most 64 bits.");
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                if (terminatedPassword.Length > modulus.Length - 42)
                    throw new ProtocolException("Password exceeds the RSA OAEP-SHA1 payload limit.");
                var masked = (byte[])terminatedPassword.Clone();
                try
                {
                    for (var i = 0; i < masked.Length; i++) masked[i] ^= seed[i % seed.Length];
                    return rsa.Encrypt(masked, RSAEncryptionPadding.OaepSHA1);
                }
                finally { Array.Clear(masked, 0, masked.Length); }
            }
            catch (Exception error) when (error is FormatException || error is CryptographicException || error is ProtocolException || error is ArgumentException)
            {
                // Provider and parser messages can include supplied key material; keep them out of public errors.
                throw new OceanBaseException("RSA password authentication failed; check the public key format, key size and password length.");
            }
        }

        private static ByteCursor Element(ByteCursor source, byte tag)
        {
            if (source.ReadUInt8() != tag) throw new ProtocolException("Invalid RSA DER tag.");
            var first = source.ReadUInt8();
            var length = (int)first;
            if ((first & 128) != 0)
            {
                var count = first & 127;
                if (count == 0 || count > 2) throw new ProtocolException("Invalid RSA DER length.");
                length = source.ReadUInt8();
                if (length == 0) throw new ProtocolException("Non-minimal RSA DER length.");
                if (count == 2) length = (length << 8) | source.ReadUInt8();
                if (length < 128 || (count == 2 && length < 256)) throw new ProtocolException("Non-minimal RSA DER length.");
            }
            return new ByteCursor(source.ReadBytes(length));
        }

        private static byte[] Integer(ByteCursor source)
        {
            var value = Element(source, 2).ReadRemaining();
            if (value.Length == 0 || value[0] >= 128) throw new ProtocolException("RSA DER integers must be positive.");
            if (value[0] != 0) return value;
            if (value.Length == 1 || value[1] < 128) throw new ProtocolException("Non-minimal RSA DER integer.");
            var unsigned = new byte[value.Length - 1]; Buffer.BlockCopy(value, 1, unsigned, 0, unsigned.Length); return unsigned;
        }

        private static void End(ByteCursor source)
        {
            if (source.HasRemaining) throw new ProtocolException("Trailing data in RSA DER key.");
        }
    }
}
