using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal static partial class HandshakeFlow
    {
        private static bool NeedsIdentityProbe(InitialHandshakePacket greeting, ConnectResult result) =>
            !greeting.IsOceanBaseServer || result.Mode == OceanBaseCompatibilityMode.MySql;

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
            channel.BeginCommand();
            channel.WritePacket(OceanBaseCommand.SqlPacket("select ob_version() from dual", 3));
            ValidateIdentityHeader(channel.ReadPacket());
            ValidateIdentityColumn(channel.ReadPacket(), result.Mode);
            if (!result.Capabilities.HasEffective(CapabilityFlags.ClientDeprecateEof)) ValidateIdentityEnd(channel.ReadPacket(), result, columns: true);
            var version = IdentityVersion(channel.ReadPacket());
            ValidateIdentityEnd(channel.ReadPacket(), result);
            channel.CompleteResponse();
            token.ThrowIfCancellationRequested();
            ValidateIdentityVersion(version);
        }

        private static async Task ConfirmIdentityAsync(PacketChannel channel, ConnectResult result, CancellationToken token)
        {
            channel.BeginCommand();
            await channel.WritePacketAsync(OceanBaseCommand.SqlPacket("select ob_version() from dual", 3), token).ConfigureAwait(false);
            ValidateIdentityHeader(await channel.ReadPacketAsync(token).ConfigureAwait(false));
            ValidateIdentityColumn(await channel.ReadPacketAsync(token).ConfigureAwait(false), result.Mode);
            if (!result.Capabilities.HasEffective(CapabilityFlags.ClientDeprecateEof)) ValidateIdentityEnd(await channel.ReadPacketAsync(token).ConfigureAwait(false), result, columns: true);
            var version = IdentityVersion(await channel.ReadPacketAsync(token).ConfigureAwait(false));
            ValidateIdentityEnd(await channel.ReadPacketAsync(token).ConfigureAwait(false), result);
            channel.CompleteResponse();
            token.ThrowIfCancellationRequested();
            ValidateIdentityVersion(version);
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

        private static void ValidateIdentityHeader(byte[] packet)
        {
            CheckIdentityError(packet);
            if (packet.Length != 1 || packet[0] != 1) throw new ProtocolException("OceanBase identity requires one result column.");
        }

        private static void ValidateIdentityColumn(byte[] packet, OceanBaseCompatibilityMode mode)
        {
            CheckIdentityError(packet);
            if (packet.Length > 4096) throw new ProtocolException("Excessive OceanBase identity metadata.");
            var column = ResultColumn.Parse(packet, mode);
            if (column.TypeCode != 15 && column.TypeCode != 253 && column.TypeCode != 254)
                throw new ProtocolException("OceanBase identity requires an ordinary string column.");
        }

        private static string? IdentityVersion(byte[] packet)
        {
            CheckIdentityError(packet);
            if (packet.Length > 129) throw new ProtocolException("Excessive OceanBase identity value.");
            var cursor = new ByteCursor(packet);
            var bytes = cursor.ReadNullableLengthEncodedBytes();
            if (cursor.HasRemaining) throw new ProtocolException("Trailing OceanBase identity row bytes.");
            return bytes == null ? null : ResultColumn.Utf8.GetString(bytes);
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

        private static void ValidateIdentityVersion(string? version)
        {
            // Only this real-server version has verified both authenticated mode states.
            if (!string.Equals(version, "4.4.2.2", StringComparison.Ordinal))
                throw new OceanBaseException("OceanBase identity and compatibility mode require a verified server version (currently 4.4.2.2).");
        }
    }
}
