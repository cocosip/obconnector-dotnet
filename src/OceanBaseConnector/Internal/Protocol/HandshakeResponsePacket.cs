using System;
using System.IO;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Builds the client handshake-response packet (and the auth-switch
    /// response payload): capability flags (4, LE), max packet size (4, LE),
    /// charset (1), 19 zero bytes, MariaDB extended flags (4, zero here),
    /// NUL-terminated username, auth data (lenenc- or 1-byte-prefixed
    /// depending on server capabilities), optional database, plugin name and
    /// length-encoded connection attributes. No SQL text or credentials
    /// other than the username and the auth token appear in the packet.
    /// </summary>
    internal static class HandshakeResponsePacket
    {
        public const int MaxPacketSize = SslRequestPacket.DefaultMaxPacketSize;

        private const string ClientNameAttribute = "_client_name";
        private const string ClientVersionAttribute = "_client_version";
        private const string ServerHostAttribute = "_server_host";
        private const string DriverName = "OceanBase Connector/.NET";

        public static byte[] Build(
            string userId,
            byte[] authenticationData,
            string database,
            CapabilityFlags clientCapabilities,
            CapabilityFlags serverCapabilities,
            byte charset,
            string authenticationPluginName,
            string? serverHost,
            bool requestOb20 = false,
            bool requestFullLinkTrace = false, bool requestLobLocators = false)
        {
            var writer = new MemoryStream();
            WriteUInt32(writer, (uint)clientCapabilities);
            WriteUInt32(writer, MaxPacketSize);
            writer.WriteByte(charset);
            writer.Write(new byte[19], 0, 19);
            WriteUInt32(writer, 0); // MariaDB extended capabilities

            var userBytes = Encoding.UTF8.GetBytes(userId);
            writer.Write(userBytes, 0, userBytes.Length);
            writer.WriteByte(0);

            if ((serverCapabilities & CapabilityFlags.ClientPluginAuthLenencClientData) != 0)
            {
                WriteLengthEncodedInteger(writer, authenticationData.Length);
                writer.Write(authenticationData, 0, authenticationData.Length);
            }
            else if ((serverCapabilities & CapabilityFlags.ClientSecureConnection) != 0)
            {
                if (authenticationData.Length > byte.MaxValue)
                {
                    throw new ProtocolException(
                        $"Authentication data of {authenticationData.Length} byte(s) exceeds the 255-byte limit of the secure-connection encoding.");
                }

                writer.WriteByte((byte)authenticationData.Length);
                writer.Write(authenticationData, 0, authenticationData.Length);
            }
            else
            {
                writer.Write(authenticationData, 0, authenticationData.Length);
                writer.WriteByte(0);
            }

            if ((clientCapabilities & CapabilityFlags.ClientConnectWithDb) != 0)
            {
                var databaseBytes = Encoding.UTF8.GetBytes(database);
                writer.Write(databaseBytes, 0, databaseBytes.Length);
                writer.WriteByte(0);
            }

            if ((serverCapabilities & CapabilityFlags.ClientPluginAuth) != 0)
            {
                var pluginBytes = Encoding.ASCII.GetBytes(authenticationPluginName);
                writer.Write(pluginBytes, 0, pluginBytes.Length);
                writer.WriteByte(0);
            }

            if ((serverCapabilities & CapabilityFlags.ClientConnectAttrs) != 0)
            {
                WriteConnectAttributes(writer, serverHost, requestOb20, requestFullLinkTrace, requestLobLocators);
            }

            return writer.ToArray();
        }

        public static byte[] BuildAuthSwitchResponse(byte[] authenticationData)
        {
            return authenticationData;
        }

        private static void WriteConnectAttributes(MemoryStream writer, string? serverHost, bool requestOb20, bool requestFullLinkTrace, bool requestLobLocators)
        {
            using var attributes = new MemoryStream();
            WriteLengthString(attributes, ClientNameAttribute, DriverName);
            WriteLengthString(attributes, ClientVersionAttribute, typeof(HandshakeResponsePacket)
                .Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            WriteLengthString(attributes, ServerHostAttribute, serverHost ?? string.Empty);
            var proxyFlags = requestOb20 ? (requestFullLinkTrace ? 311552 : 256) : 0;
            if (proxyFlags != 0) WriteLengthString(attributes, "__proxy_capability_flag", proxyFlags.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (requestLobLocators) WriteLengthString(attributes, "__ob_client_attribute_capability_flag", "1");

            var attributeBytes = attributes.ToArray();
            WriteLengthEncodedInteger(writer, attributeBytes.Length);
            writer.Write(attributeBytes, 0, attributeBytes.Length);
        }

        private static void WriteLengthString(MemoryStream writer, string key, string value)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            WriteLengthEncodedInteger(writer, keyBytes.Length);
            writer.Write(keyBytes, 0, keyBytes.Length);
            WriteLengthEncodedInteger(writer, valueBytes.Length);
            writer.Write(valueBytes, 0, valueBytes.Length);
        }

        private static void WriteUInt32(MemoryStream writer, uint value)
        {
            writer.WriteByte((byte)value);
            writer.WriteByte((byte)(value >> 8));
            writer.WriteByte((byte)(value >> 16));
            writer.WriteByte((byte)(value >> 24));
        }

        private static void WriteLengthEncodedInteger(MemoryStream writer, long value)
        {
            if (value < 251)
            {
                writer.WriteByte((byte)value);
            }
            else if (value <= ushort.MaxValue)
            {
                writer.WriteByte(0xFC);
                writer.WriteByte((byte)value);
                writer.WriteByte((byte)(value >> 8));
            }
            else if (value <= 0xFFFFFF)
            {
                writer.WriteByte(0xFD);
                writer.WriteByte((byte)value);
                writer.WriteByte((byte)(value >> 8));
                writer.WriteByte((byte)(value >> 16));
            }
            else
            {
                writer.WriteByte(0xFE);
                for (var i = 0; i < 8; i++)
                {
                    writer.WriteByte((byte)(value >> (8 * i)));
                }
            }
        }
    }
}
