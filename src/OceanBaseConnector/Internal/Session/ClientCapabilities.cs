using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>
    /// Computes the client capability set for a connection. Base set mirrors
    /// the Java baseline plus this driver's connection policy:
    /// CLIENT_FOUND_ROWS follows the Use Affected Rows connection option,
    /// and CLIENT_SUPPORT_ORACLE / CLIENT_SESSION_TRACK are declared so the
    /// server may answer in either compatibility mode. CLIENT_SSL is added
    /// by the TLS flow only when TLS is actually negotiated.
    /// </summary>
    internal static class ClientCapabilities
    {
        public static CapabilityFlags Build(CapabilityFlags serverCapabilities, OceanBaseConnectionOptions options)
        {
            var capabilities = CapabilityFlags.ClientIgnoreSpace
                | CapabilityFlags.ClientProtocol41
                | CapabilityFlags.ClientTransactions
                | CapabilityFlags.ClientSecureConnection
                | CapabilityFlags.ClientMultiResults
                | CapabilityFlags.ClientPsMultiResults
                | CapabilityFlags.ClientPluginAuth
                | CapabilityFlags.ClientConnectAttrs
                | CapabilityFlags.ClientPluginAuthLenencClientData
                | CapabilityFlags.ClientSessionTrack
                | CapabilityFlags.ClientSupportOracle;

            if (options.UseCompression)
            {
                if ((serverCapabilities & CapabilityFlags.ClientCompress) != 0)
                    capabilities |= CapabilityFlags.ClientCompress;
            }
            if (!options.UseAffectedRows) capabilities |= CapabilityFlags.ClientFoundRows;
            if (options.AllowMultiStatements) capabilities |= CapabilityFlags.ClientMultiStatements;
            if (options.AllowLocalInfile) capabilities |= CapabilityFlags.ClientLocalFiles;

            if (options.Database.Length > 0)
            {
                capabilities |= CapabilityFlags.ClientConnectWithDb;
            }
            if (options.UseLobLocators) capabilities |= CapabilityFlags.ClientSupportLobLocator;

            if (options.TlsMode != OceanBaseTlsMode.Disabled)
                capabilities |= CapabilityFlags.ClientSsl;

            if ((serverCapabilities & CapabilityFlags.ClientDeprecateEof) != 0)
            {
                capabilities |= CapabilityFlags.ClientDeprecateEof;
            }

            return capabilities;
        }
    }
}
