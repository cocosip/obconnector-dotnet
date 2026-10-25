using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Server status flags carried in OK packets and status transitions.
    /// Values verified against the Java baseline
    /// (internal/util/constant/ServerStatus.java).
    /// </summary>
    [Flags]
    internal enum ServerStatusFlags : ushort
    {
        None = 0,

        InTransaction = 1,
        Autocommit = 2,

        /// <summary>
        /// OceanBase extension: the authenticated session runs in Oracle
        /// compatibility mode. Only trustworthy on OceanBase/OBProxy
        /// endpoints whose version is on the verified matrix; the handshake
        /// flow refuses to deliver a session whose mode it cannot trust.
        /// </summary>
        ObServerUseOracleMode = 4,

        MoreResultsExists = 8,
        QueryNoGoodIndexUsed = 16,
        QueryNoIndexUsed = 32,
        CursorExists = 64,
        LastRowSent = 128,
        DbDropped = 256,
        NoBackslashEscapes = 512,
        MetadataChanged = 1024,
        QueryWasSlow = 2048,
        PsOutParameters = 4096,
        InTransactionReadOnly = 8192,
        ServerSessionStateChanged = 1 << 14,
    }
}
