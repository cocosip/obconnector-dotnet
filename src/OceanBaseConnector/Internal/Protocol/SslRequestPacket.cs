using System;
using System.IO;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Builds the SSLRequest short packet sent before the TLS handshake:
    /// capability flags (4, LE), maximum packet size (4, LE), charset id (1)
    /// and 23 zero filler bytes. Sent only after the server's initial
    /// handshake confirmed CLIENT_SSL and only when the connection wants TLS;
    /// no authentication data is included.
    /// </summary>
    internal static class SslRequestPacket
    {
        public const int PacketLength = 32;
        public const byte DefaultCharsetId = 45; // utf8mb4_general_ci
        public const int DefaultMaxPacketSize = 0x1000000; // 16 MiB

        public static byte[] Build(CapabilityFlags capabilities, int maxPacketSize = DefaultMaxPacketSize, byte charsetId = DefaultCharsetId)
        {
            if (maxPacketSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPacketSize), "Maximum packet size must not be negative.");
            }

            var packet = new byte[PacketLength];
            using var writer = new MemoryStream(packet, 0, PacketLength, writable: true);
            WriteUInt32(writer, (uint)capabilities);
            WriteUInt32(writer, (uint)maxPacketSize);
            writer.WriteByte(charsetId);
            // 23 filler bytes are already zero-initialized.
            return packet;
        }

        private static void WriteUInt32(MemoryStream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
