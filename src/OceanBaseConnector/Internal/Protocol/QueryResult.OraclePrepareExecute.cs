using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class QueryResult
    {
        private readonly int? _oracleParameterCount;
        private bool _oracleHeaderPending;
        private bool _oracleTailPending;
        internal OraclePrepareExecutePacket? OracleHeader { get; private set; }

        private OraclePrepareExecutePacket BeginOracleMetadata(byte[] bytes)
        {
            var header = OraclePrepareExecutePacket.ParseHeader(bytes);
            if (header.ParameterCount != _oracleParameterCount || header.HasResultSet && header.ColumnCount == 0)
                throw new ProtocolException("Oracle PrepareExecute metadata counts do not match the request.");
            OracleHeader = header;
            _oracleHeaderPending = false; _oracleTailPending = true;
            _metadataBytes = 0; _ended = false; _more = false; HasRows = false; Row = _pending = null;
            Columns = header.HasResultSet ? new ResultColumn[header.ColumnCount] : System.Array.Empty<ResultColumn>();
            return header;
        }

        private bool StartOracleMetadata()
        {
            var header = BeginOracleMetadata(_query.Receive());
            for (var i = 0; i < header.ParameterCount; i++) ParseColumn(_query.Receive());
            if (header.ParameterCount > 0 && !DeprecatedEof) ReadEof(_query.Receive());
            if (header.HasResultSet) return true;
            ReadOracleTail(); return false;
        }

        private async Task<bool> StartOracleMetadataAsync(CancellationToken token)
        {
            var header = BeginOracleMetadata(await _query.ReceiveAsync(token).ConfigureAwait(false));
            for (var i = 0; i < header.ParameterCount; i++) ParseColumn(await _query.ReceiveAsync(token).ConfigureAwait(false));
            if (header.ParameterCount > 0 && !DeprecatedEof) ReadEof(await _query.ReceiveAsync(token).ConfigureAwait(false));
            if (header.HasResultSet) return true;
            await ReadOracleTailAsync(token).ConfigureAwait(false); return false;
        }

        private void ReadOracleTail() => EndOracleTail(_query.Receive());
        private async Task ReadOracleTailAsync(CancellationToken token)
            => EndOracleTail(await _query.ReceiveAsync(token).ConfigureAwait(false));

        private void EndOracleTail(byte[] packet)
        {
            var ok = OkPacket.Parse(packet, _query.Capabilities);
            _affected = checked(System.Math.Max(0, _affected) + ok.AffectedRows);
            _oracleTailPending = false;
            End(ok.StatusFlags, ok);
        }
    }
}
