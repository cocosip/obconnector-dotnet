using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal static partial class HandshakeFlow
    {
        internal static string ClientCharacterSetSql(OceanBaseCompatibilityMode mode) =>
            "SELECT @@character_set_client, @@character_set_connection, @@character_set_results, @@collation_connection" +
            (mode == OceanBaseCompatibilityMode.Oracle ? " FROM DUAL" : "");

        internal static string[] ClientCharacterSetInitializationSql(ClientCharacterSet charset, OceanBaseCompatibilityMode mode) =>
            mode == OceanBaseCompatibilityMode.Oracle
                ? new[] { "SET character_set_client = '" + charset.Name + "'", "SET character_set_connection = '" + charset.Name + "'", "SET character_set_results = 'utf8mb4'" }
                : new[] { "SET NAMES " + charset.Name };

        internal static async Task InitializeClientCharacterSet(PacketChannel channel, ConnectResult info, ClientCharacterSet charset, bool asynchronous, CancellationToken token)
        {
            if (!charset.Managed) return;
            foreach (var sql in ClientCharacterSetInitializationSql(charset, info.Mode))
            {
                token.ThrowIfCancellationRequested();
                channel.BeginCommand();
                var request = OceanBaseCommand.SqlPacket(sql, 3);
                if (asynchronous) await channel.WritePacketAsync(request, token).ConfigureAwait(false); else channel.WritePacket(request);
                var packet = asynchronous ? await channel.ReadPacketAsync(token).ConfigureAwait(false) : channel.ReadPacket();
                CheckIdentityError(packet);
                var ok = OkPacket.Parse(packet, info.Capabilities.Effective);
                if (ok.AffectedRows != 0 || ok.LastInsertId != 0 || (ok.StatusFlags & ServerStatusFlags.MoreResultsExists) != 0)
                    throw new ProtocolException("Invalid client character set initialization response.");
                info.ObserveSessionVariables(ok.SessionVariables);
                channel.CompleteResponse();
            }
            channel.BeginCommand();
            var query = OceanBaseCommand.SqlPacket(ClientCharacterSetSql(info.Mode), 3);
            if (asynchronous) await channel.WritePacketAsync(query, token).ConfigureAwait(false); else channel.WritePacket(query);
            async Task<byte[]> Read() => asynchronous ? await channel.ReadPacketAsync(token).ConfigureAwait(false) : channel.ReadPacket();
            ValidateIdentityHeader(await Read().ConfigureAwait(false), 4);
            for (var i = 0; i < 4; i++) ValidateIdentityColumn(await Read().ConfigureAwait(false), info.Mode, false);
            if (!info.Capabilities.HasEffective(CapabilityFlags.ClientDeprecateEof))
                ValidateIdentityEnd(await Read().ConfigureAwait(false), info, columns: true);
            var row = new ByteCursor(await Read().ConfigureAwait(false));
            var values = new string[4];
            for (var i = 0; i < values.Length; i++) values[i] = ReadClientCharacterSetValue(row.ReadNullableLengthEncodedBytes());
            if (row.HasRemaining) throw new ProtocolException("Trailing client character set state bytes.");
            ValidateIdentityEnd(await Read().ConfigureAwait(false), info);
            channel.CompleteResponse();
            ValidateClientCharacterSet(charset, info.Mode, values);
            info.ObserveSessionVariables(new Dictionary<string, string>
            {
                ["character_set_client"] = values[0],
                ["character_set_connection"] = values[1],
                ["character_set_results"] = values[2],
                ["collation_connection"] = values[3],
            });
            token.ThrowIfCancellationRequested();
        }

        internal static string ReadClientCharacterSetValue(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 128) throw new ProtocolException("Invalid client character set state value.");
            foreach (var c in bytes)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_'))
                    throw new ProtocolException("Invalid client character set state value.");
            return ResultColumn.Utf8.GetString(bytes);
        }

        internal static void ValidateClientCharacterSet(ClientCharacterSet charset, OceanBaseCompatibilityMode mode, string[] values)
        {
            if (!charset.Matches(values[0]) || !charset.Matches(values[1]) ||
                (mode == OceanBaseCompatibilityMode.Oracle
                    ? !string.Equals(values[2], "utf8mb4", StringComparison.OrdinalIgnoreCase)
                    : !charset.Matches(values[2])) || !charset.MatchesCollation(values[3]))
                throw new NotSupportedException("The session character sets do not match the configured client encoding.");
        }
    }
}
