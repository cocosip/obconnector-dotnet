using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private const string MySqlLiteralStateSql = "SELECT @@character_set_client, @@character_set_connection, @@collation_connection, @@sql_mode";

        internal MySqlLiteralContext ReadMySqlLiteralContext()
        {
            Send(OceanBaseCommand.SqlPacket(MySqlLiteralStateSql, 3), executesSql: false);
            var result = new QueryResult(this, completeExchange: false);
            result.Start();
            var values = ReadMySqlLiteralState(result, result.Read());
            if (result.Read()) throw new ProtocolException("The MySQL literal state query returned extra rows.");
            return ParseMySqlLiteralContext(values);
        }

        internal async Task<MySqlLiteralContext> ReadMySqlLiteralContextAsync(CancellationToken token)
        {
            await SendAsync(OceanBaseCommand.SqlPacket(MySqlLiteralStateSql, 3), executesSql: false).ConfigureAwait(false);
            var result = new QueryResult(this, completeExchange: false);
            await result.StartAsync(token).ConfigureAwait(false);
            var values = ReadMySqlLiteralState(result, await result.ReadAsync(token).ConfigureAwait(false));
            if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("The MySQL literal state query returned extra rows.");
            return ParseMySqlLiteralContext(values);
        }

        private MySqlLiteralContext ParseMySqlLiteralContext(string[] values)
        {
            if (_session.ClientCharacterSet.Managed && (!_session.ClientCharacterSet.Matches(values[0]) || !_session.ClientCharacterSet.Matches(values[1])))
            {
                _session.SetClientCharacterSetVerified(false);
                throw new System.NotSupportedException("The literal session character sets do not match the configured client encoding.");
            }
            return MySqlLiteralContext.Parse(values[0], values[1], values[2], values[3], _session.ClientCharacterSet);
        }

        private static string[] ReadMySqlLiteralState(QueryResult result, bool hasRow)
        {
            if (result.Columns.Length != 4 || !hasRow) throw new ProtocolException("The MySQL literal state query requires four columns and one row.");
            var values = new string[4];
            for (var i = 0; i < values.Length; i++)
            {
                var column = result.Columns[i];
                var bytes = result.Row![i];
                if (column.TypeCode != 15 && column.TypeCode != 253 && column.TypeCode != 254 || bytes == null || bytes.Length > 4096)
                    throw new ProtocolException("Invalid MySQL literal session state value.");
                values[i] = column.TextEncoding.GetString(bytes);
            }
            return values;
        }
    }
}
