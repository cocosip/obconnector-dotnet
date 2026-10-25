using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class QueryResult
    {
        private readonly MySqlCommandPlan? _mysqlPlan;
        private bool _mysqlOutputsPending;
        internal bool DrainOnClose => _mysqlPlan != null && !_faulted && _query.IsActive;

        private void ReadMySqlOutputs()
        {
            _query.Send(OceanBaseCommand.SqlPacket(_mysqlPlan!.OutputSql, 3));
            var result = new QueryResult(_query, completeExchange: false);
            result.Start();
            _mysqlPlan.CaptureOutputs(result, result.Read());
            if (result.Read()) throw new ProtocolException("Routine output query returned extra rows.");
            CompleteMySqlOutputs();
        }

        private async Task ReadMySqlOutputsAsync(CancellationToken token)
        {
            await _query.SendAsync(OceanBaseCommand.SqlPacket(_mysqlPlan!.OutputSql, 3), readerToken: token).ConfigureAwait(false);
            var result = new QueryResult(_query, completeExchange: false);
            await result.StartAsync(token).ConfigureAwait(false);
            _mysqlPlan.CaptureOutputs(result, await result.ReadAsync(token).ConfigureAwait(false));
            if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("Routine output query returned extra rows.");
            CompleteMySqlOutputs();
        }

        private void CompleteMySqlOutputs()
        {
            _mysqlOutputsPending = _more = false;
            ClearColumns();
            _query.Complete();
        }
    }
}
