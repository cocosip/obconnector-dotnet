using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Authentication switch request (0xFE): NUL-terminated plugin name
    /// followed by the new auth seed for the remainder of the packet. The
    /// single-byte legacy old-password switch is handled by the conversation
    /// using the original handshake challenge, before this parser is called.
    /// </summary>
    internal sealed class AuthSwitchRequest
    {
        private AuthSwitchRequest(string pluginName, byte[] seed)
        {
            PluginName = pluginName;
            Seed = seed;
        }

        public string PluginName { get; }

        public byte[] Seed { get; }

        public static bool IsAuthSwitch(byte[] payload) => payload.Length > 0 && payload[0] == 0xFE;

        public static AuthSwitchRequest Parse(byte[] payload)
        {
            var cursor = new ByteCursor(payload);
            if (cursor.ReadUInt8() != 0xFE) throw new ProtocolException("Not an authentication switch.");
            var pluginName = cursor.ReadNullTerminatedAscii();
            var seed = cursor.HasRemaining ? cursor.ReadRemaining() : Array.Empty<byte>();
            if (seed.Length > 0 && seed[seed.Length - 1] == 0) Array.Resize(ref seed, seed.Length - 1);
            return new AuthSwitchRequest(pluginName, seed);
        }
    }
}
