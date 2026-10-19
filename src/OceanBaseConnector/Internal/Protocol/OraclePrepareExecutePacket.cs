using System;
using System.IO;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class OraclePrepareExecutePacket
    {
        private OraclePrepareExecutePacket(ByteCursor cursor)
        {
            if (cursor.ReadUInt8() != 0) throw new ProtocolException("Expected an Oracle PrepareExecute header.");
            StatementId = cursor.ReadUInt32();
            ColumnCount = cursor.ReadUInt16();
            ParameterCount = cursor.ReadUInt16();
            if (cursor.ReadUInt8() != 0) throw new ProtocolException("Invalid Oracle PrepareExecute filler.");
            WarningCount = cursor.ReadUInt16();
            ExtendFlags = cursor.ReadUInt32();
            var hasResultSet = cursor.ReadUInt8();
            if (hasResultSet > 1 || cursor.HasRemaining || ColumnCount > 4096)
                throw new ProtocolException("Invalid Oracle PrepareExecute metadata header.");
            HasResultSet = hasResultSet != 0;
        }

        internal uint StatementId { get; }
        internal int ColumnCount { get; }
        internal int ParameterCount { get; }
        internal ushort WarningCount { get; }
        internal uint ExtendFlags { get; }
        internal bool HasResultSet { get; }

        // This is the extended metadata header; execution still requires its results and final OK/ERR.
        internal static OraclePrepareExecutePacket ParseHeader(byte[] bytes) => new OraclePrepareExecutePacket(new ByteCursor(bytes));

        internal static byte[] Build(uint statementId, string sql, int parameterCount, byte[] parameters)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if ((uint)parameterCount > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(parameterCount));
            if (parameterCount == 0 ? parameters.Length != 0 : parameters.Length < (parameterCount + 7) / 8 + 1 + parameterCount * 2)
                throw new ArgumentException("The encoded Oracle parameter block does not match its count.", nameof(parameters));
            var sqlLength = ResultColumn.Utf8.GetByteCount(sql);
            var prefix = sqlLength < 251 ? 1 : sqlLength <= ushort.MaxValue ? 3 : sqlLength <= 0xffffff ? 4 : 9;
            var length = 30L + prefix + sqlLength + parameters.Length;
            if (length > PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Oracle PrepareExecute exceeds the packet size limit.");

            var text = ResultColumn.Utf8.GetBytes(sql);
            var packet = new byte[(int)length];
            using var stream = new MemoryStream(packet);
            using var writer = new BinaryWriter(stream, ResultColumn.Utf8);
            writer.Write((byte)0xa1);
            writer.Write(statementId);
            writer.Write((byte)0); // Procedure/RETURNING execution does not request a fetch cursor.
            writer.Write(1u);
            PreparedParameters.WriteBytes(writer, text);
            writer.Write((uint)parameterCount);
            writer.Write(parameters);
            writer.Write(0u); // Default execution mode; array binding is a separate extension.
            writer.Write(0u); // No bundled statement closes.
            writer.Write(Ob20Checksum.Payload(0, text, 0, text.Length));
            writer.Write(0u);
            return packet;
        }
    }
}
