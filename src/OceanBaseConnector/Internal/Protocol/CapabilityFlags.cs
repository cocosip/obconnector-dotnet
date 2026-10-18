using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// MySQL-protocol capability flags exchanged in the initial handshake,
    /// SSLRequest and handshake-response packets. OceanBase-specific
    /// capability bits and their negotiation rules are added with A05.
    /// </summary>
    [Flags]
    internal enum CapabilityFlags : uint
    {
        None = 0,

        ClientLongPassword = 0x1,
        ClientFoundRows = 0x2,
        ClientLongFlag = 0x4,
        ClientConnectWithDb = 0x8,
        ClientNoSchema = 0x10,
        ClientCompress = 0x20,
        ClientOdbc = 0x40,
        ClientLocalFiles = 0x80,
        ClientIgnoreSpace = 0x100,
        ClientProtocol41 = 0x200,
        ClientInteractive = 0x400,
        ClientSsl = 0x800,
        ClientIgnoreSigpipe = 0x1000,
        ClientTransactions = 0x2000,
        ClientReserved = 0x4000,
        ClientSecureConnection = 0x8000,
        ClientMultiStatements = 0x10000,
        ClientMultiResults = 0x20000,
        ClientPsMultiResults = 0x40000,
        ClientPluginAuth = 0x80000,
        ClientConnectAttrs = 0x100000,
        ClientPluginAuthLenencClientData = 0x200000,
        ClientCanHandleExpiredPasswords = 0x400000,
        ClientSessionTrack = 0x800000,
        ClientDeprecateEof = 0x1000000,
        ClientOptionalResultsetMetadata = 0x2000000,
        ClientZstdCompressionAlgorithm = 0x4000000,
        ClientQueryAttributes = 0x8000000,

        /// <summary>OceanBase extension: client understands Oracle-mode packets.</summary>
        ClientSupportOracle = 0x8000000,

        /// <summary>OceanBase extension: client understands LOB locators.</summary>
        ClientSupportLobLocator = 0x20000000,
        ClientSslVerifyServerCert = 0x40000000,
    }
}
