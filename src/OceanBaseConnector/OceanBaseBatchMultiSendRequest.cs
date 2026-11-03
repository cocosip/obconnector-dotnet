#if NET8_0_OR_GREATER
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector
{
    internal sealed class OceanBaseBatchMultiSendRequest : System.IDisposable
    {
        internal OceanBaseBatchMultiSendRequest(byte[] packet, uint statementId, bool binary, OceanBaseParameter[]? longDataParameters,
            Dictionary<(string, string, string, string), OceanBaseComplexType>? sourceComplexTypes = null,
            string? sessionTimeZone = null, NationalCharacterSet? nationalCharacterSet = null, OracleCommandPlan? oraclePlan = null)
        {
            Packet = packet; StatementId = statementId; Binary = binary; LongDataParameters = longDataParameters;
            SourceComplexTypes = sourceComplexTypes;
            SessionTimeZone = sessionTimeZone; NationalCharacterSet = nationalCharacterSet;
            OraclePlan = oraclePlan;
        }

        internal byte[] Packet { get; }
        internal uint StatementId { get; }
        internal bool Binary { get; }
        internal OceanBaseParameter[]? LongDataParameters { get; }
        internal Dictionary<(string, string, string, string), OceanBaseComplexType>? SourceComplexTypes { get; }
        internal string? SessionTimeZone { get; }
        internal NationalCharacterSet? NationalCharacterSet { get; }
        internal bool HasLongData => LongDataParameters != null;
        private OracleCommandPlan? OraclePlan { get; }
        internal QueryResult CreateResult(CommandExchange exchange, bool sequential = false) => new QueryResult(exchange, Binary, completeExchange: false, sequential: sequential,
            oracleParameterCount: OraclePlan?.Bindings.Parameters.Length, oraclePlan: OraclePlan);
        public void Dispose()
        {
            OraclePlan?.DiscardOutputs();
            OraclePlan?.Cursors?.EndExecution();
        }
    }
}
#endif
