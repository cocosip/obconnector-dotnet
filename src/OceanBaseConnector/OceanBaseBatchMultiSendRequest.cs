#if NET8_0_OR_GREATER
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector
{
    internal sealed class OceanBaseBatchMultiSendRequest
    {
        internal OceanBaseBatchMultiSendRequest(byte[] packet, uint statementId, bool binary, OceanBaseParameter[]? longDataParameters,
            Dictionary<(string, string, string, string), OceanBaseComplexType>? sourceComplexTypes = null,
            string? sessionTimeZone = null, NationalCharacterSet? nationalCharacterSet = null)
        {
            Packet = packet; StatementId = statementId; Binary = binary; LongDataParameters = longDataParameters;
            SourceComplexTypes = sourceComplexTypes;
            SessionTimeZone = sessionTimeZone; NationalCharacterSet = nationalCharacterSet;
        }

        internal byte[] Packet { get; }
        internal uint StatementId { get; }
        internal bool Binary { get; }
        internal OceanBaseParameter[]? LongDataParameters { get; }
        internal Dictionary<(string, string, string, string), OceanBaseComplexType>? SourceComplexTypes { get; }
        internal string? SessionTimeZone { get; }
        internal NationalCharacterSet? NationalCharacterSet { get; }
        internal bool HasLongData => LongDataParameters != null;
    }
}
#endif
