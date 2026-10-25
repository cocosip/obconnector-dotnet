using System;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
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
    /// bit and checked against the session's reported mode. Every connection
    /// confirms OceanBase identity and version before the session is usable.
    /// SHA-2 fast authentication is supported; complete passwords are sent
    /// only over identity-verified TLS. Unknown plugins fail explicitly. The
    /// synchronous and asynchronous entries share parsing and decisions but
    /// never share each other's I/O calls.
    /// </summary>
    internal static partial class HandshakeFlow
    {
        public static ConnectResult Authenticate(
            TcpTransport transport,
            PacketChannel channel,
            OceanBaseConnectionOptions options,
            CancellationToken cancellationToken = default,
            X509Certificate2? clientCertificate = null, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null)
        {
            var phase = DriverDiagnostics.StartConnectionPhase("greeting");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handshake = ParseInitialHandshake(channel.ReadPacket(), options);
                var clientCapabilities = ClientCapabilities.Build(handshake.Capabilities, options);
                phase?.Finish(null);

                phase = options.TlsMode == OceanBaseTlsMode.Disabled ? null : DriverDiagnostics.StartConnectionPhase("tls");
                TlsHandshakeFlow.Apply(transport, channel, handshake.Capabilities, clientCapabilities, options, clientCertificate, remoteCertificateValidationCallback);
                phase?.Finish(null);

                phase = DriverDiagnostics.StartConnectionPhase("authentication");
                cancellationToken.ThrowIfCancellationRequested();
                using var authentication = new AuthenticationConversation(options, handshake.AuthenticationPluginName, handshake.Seed);
                cancellationToken.ThrowIfCancellationRequested();
                channel.WritePacket(BuildHandshakeResponse(handshake, clientCapabilities, options, authentication));
                var result = ReadAuthenticationResult(channel, handshake, clientCapabilities, options, authentication, cancellationToken);
                EnableFraming(channel, result, options);
                phase?.Finish(null);

                phase = DriverDiagnostics.StartConnectionPhase("identity");
                ConfirmIdentity(channel, result, cancellationToken);
                phase?.Finish(null);
                return result;
            }
            catch (Exception error) { phase?.Finish(error); throw; }
        }

        public static async Task<ConnectResult> AuthenticateAsync(
            TcpTransport transport, PacketChannel channel, OceanBaseConnectionOptions options,
            CancellationToken cancellationToken,
            X509Certificate2? clientCertificate = null, RemoteCertificateValidationCallback? remoteCertificateValidationCallback = null)
        {
            var phase = DriverDiagnostics.StartConnectionPhase("greeting");
            try
            {
                var handshake = ParseInitialHandshake(
                    await channel.ReadPacketAsync(cancellationToken).ConfigureAwait(false), options);
                var capabilities = ClientCapabilities.Build(handshake.Capabilities, options);
                phase?.Finish(null);

                phase = options.TlsMode == OceanBaseTlsMode.Disabled ? null : DriverDiagnostics.StartConnectionPhase("tls");
                await TlsHandshakeFlow.ApplyAsync(transport, channel, handshake.Capabilities, capabilities, options, cancellationToken, clientCertificate, remoteCertificateValidationCallback)
                    .ConfigureAwait(false);
                phase?.Finish(null);

                phase = DriverDiagnostics.StartConnectionPhase("authentication");
                cancellationToken.ThrowIfCancellationRequested();
                using var authentication = new AuthenticationConversation(options, handshake.AuthenticationPluginName, handshake.Seed);
                cancellationToken.ThrowIfCancellationRequested();
                await channel.WritePacketAsync(BuildHandshakeResponse(handshake, capabilities, options, authentication), cancellationToken)
                    .ConfigureAwait(false);
                var result = await ReadAuthenticationResultAsync(channel, handshake, capabilities, options, authentication, cancellationToken)
                    .ConfigureAwait(false);
                EnableFraming(channel, result, options);
                phase?.Finish(null);

                phase = DriverDiagnostics.StartConnectionPhase("identity");
                await ConfirmIdentityAsync(channel, result, cancellationToken).ConfigureAwait(false);
                phase?.Finish(null);
                return result;
            }
            catch (Exception error) { phase?.Finish(error); throw; }
        }

        private static byte[] BuildHandshakeResponse(
            InitialHandshakePacket handshake, CapabilityFlags capabilities, OceanBaseConnectionOptions options,
            AuthenticationConversation authentication)
        {
            options.Credential?.EnsureValid();
            return HandshakeResponsePacket.Build(options.UserId,
                authentication.InitialResponse,
                options.Database, capabilities, handshake.Capabilities, SslRequestPacket.DefaultCharsetId,
                authentication.PluginName, options.Server, options.UseOb20, options.EnableFullLinkTrace, options.UseLobLocators);
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

        internal static ConnectResult ReadAuthenticationResult(
            PacketChannel channel,
            InitialHandshakePacket handshake,
            CapabilityFlags clientCapabilities,
            OceanBaseConnectionOptions options,
            AuthenticationConversation authentication,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var packet = channel.ReadPacket();
                cancellationToken.ThrowIfCancellationRequested();
                var finished = TryFinishAuthentication(packet, handshake, clientCapabilities, options, authentication, out var result, out var authenticationData);
                cancellationToken.ThrowIfCancellationRequested();
                if (finished)
                {
                    return result!;
                }

                if (authenticationData is not null) { options.Credential?.EnsureValid(); channel.WritePacket(authenticationData); }
            }
        }

        internal static async Task<ConnectResult> ReadAuthenticationResultAsync(
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
                cancellationToken.ThrowIfCancellationRequested();
                var finished = TryFinishAuthentication(packet, handshake, clientCapabilities, options, authentication, out var result, out var authenticationData);
                cancellationToken.ThrowIfCancellationRequested();
                if (finished)
                {
                    return result!;
                }

                if (authenticationData is not null)
                {
                    options.Credential?.EnsureValid();
                    await channel.WritePacketAsync(authenticationData, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Dispatches one authentication response packet. Returns true with
        /// the final result on OK; otherwise returns false with an optional
        /// response (null means keep reading without sending a packet).
        /// </summary>
        internal static bool TryFinishAuthentication(
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
                    var mode = DetermineMode(ok.StatusFlags, options);
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
            ServerStatusFlags statusFlags,
            OceanBaseConnectionOptions options)
        {
            var mode = (statusFlags & ServerStatusFlags.ObServerUseOracleMode) != 0
                ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql;

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
