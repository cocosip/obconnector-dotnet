using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private const string SessionTimeZoneSql = "SELECT SESSIONTIMEZONE FROM DUAL";

        private bool HasResolvedTimeZone()
        {
            if (Mode != OceanBaseCompatibilityMode.Oracle) return true;
            try { OceanBaseTimestampLocalTimeZone.ValidateTimeZone(_session.SessionTimeZone); return true; }
            catch (ArgumentException) { return false; }
        }

        internal string? EnsureSessionTimeZone()
        {
            if (HasResolvedTimeZone()) return _session.SessionTimeZone;
            _session.TakeSessionTimeZone(_lease);
            Send(OceanBaseCommand.SqlPacket(SessionTimeZoneSql, 3), executesSql: false);
            var result = new QueryResult(this, completeExchange: false);
            result.Start();
            var zone = ReadTimeZone(result, result.Read());
            if (result.Read()) throw new ProtocolException("The session time zone query returned extra rows.");
            return PublishTimeZone(zone);
        }

        internal async Task<string?> EnsureSessionTimeZoneAsync(CancellationToken token)
        {
            if (HasResolvedTimeZone()) return _session.SessionTimeZone;
            _session.TakeSessionTimeZone(_lease);
            await SendAsync(OceanBaseCommand.SqlPacket(SessionTimeZoneSql, 3), executesSql: false).ConfigureAwait(false);
            var result = new QueryResult(this, completeExchange: false);
            await result.StartAsync(token).ConfigureAwait(false);
            var zone = ReadTimeZone(result, await result.ReadAsync(token).ConfigureAwait(false));
            if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("The session time zone query returned extra rows.");
            return PublishTimeZone(zone);
        }

        private static string ReadTimeZone(QueryResult result, bool hasRow)
        {
            if (result.Columns.Length != 1 || result.Columns[0].FieldType != typeof(string) || !hasRow)
                throw new ProtocolException("The session time zone query requires one string column and one row.");
            var bytes = result.Row![0];
            if (bytes == null || bytes.Length == 0 || bytes.Length > 255)
                throw new ProtocolException("Invalid session time zone query value.");
            var zone = ResultColumn.Utf8.GetString(bytes);
            OceanBaseTimestampLocalTimeZone.ValidateTimeZone(zone);
            return zone;
        }

        private string PublishTimeZone(string zone)
        {
            if (_session.SessionTimeZone != null && !string.Equals(_session.SessionTimeZone, zone, StringComparison.Ordinal))
            {
                _session.TakeSessionTimeZone(_lease);
                throw new ProtocolException("The session time zone report conflicts with the queried value.");
            }
            _session.ApplySessionVariables(_lease, new Dictionary<string, string> { ["time_zone"] = zone });
            return zone;
        }
    }
}
