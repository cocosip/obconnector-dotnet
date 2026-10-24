using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal static partial class HandshakeFlow
    {
        private const string OracleIdentitySql = "select ob_version(), @@ob_compatibility_mode from dual";
        private const string MySqlIdentitySql = "select ob_version(), @@ob_compatibility_mode, @@autocommit from dual";

        private static void EnableOb20(PacketChannel channel, ConnectResult result, OceanBaseConnectionOptions options)
        {
            if (options.UseOb20 && result.Capabilities.HasEffective(CapabilityFlags.ClientConnectAttrs) &&
                (result.ObCapabilityFlag & OceanBaseCapabilityFlags.ObCapObProtocolV2) != 0)
                channel.EnableOb20(result.ConnectionId,
                    options.EnableFullLinkTrace && (result.ObCapabilityFlag & OceanBaseCapabilityFlags.ObCapFullLinkTrace) != 0,
                    (result.ObCapabilityFlag & OceanBaseCapabilityFlags.ObCapNewExtraInfo) != 0);
        }

        private static void ConfirmIdentity(PacketChannel channel, ConnectResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var mysql = result.Mode == OceanBaseCompatibilityMode.MySql;
            var columns = mysql ? 3 : 2;
            channel.BeginCommand();
            channel.WritePacket(OceanBaseCommand.SqlPacket(mysql ? MySqlIdentitySql : OracleIdentitySql, 3));
            ValidateIdentityHeader(channel.ReadPacket(), columns);
            for (var i = 0; i < columns; i++) ValidateIdentityColumn(channel.ReadPacket(), result.Mode, autocommit: mysql && i == 2);
            if (!result.Capabilities.HasEffective(CapabilityFlags.ClientDeprecateEof)) ValidateIdentityEnd(channel.ReadPacket(), result, columns: true);
            var identity = IdentityValues(channel.ReadPacket(), mysql);
            ValidateIdentityEnd(channel.ReadPacket(), result);
            channel.CompleteResponse();
            token.ThrowIfCancellationRequested();
            result.ConfirmIdentity(identity.Version, identity.Mode, identity.Autocommit);
        }

        private static async Task ConfirmIdentityAsync(PacketChannel channel, ConnectResult result, CancellationToken token)
        {
            var mysql = result.Mode == OceanBaseCompatibilityMode.MySql;
            var columns = mysql ? 3 : 2;
            channel.BeginCommand();
            await channel.WritePacketAsync(OceanBaseCommand.SqlPacket(mysql ? MySqlIdentitySql : OracleIdentitySql, 3), token).ConfigureAwait(false);
            ValidateIdentityHeader(await channel.ReadPacketAsync(token).ConfigureAwait(false), columns);
            for (var i = 0; i < columns; i++) ValidateIdentityColumn(await channel.ReadPacketAsync(token).ConfigureAwait(false), result.Mode, autocommit: mysql && i == 2);
            if (!result.Capabilities.HasEffective(CapabilityFlags.ClientDeprecateEof)) ValidateIdentityEnd(await channel.ReadPacketAsync(token).ConfigureAwait(false), result, columns: true);
            var identity = IdentityValues(await channel.ReadPacketAsync(token).ConfigureAwait(false), mysql);
            ValidateIdentityEnd(await channel.ReadPacketAsync(token).ConfigureAwait(false), result);
            channel.CompleteResponse();
            token.ThrowIfCancellationRequested();
            result.ConfirmIdentity(identity.Version, identity.Mode, identity.Autocommit);
        }

        private static void CheckIdentityError(byte[] packet)
        {
            if (packet.Length == 0) throw new ProtocolException("Empty OceanBase identity response.");
            if (ErrPacket.IsErr(packet))
            {
                var error = ErrPacket.Parse(packet);
                throw new OceanBaseException("OceanBase identity could not be verified.", error.ErrorCode, error.SqlState);
            }
        }

        private static void ValidateIdentityHeader(byte[] packet, int columns)
        {
            CheckIdentityError(packet);
            if (packet.Length != 1 || packet[0] != columns) throw new ProtocolException("OceanBase identity requires an exact result column count.");
        }

        private static void ValidateIdentityColumn(byte[] packet, OceanBaseCompatibilityMode mode, bool autocommit)
        {
            CheckIdentityError(packet);
            if (packet.Length > 4096) throw new ProtocolException("Excessive OceanBase identity metadata.");
            var column = ResultColumn.Parse(packet, mode);
            var ordinary = column.TypeCode == 15 || column.TypeCode == 253 || column.TypeCode == 254;
            if (!ordinary && !(autocommit && (column.TypeCode == 1 || column.TypeCode == 8)))
                throw new ProtocolException("OceanBase identity requires an ordinary string column.");
        }

        private static (string Version, string Mode, bool? Autocommit) IdentityValues(byte[] packet, bool autocommit)
        {
            CheckIdentityError(packet);
            if (packet.Length > 144) throw new ProtocolException("Excessive OceanBase identity value.");
            var cursor = new ByteCursor(packet);
            var bytes = cursor.ReadNullableLengthEncodedBytes();
            var mode = cursor.ReadNullableLengthEncodedBytes();
            var setting = autocommit ? cursor.ReadNullableLengthEncodedBytes() : null;
            if (cursor.HasRemaining) throw new ProtocolException("Trailing OceanBase identity row bytes.");
            if (bytes == null || bytes.Length == 0 || bytes.Length > 128 || mode == null || mode.Length > 6)
                throw new OceanBaseException("OceanBase identity requires a version and compatibility mode.");
            bool? enabled = null;
            if (autocommit)
            {
                if (setting == null || setting.Length != 1 || setting[0] != (byte)'0' && setting[0] != (byte)'1')
                    throw new OceanBaseException("OceanBase identity requires an autocommit setting of 0 or 1.");
                enabled = setting[0] == (byte)'1';
            }
            return (ResultColumn.Utf8.GetString(bytes), ResultColumn.Utf8.GetString(mode), enabled);
        }

        private static void ValidateIdentityEnd(byte[] packet, ConnectResult result, bool columns = false)
        {
            CheckIdentityError(packet);
            ServerStatusFlags status;
            if (!columns && result.Capabilities.HasEffective(CapabilityFlags.ClientDeprecateEof))
            {
                var ok = OkPacket.Parse(packet, result.Capabilities.Effective, allowEofHeader: true);
                if (packet[0] != 254 || ok.AffectedRows != 0 || ok.LastInsertId != 0) throw new ProtocolException("Invalid OceanBase identity terminator.");
                status = ok.StatusFlags;
            }
            else status = QueryResult.ReadEof(packet);
            // The Oracle mode bit is an authentication fact; query EOFs may omit it.
            if ((status & ServerStatusFlags.MoreResultsExists) != 0)
                throw new ProtocolException("OceanBase identity returned multiple results.");
        }

    }
}
