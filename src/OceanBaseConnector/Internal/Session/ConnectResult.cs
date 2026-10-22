using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>
    /// Outcome of a successful authentication: the session facts every
    /// later stage (commands, decoding, transactions) must rely on. The
    /// compatibility mode is only ever set here, from the server's
    /// authenticated OK packet, never from connection-string hints.
    /// </summary>
    internal sealed class ConnectResult
    {
        public ConnectResult(
            string serverVersion,
            uint connectionId,
            OceanBaseCompatibilityMode mode,
            NegotiatedCapabilities capabilities,
            ServerStatusFlags serverStatus,
            OceanBaseCapabilityFlags obCapabilityFlag,
            IReadOnlyDictionary<string, string> sessionVariables)
        {
            GreetingVersion = serverVersion;
            ServerVersion = serverVersion;
            ConnectionId = connectionId;
            Mode = mode;
            Capabilities = capabilities;
            ServerStatus = serverStatus;
            ObCapabilityFlag = obCapabilityFlag;
            SessionVariables = sessionVariables;
        }

        internal string GreetingVersion { get; }
        public string ServerVersion { get; private set; } = string.Empty;
        internal Version? ProductVersion { get; private set; }

        internal void ConfirmIdentity(string version, string mode)
        {
            var parsed = OceanBaseServerVersion.Parse(version);
            if (parsed == null) throw new OceanBaseException("OceanBase identity requires a supported product version format.");
            var expected = Mode == OceanBaseCompatibilityMode.Oracle ? "ORACLE" : "MYSQL";
            if (!string.Equals(mode, expected, StringComparison.OrdinalIgnoreCase))
                throw new OceanBaseException("OceanBase session compatibility mode does not match authentication.");
            ServerVersion = version;
            ProductVersion = parsed;
        }

        public uint ConnectionId { get; }

        public OceanBaseCompatibilityMode Mode { get; }

        public NegotiatedCapabilities Capabilities { get; }

        public ServerStatusFlags ServerStatus { get; }

        /// <summary>Parsed ob_capability_flag (0 when the server did not report it).</summary>
        public OceanBaseCapabilityFlags ObCapabilityFlag { get; }

        public IReadOnlyDictionary<string, string> SessionVariables { get; }
    }
}
