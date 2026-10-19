using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>
    /// Drives the authentication leg on top of an established transport:
    /// read the initial handshake, run the TLS flow (A04), negotiate client
    /// capabilities, send the handshake response and process the server's
    /// answer including authentication switches. The compatibility mode is
    /// taken from the authenticated OK packet's OB_SERVER_USE_ORACLE_MODE
    /// bit and only for endpoints whose version identifies OceanBase;
    /// unknown endpoints are refused before the session is usable.
    /// SHA-2 fast authentication is supported; complete passwords are sent
    /// only over identity-verified TLS. Unknown plugins fail explicitly. The
    /// synchronous and asynchronous entries share parsing and decisions but
    /// never share each other's I/O calls.
    /// </summary>
    internal static class HandshakeFlow
    {
        public static ConnectResult Authenticate(
            TcpTransport transport,
            PacketChannel channel,
            OceanBaseConnectionOptions options)
        {
            var handshake = ParseInitialHandshake(channel.ReadPacket(), options);
            var clientCapabilities = ClientCapabilities.Build(handshake.Capabilities, options);

            TlsHandshakeFlow.Apply(transport, channel, handshake.Capabilities, clientCapabilities, options);

            var authentication = new AuthenticationConversation(options, handshake.AuthenticationPluginName, handshake.Seed);
            channel.WritePacket(BuildHandshakeResponse(handshake, clientCapabilities, options, authentication));
            return ReadAuthenticationResult(channel, handshake, clientCapabilities, options, authentication);
        }

        public static async Task<ConnectResult> AuthenticateAsync(
            TcpTransport transport, PacketChannel channel, OceanBaseConnectionOptions options,
            CancellationToken cancellationToken)
        {
            var handshake = ParseInitialHandshake(
                await channel.ReadPacketAsync(cancellationToken).ConfigureAwait(false), options);
            var capabilities = ClientCapabilities.Build(handshake.Capabilities, options);
            await TlsHandshakeFlow.ApplyAsync(transport, channel, handshake.Capabilities, capabilities, options, cancellationToken)
                .ConfigureAwait(false);
            var authentication = new AuthenticationConversation(options, handshake.AuthenticationPluginName, handshake.Seed);
            await channel.WritePacketAsync(BuildHandshakeResponse(handshake, capabilities, options, authentication), cancellationToken)
                .ConfigureAwait(false);
            return await ReadAuthenticationResultAsync(channel, handshake, capabilities, options, authentication, cancellationToken)
                .ConfigureAwait(false);
        }

        private static byte[] BuildHandshakeResponse(
            InitialHandshakePacket handshake, CapabilityFlags capabilities, OceanBaseConnectionOptions options,
            AuthenticationConversation authentication)
        {
            return HandshakeResponsePacket.Build(options.UserId,
                authentication.InitialResponse,
                options.Database, capabilities, handshake.Capabilities, SslRequestPacket.DefaultCharsetId,
                authentication.PluginName, options.Server, options.UseOb20, options.EnableFullLinkTrace);
        }

        private static InitialHandshakePacket ParseInitialHandshake(byte[] packet, OceanBaseConnectionOptions options)
        {
            if (ErrPacket.IsErr(packet)) throw CreateServerError(ErrPacket.Parse(packet), options);
            var handshake = InitialHandshakePacket.Parse(packet);
            var required = CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSecureConnection;
            if ((handshake.Capabilities & required) != required)
                throw new ProtocolException("Server lacks required protocol 4.1 or secure authentication capability.");
            return handshake;
        }

        private static ConnectResult ReadAuthenticationResult(
            PacketChannel channel,
            InitialHandshakePacket handshake,
            CapabilityFlags clientCapabilities,
            OceanBaseConnectionOptions options,
            AuthenticationConversation authentication)
        {
            while (true)
            {
                var packet = channel.ReadPacket();
                if (TryFinishAuthentication(packet, handshake, clientCapabilities, options, authentication, out var result, out var authenticationData))
                {
                    return result!;
                }

                if (authenticationData is not null) channel.WritePacket(authenticationData);
            }
        }

        private static async Task<ConnectResult> ReadAuthenticationResultAsync(
            PacketChannel channel,
            InitialHandshakePacket handshake,
            CapabilityFlags clientCapabilities,
            OceanBaseConnectionOptions options,
            AuthenticationConversation authentication,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var packet = await channel.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
                if (TryFinishAuthentication(packet, handshake, clientCapabilities, options, authentication, out var result, out var authenticationData))
                {
                    return result!;
                }

                if (authenticationData is not null)
                    await channel.WritePacketAsync(authenticationData, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Dispatches one authentication response packet. Returns true with
        /// the final result on OK; otherwise returns false with an optional
        /// response (null means keep reading without sending a packet).
        /// </summary>
        private static bool TryFinishAuthentication(
            byte[] packet,
            InitialHandshakePacket handshake,
            CapabilityFlags clientCapabilities,
            OceanBaseConnectionOptions options,
            AuthenticationConversation authentication,
            out ConnectResult? result,
            out byte[]? authenticationData)
        {
            result = null;
            authenticationData = null;

            if (packet.Length == 0)
            {
                throw new ProtocolException("Empty packet during authentication.");
            }

            switch (packet[0])
            {
                case 0x00:
                    authentication.ValidateSuccess();
                    var negotiated = new NegotiatedCapabilities(clientCapabilities, handshake.Capabilities);
                    var ok = OkPacket.Parse(packet, negotiated.Effective);
                    var mode = DetermineMode(handshake, ok.StatusFlags, options);
                    result = new ConnectResult(
                        handshake.ServerVersion,
                        handshake.ConnectionId,
                        mode,
                        negotiated,
                        ok.StatusFlags,
                        ParseObCapabilityFlag(ok.SessionVariables),
                        ok.SessionVariables);
                    return true;

                case 0xFF:
                    var error = ErrPacket.Parse(packet);
                    throw CreateServerError(error, options);

                default:
                    authenticationData = authentication.Continue(packet);
                    return false;
            }
        }

        private static OceanBaseCompatibilityMode DetermineMode(
            InitialHandshakePacket handshake,
            ServerStatusFlags statusFlags,
            OceanBaseConnectionOptions options)
        {
            if (!handshake.IsOceanBaseServer)
            {
                throw new OceanBaseException(
                    $"Server at {options.Endpoint} does not identify an OceanBase endpoint; " +
                    "the compatibility mode cannot be determined reliably, so the connection is refused before any query.");
            }

            if ((statusFlags & ServerStatusFlags.ObServerUseOracleMode) == 0)
            {
                // No server versions have passed G0/G1 yet; absence cannot prove MySQL mode.
                throw new OceanBaseException($"Compatibility mode at {options.Endpoint} cannot be confirmed without a verified OceanBase/OBProxy version matrix.");
            }
            var mode = OceanBaseCompatibilityMode.Oracle;

            if (options.ExpectedMode.HasValue && options.ExpectedMode.Value != mode)
            {
                throw new OceanBaseException(
                    $"Server at {options.Endpoint} authenticated in {mode} mode, but the connection string requires " +
                    $"{options.ExpectedMode.Value} mode (ExpectedMode).");
            }

            return mode;
        }

        private static OceanBaseCapabilityFlags ParseObCapabilityFlag(System.Collections.Generic.IReadOnlyDictionary<string, string> sessionVariables)
        {
            if (!sessionVariables.TryGetValue("ob_capability_flag", out var value) ||
                !long.TryParse(value, out var parsed))
            {
                return OceanBaseCapabilityFlags.None;
            }

            // Defined flags live far below bit 63, so a signed long is a safe
            // transport for the value; widen to the flag enum afterwards.
            return (OceanBaseCapabilityFlags)(ulong)parsed;
        }

        private static OceanBaseException CreateServerError(ErrPacket error, OceanBaseConnectionOptions options)
        {
            return new OceanBaseException(
                $"Authentication to {options.Endpoint} failed (server error {error.ErrorCode}).",
                error.ErrorCode,
                error.SqlState);
        }
    }
}
