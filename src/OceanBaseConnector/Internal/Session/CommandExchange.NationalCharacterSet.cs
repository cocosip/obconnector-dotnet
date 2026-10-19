using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private const string NationalCharacterSetSql = "SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL";

        internal NationalCharacterSet? EnsureNationalCharacterSet()
        {
            if (Mode != OceanBaseCompatibilityMode.Oracle || _session.NationalCharacterSet != null) return _session.NationalCharacterSet;
            var zone = _session.SessionTimeZone;
            Send(OceanBaseCommand.SqlPacket(NationalCharacterSetSql, 3), executesSql: false);
            var result = new QueryResult(this, completeExchange: false);
            result.Start();
            var value = ReadNationalCharacterSet(result, result.Read());
            if (result.Read()) throw new ProtocolException("The national character set query returned extra rows.");
            return PublishNationalCharacterSet(value, zone);
        }

        internal async Task<NationalCharacterSet?> EnsureNationalCharacterSetAsync(CancellationToken token)
        {
            if (Mode != OceanBaseCompatibilityMode.Oracle || _session.NationalCharacterSet != null) return _session.NationalCharacterSet;
            var zone = _session.SessionTimeZone;
            await SendAsync(OceanBaseCommand.SqlPacket(NationalCharacterSetSql, 3), executesSql: false).ConfigureAwait(false);
            var result = new QueryResult(this, completeExchange: false);
            await result.StartAsync(token).ConfigureAwait(false);
            var value = ReadNationalCharacterSet(result, await result.ReadAsync(token).ConfigureAwait(false));
            if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("The national character set query returned extra rows.");
            return PublishNationalCharacterSet(value, zone);
        }

        private static NationalCharacterSet ReadNationalCharacterSet(QueryResult result, bool hasRow)
        {
            if (result.Columns.Length != 3 || !hasRow) throw new ProtocolException("The national character set query requires three columns and one row.");
            var values = new string?[3];
            for (var i = 0; i < values.Length; i++)
            {
                var column = result.Columns[i];
                if (column.TypeCode != 15 && column.TypeCode != 253 && column.TypeCode != 254)
                    throw new ProtocolException("The national character set query requires ordinary string columns.");
                var bytes = result.Row![i];
                if (bytes == null) continue;
                if (bytes.Length == 0 || bytes.Length > 128) throw new ProtocolException("Invalid session character set name.");
                var name = NationalCharacterSet.EncodingForCollation(column.Charset).GetString(bytes);
                if (name.Length == 0 || name.Length > 64) throw new ProtocolException("Invalid session character set name.");
                foreach (var b in name)
                    if (!(b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z' || b >= '0' && b <= '9' || b == '_'))
                        throw new ProtocolException("Invalid session character set name.");
                values[i] = name;
            }
            if (values[2] == null) throw new ProtocolException("The session connection character set is missing.");
            return new NationalCharacterSet(values[0], values[1], values[2]);
        }

        private NationalCharacterSet PublishNationalCharacterSet(NationalCharacterSet value, string? zone)
        {
            if (_session.SessionTimeZone != null && zone != null && !string.Equals(_session.SessionTimeZone, zone, StringComparison.Ordinal))
            {
                _session.TakeSessionTimeZone(_lease);
                _session.TakeNationalCharacterSet(_lease);
                throw new ProtocolException("The session time zone report changed during the national character set query.");
            }
            var reported = _session.NationalCharacterSet;
            if (reported != null && (!string.Equals(reported.National, value.National, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reported.Results, value.Results, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reported.Connection, value.Connection, StringComparison.OrdinalIgnoreCase)))
            {
                _session.TakeNationalCharacterSet(_lease);
                throw new ProtocolException("The national character set report conflicts with the queried value.");
            }
            _session.ApplySessionVariables(_lease, value.Variables(zone));
            return value;
        }
    }
}
