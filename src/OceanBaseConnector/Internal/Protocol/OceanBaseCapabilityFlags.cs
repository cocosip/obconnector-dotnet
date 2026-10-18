using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// OceanBase capability bits exchanged through the session variable
    /// <c>ob_capability_flag</c> (64-bit). Values verified against the Java
    /// baseline (internal/protocol/OceanBaseCapabilityFlag.java). OB20 and
    /// full-link tracing are stage-D features; the flags are parsed and
    /// stored from stage A05 so the effective-capability rule can gate on
    /// them without re-negotiation later.
    /// </summary>
    [Flags]
    internal enum OceanBaseCapabilityFlags : ulong
    {
        None = 0,

        ObCapPartitionTable = 1,
        ObCapChangeUser = 1 << 1,
        ObCapReadWeak = 1 << 2,
        ObCapChecksum = 1 << 3,
        ObCapSafeWeakRead = 1 << 4,
        ObCapPriorityHit = 1 << 5,
        ObCapChecksumSwitch = 1 << 6,
        ObCapOcjEnableExtraOkPacket = 1 << 7,

        /// <summary>Server supports the OB20 protocol wrapping (stage D).</summary>
        ObCapObProtocolV2 = 1 << 8,

        ObCapExtraOkPacketForStatistics = 1 << 9,
        ObCapAbundantFeedback = 1 << 10,
        ObCapPlRoute = 1 << 11,
        ObCapProxyReroute = 1 << 12,
        ObCapProxySessionSync = 1 << 13,
        ObCapFullLinkTrace = 1 << 14,
        ObCapNewExtraInfo = 1 << 15,
        ObCapProxySessionVarSync = 1 << 16,
        ObCapProxyWeakStaleFeedback = 1 << 17,
        ObCapFullLinkTraceShowTrace = 1 << 18,
        ObCapLocalFiles = 1 << 20,
    }
}
