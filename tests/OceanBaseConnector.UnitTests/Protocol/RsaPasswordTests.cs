using System;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class RsaPasswordTests
    {
        [Theory]
        [InlineData("empty")]
        [InlineData("private")]
        [InlineData("ec")]
        [InlineData("oversized")]
        [InlineData("truncated")]
        [InlineData("trailing")]
        [InlineData("indefinite")]
        [InlineData("overlong")]
        [InlineData("nonminimal")]
        [InlineData("weak")]
        [InlineData("large")]
        [InlineData("negative")]
        [InlineData("zero")]
        [InlineData("even-exponent")]
        [InlineData("small-exponent")]
        [InlineData("large-exponent")]
        [InlineData("parameters")]
        [InlineData("unused-bits")]
        [InlineData("extra-field")]
        public void RsaRejectsMalformedOrUnsupportedKeyWithoutEcho(string kind)
        {
            using var rsa = RSA.Create(2048);
            var der = rsa.ExportSubjectPublicKeyInfo();
            var pem = rsa.ExportSubjectPublicKeyInfoPem();
            switch (kind)
            {
                case "empty": pem = "hidden-key"; break;
                case "private": pem = rsa.ExportPkcs8PrivateKeyPem(); break;
                case "ec": using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)) pem = ec.ExportSubjectPublicKeyInfoPem(); break;
                case "oversized": pem = new string('x', 16385); break;
                case "truncated": pem = Pem(der.AsSpan(0, der.Length - 1).ToArray()); break;
                case "trailing": pem = Pem(Append(der, 0)); break;
                case "indefinite": der[1] = 128; pem = Pem(der); break;
                case "overlong": pem = Pem(new byte[] { 48, 131, 0, 1, 0 }); break;
                case "nonminimal": pem = Pem(new byte[] { 48, 130, 0, 127 }); break;
                case "weak": using (var weak = RSA.Create(1024)) pem = weak.ExportSubjectPublicKeyInfoPem(); break;
                default: pem = Pem(Spki(rsa.ExportParameters(false), kind)); break;
            }
            var error = Assert.Throws<OceanBaseException>(() => RsaPassword.Encrypt(Encoding.UTF8.GetBytes("secretpw\0"), new byte[20], pem));
            Assert.DoesNotContain("secretpw", error.ToString()); Assert.DoesNotContain("hidden-key", error.ToString());
            Assert.DoesNotContain("BEGIN", error.ToString()); Assert.Null(error.InnerException);
        }

        [Fact]
        public void RsaAcceptsRsaAlgorithmWithoutNullParameters()
        {
            using var rsa = RSA.Create(2048);
            var cipher = RsaPassword.Encrypt(new byte[] { 112, 119, 0 }, new byte[20], Pem(Spki(rsa.ExportParameters(false), "absent")));
            Assert.Equal(new byte[] { 112, 119, 0 }, rsa.Decrypt(cipher, RSAEncryptionPadding.OaepSHA1));
        }

        private static byte[] Spki(RSAParameters parameters, string kind)
        {
            var key = new AsnWriter(AsnEncodingRules.DER);
            using (key.PushSequence())
            {
                if (kind == "negative") key.WriteInteger(-1);
                else if (kind == "zero") key.WriteInteger(0);
                else if (kind == "large") { var n = new byte[1025]; n[0] = 128; key.WriteIntegerUnsigned(n); }
                else key.WriteIntegerUnsigned(parameters.Modulus!);
                if (kind == "even-exponent") key.WriteInteger(2);
                else if (kind == "small-exponent") key.WriteInteger(1);
                else if (kind == "large-exponent") key.WriteIntegerUnsigned(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1 });
                else key.WriteInteger(kind == "unused-bits" ? 65536 : 65537);
                if (kind == "extra-field") key.WriteInteger(3);
            }
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("1.2.840.113549.1.1.1");
                    if (kind == "parameters") writer.WriteInteger(1);
                    else if (kind != "absent") writer.WriteNull();
                }
                writer.WriteBitString(key.Encode(), unusedBitCount: kind == "unused-bits" ? 1 : 0);
            }
            return writer.Encode();
        }

        private static string Pem(byte[] der) => "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(der) + "\n-----END PUBLIC KEY-----";
        private static byte[] Append(byte[] bytes, byte value)
        {
            var result = new byte[bytes.Length + 1]; bytes.CopyTo(result, 0); result[bytes.Length] = value; return result;
        }
    }
}
