#if NET8_0_OR_GREATER
namespace OceanBaseConnector
{
    internal sealed class OceanBaseBatchMultiSendRequest
    {
        internal OceanBaseBatchMultiSendRequest(byte[] packet, uint statementId, bool binary, OceanBaseParameter[]? longDataParameters)
        {
            Packet = packet; StatementId = statementId; Binary = binary; LongDataParameters = longDataParameters;
        }

        internal byte[] Packet { get; }
        internal uint StatementId { get; }
        internal bool Binary { get; }
        internal OceanBaseParameter[]? LongDataParameters { get; }
        internal bool HasLongData => LongDataParameters != null;
    }
}
#endif
