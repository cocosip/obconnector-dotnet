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
        { EnsureOracleVersion(); ValidateOraclePrepareExecute(); }

        internal async Task EnsureOraclePrepareExecuteAsync(CancellationToken token)
        { await EnsureOracleVersionAsync(token).ConfigureAwait(false); ValidateOraclePrepareExecute(); }

        internal void EnsureCursorOffset()
        { EnsureOracleVersion(); ValidateCursorOffset(); }

        internal async Task EnsureCursorOffsetAsync(CancellationToken token)
        { await EnsureOracleVersionAsync(token).ConfigureAwait(false); ValidateCursorOffset(); }

        private void EnsureOracleVersion()
        {
            if (!_session.OracleVersionLoaded)
            {
                Send(OceanBaseCommand.SqlPacket(OracleVersionSql, 3), executesSql: false);
                var result = new QueryResult(this, completeExchange: false); result.Start();
                var version = ReadOracleVersion(result, result.Read());
                if (result.Read()) throw new ProtocolException("The OceanBase version query returned extra rows.");
                _session.OracleVersion = ParseOracleVersion(version);
                _session.OracleVersionLoaded = true;
            }
        }

        private async Task EnsureOracleVersionAsync(CancellationToken token)
        {
            if (!_session.OracleVersionLoaded)
            {
                await SendAsync(OceanBaseCommand.SqlPacket(OracleVersionSql, 3), executesSql: false).ConfigureAwait(false);
                var result = new QueryResult(this, completeExchange: false); await result.StartAsync(token).ConfigureAwait(false);
                var version = ReadOracleVersion(result, await result.ReadAsync(token).ConfigureAwait(false));
                if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("The OceanBase version query returned extra rows.");
                _session.OracleVersion = ParseOracleVersion(version);
                _session.OracleVersionLoaded = true;
            }
        }

        private void ValidateOraclePrepareExecute()
        {
            if (!_session.UseOraclePrepareExecute || Mode != OceanBaseCompatibilityMode.Oracle || _session.OracleVersion == null || _session.OracleVersion < new Version(2, 2, 76))
                throw new NotSupportedException("Oracle PrepareExecute requires explicit opt-in and OceanBase version 2.2.76 or later.");
        }

        private void ValidateCursorOffset()
        {
            if (Mode != OceanBaseCompatibilityMode.Oracle || _session.OracleVersion == null || _session.OracleVersion < new Version(2, 2, 5))
                throw new NotSupportedException("Offset fetch requires OceanBase Oracle mode version 2.2.5 or later.");
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

        private static Version? ParseOracleVersion(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            foreach (var c in value!) if (!(c >= '0' && c <= '9' || c == '.')) return null;
            return Version.TryParse(value, out var version) && version.Build >= 0 ? version : null;
        }
    }
}
