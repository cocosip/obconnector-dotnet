using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private const string OracleVersionSql = "select ob_version() from dual";

        internal void EnsureOraclePrepareExecute()
        {
            if (!_session.OraclePrepareExecuteSupported.HasValue)
            {
                Send(OceanBaseCommand.SqlPacket(OracleVersionSql, 3), executesSql: false);
                var result = new QueryResult(this, completeExchange: false); result.Start();
                var version = ReadOracleVersion(result, result.Read());
                if (result.Read()) throw new ProtocolException("The OceanBase version query returned extra rows.");
                _session.OraclePrepareExecuteSupported = SupportsOraclePrepareExecute(version);
            }
            ValidateOraclePrepareExecute();
        }

        internal async Task EnsureOraclePrepareExecuteAsync(CancellationToken token)
        {
            if (!_session.OraclePrepareExecuteSupported.HasValue)
            {
                await SendAsync(OceanBaseCommand.SqlPacket(OracleVersionSql, 3), executesSql: false).ConfigureAwait(false);
                var result = new QueryResult(this, completeExchange: false); await result.StartAsync(token).ConfigureAwait(false);
                var version = ReadOracleVersion(result, await result.ReadAsync(token).ConfigureAwait(false));
                if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("The OceanBase version query returned extra rows.");
                _session.OraclePrepareExecuteSupported = SupportsOraclePrepareExecute(version);
            }
            ValidateOraclePrepareExecute();
        }

        private void ValidateOraclePrepareExecute()
        {
            if (!_session.UseOraclePrepareExecute || Mode != OceanBaseCompatibilityMode.Oracle || _session.OraclePrepareExecuteSupported != true)
                throw new NotSupportedException("Oracle PrepareExecute requires explicit opt-in and OceanBase version 2.2.76 or later.");
        }

        private static string? ReadOracleVersion(QueryResult result, bool hasRow)
        {
            if (result.Columns.Length != 1 || !hasRow || result.Columns[0].TypeCode != 15 && result.Columns[0].TypeCode != 253 && result.Columns[0].TypeCode != 254)
                throw new ProtocolException("The OceanBase version query requires one ordinary string column and one row.");
            var bytes = result.Row![0];
            if (bytes == null) return null;
            if (bytes.Length > 128) throw new ProtocolException("The OceanBase version value is excessive.");
            return ResultColumn.Utf8.GetString(bytes);
        }

        private static bool SupportsOraclePrepareExecute(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var c in value!) if (!(c >= '0' && c <= '9' || c == '.')) return false;
            return Version.TryParse(value, out var version) && version.Build >= 0 && version >= new Version(2, 2, 76);
        }
    }
}
