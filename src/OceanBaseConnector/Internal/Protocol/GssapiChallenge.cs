using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class GssapiChallenge
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static string Parse(byte[] seed, string configuredPrincipal)
        {
            if (seed.Length > 16384) throw new ProtocolException("GSSAPI challenge exceeds 16 KiB.");
            var cursor = new ByteCursor(seed);
            string principal, mechanism;
            try
            {
                principal = StrictUtf8.GetString(cursor.ReadNullTerminatedBytes());
                mechanism = StrictUtf8.GetString(cursor.ReadNullTerminatedBytes());
            }
            catch (DecoderFallbackException) { throw new ProtocolException("GSSAPI challenge must use valid UTF8."); }
            while (cursor.HasRemaining)
                if (cursor.ReadUInt8() != 0) throw new ProtocolException("Unexpected GSSAPI challenge data.");
            if (mechanism.Length != 0 && !string.Equals(mechanism, "Kerberos", StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("Only the Kerberos GSSAPI mechanism is supported.");
            if (configuredPrincipal.Length != 0) principal = configuredPrincipal;
            if (!IsValidPrincipal(principal)) throw new ProtocolException("GSSAPI requires a valid service principal name.");
            return principal;
        }

        internal static bool IsValidPrincipal(string principal)
        {
            if (string.IsNullOrWhiteSpace(principal) || principal.Length > 1024) return false;
            foreach (var character in principal)
                if (char.IsControl(character)) return false;
            try { _ = StrictUtf8.GetByteCount(principal); return true; }
            catch (EncoderFallbackException) { return false; }
        }
    }
}
