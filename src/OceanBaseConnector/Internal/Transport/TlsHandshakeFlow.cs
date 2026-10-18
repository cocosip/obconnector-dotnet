using System;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Transport
{
    /// <summary>
    /// Applies the TLS leg of the connection sequence after the server's
    /// initial handshake has been read and its capabilities parsed:
    /// check CLIENT_SSL, send SSLRequest with the next sequence id, upgrade
    /// to an SslStream and hand it to the packet channel. Strict ordering is
    /// deliberate: authentication data is only sent by the caller after this
    /// flow succeeded, so a server without TLS, a certificate error or a
    /// failed upgrade always leaves zero authentication bytes on the wire
    /// and never downgrades to plaintext.
    /// </summary>
    internal static class TlsHandshakeFlow
    {
        /// <summary>
        /// Capabilities declared in the SSLRequest. Kept minimal until the
        /// full client capability set is negotiated in A05; CLIENT_SSL is
        /// what unlocks the TLS upgrade.
        /// </summary>
        public static CapabilityFlags BuildSslRequestCapabilities() =>
            CapabilityFlags.ClientSsl |
            CapabilityFlags.ClientProtocol41 |
            CapabilityFlags.ClientSecureConnection;

        public static void Apply(
            TcpTransport transport,
            PacketChannel channel,
            CapabilityFlags serverCapabilities,
            OceanBaseConnectionOptions options)
        {
            if (options.TlsMode == OceanBaseTlsMode.Disabled)
            {
                return;
            }

            RequireServerTls(serverCapabilities, options);

            channel.AlignSendSequenceToReceived();
            channel.WritePacket(SslRequestPacket.Build(BuildSslRequestCapabilities()));

            var targetHost = TlsUpgrade.ResolveTargetHost(options.Server, options.TlsServerName);
            var sslStream = TlsUpgrade.Upgrade(transport.Stream, targetHost, options.TlsMode);
            channel.ReplaceUnderlyingStream(sslStream);
        }

        public static async Task ApplyAsync(
            TcpTransport transport,
            PacketChannel channel,
            CapabilityFlags serverCapabilities,
            OceanBaseConnectionOptions options,
            CancellationToken cancellationToken)
        {
            if (options.TlsMode == OceanBaseTlsMode.Disabled)
            {
                return;
            }

            RequireServerTls(serverCapabilities, options);

            channel.AlignSendSequenceToReceived();
            await channel.WritePacketAsync(SslRequestPacket.Build(BuildSslRequestCapabilities()), cancellationToken)
                .ConfigureAwait(false);

            var targetHost = TlsUpgrade.ResolveTargetHost(options.Server, options.TlsServerName);
            var sslStream = await TlsUpgrade.UpgradeAsync(transport.Stream, targetHost, options.TlsMode, cancellationToken)
                .ConfigureAwait(false);
            channel.ReplaceUnderlyingStream(sslStream);
        }

        private static void RequireServerTls(CapabilityFlags serverCapabilities, OceanBaseConnectionOptions options)
        {
            if ((serverCapabilities & CapabilityFlags.ClientSsl) == 0)
            {
                throw new ProtocolException(
                    $"Server at {options.Endpoint} does not advertise TLS (CLIENT_SSL); refusing to downgrade or send credentials.");
            }
        }
    }
}
