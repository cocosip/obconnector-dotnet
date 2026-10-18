using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Parsed server initial handshake. Layout per the Java baseline
    /// (ReadInitialHandShakePacket): protocol version, NUL-terminated server
    /// version, connection id, 8 seed bytes + filler, capability lower/upper
    /// halves, charset, status, auth-plugin-data length, 6 reserved bytes, 4
    /// MariaDB extended capability bytes (ignored on OceanBase), the second
    /// seed part and the NUL-terminated plugin name.
    /// </summary>
    internal sealed class InitialHandshakePacket
    {
        public const byte SupportedProtocolVersion = 10;
        public const string NativePasswordPluginName = "mysql_native_password";
        public const string ClearPasswordPluginName = "mysql_clear_password";

        private InitialHandshakePacket(
            byte protocolVersion,
            string serverVersion,
            uint connectionId,
            byte[] seed,
            CapabilityFlags capabilities,
            byte charset,
            ServerStatusFlags statusFlags,
            string? authenticationPluginName,
            bool isOceanBaseServer)
        {
            ProtocolVersion = protocolVersion;
            ServerVersion = serverVersion;
            ConnectionId = connectionId;
            Seed = seed;
            Capabilities = capabilities;
            Charset = charset;
            StatusFlags = statusFlags;
            AuthenticationPluginName = authenticationPluginName;
            IsOceanBaseServer = isOceanBaseServer;
        }

        public byte ProtocolVersion { get; }

        public string ServerVersion { get; }

        public uint ConnectionId { get; }

        /// <summary>Concatenated 20-byte auth seed (or shorter on old servers).</summary>
        public byte[] Seed { get; }

        public CapabilityFlags Capabilities { get; }

        public byte Charset { get; }

        public ServerStatusFlags StatusFlags { get; }

        public string? AuthenticationPluginName { get; }

        /// <summary>
        /// True when the server version string identifies an OceanBase
        /// endpoint. Only then may the Oracle-mode status bit be interpreted
        /// at all; unknown endpoints must be rejected before use.
        /// </summary>
        public bool IsOceanBaseServer { get; }

        public static InitialHandshakePacket Parse(byte[] payload)
        {
            var cursor = new ByteCursor(payload);
            var protocolVersion = cursor.ReadUInt8();
            if (protocolVersion != SupportedProtocolVersion)
            {
                throw new ProtocolException(
                    $"Unsupported protocol version {protocolVersion}; expected {SupportedProtocolVersion}.");
            }

            var serverVersion = cursor.ReadNullTerminatedAscii();
            var connectionId = cursor.ReadUInt32();
            var seed1 = cursor.ReadBytes(8);
            cursor.Skip(1); // filler
            var capabilityLower = cursor.ReadUInt16();
            var charset = cursor.ReadUInt8();
            var statusFlags = (ServerStatusFlags)cursor.ReadUInt16();
            var capabilityUpper = cursor.ReadUInt16();
            var capabilities = (CapabilityFlags)((uint)capabilityLower | ((uint)capabilityUpper << 16));

            var saltLength = 0;
            if ((capabilities & CapabilityFlags.ClientPluginAuth) != 0)
            {
                saltLength = Math.Max(12, cursor.ReadUInt8() - 9);
            }
            else
            {
                cursor.Skip(1);
            }

            cursor.Skip(6); // reserved
            _ = cursor.ReadUInt32(); // MariaDB extended capabilities; OceanBase keeps bit 1 set, ignore.

            byte[] seed2;
            if ((capabilities & CapabilityFlags.ClientSecureConnection) != 0)
            {
                if (saltLength > 0)
                {
                    seed2 = cursor.ReadBytes(saltLength);
                    if (cursor.ReadUInt8() != 0) throw new ProtocolException("Missing seed terminator.");
                }
                else
                {
                    seed2 = cursor.ReadNullTerminatedBytes();
                }
            }
            else
            {
                seed2 = Array.Empty<byte>();
            }

            string? pluginName = null;
            if ((capabilities & CapabilityFlags.ClientPluginAuth) != 0 && cursor.HasRemaining)
            {
                pluginName = cursor.ReadNullTerminatedAscii();
            }

            var seed = new byte[seed1.Length + seed2.Length];
            Buffer.BlockCopy(seed1, 0, seed, 0, seed1.Length);
            Buffer.BlockCopy(seed2, 0, seed, seed1.Length, seed2.Length);

            var isOceanBase = serverVersion.IndexOf("OceanBase", StringComparison.OrdinalIgnoreCase) >= 0;
            return new InitialHandshakePacket(
                protocolVersion,
                serverVersion,
                connectionId,
                seed,
                capabilities,
                charset,
                statusFlags,
                pluginName,
                isOceanBase);
        }
    }
}
