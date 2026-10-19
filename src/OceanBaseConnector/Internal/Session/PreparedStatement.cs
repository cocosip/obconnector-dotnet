using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class PreparedStatement
    {
        private PreparedStatement(PhysicalSession session, string sql, byte[] header, out int columns)
        {
            var cursor = new ByteCursor(header);
            if (cursor.ReadUInt8() != 0) throw new ProtocolException("Expected a prepared statement response.");
            Id = cursor.ReadUInt32();
            columns = cursor.ReadUInt16();
            ColumnCount = columns;
            ParameterCount = cursor.ReadUInt16();
            if (cursor.ReadUInt8() != 0) throw new ProtocolException("Invalid prepared statement filler.");
            cursor.ReadUInt16();
            if (cursor.HasRemaining || columns > 4096) throw new ProtocolException("Invalid prepared statement metadata counts.");
            Session = session;
            CommandText = sql;
            Database = session.Database;
        }

        internal PhysicalSession Session { get; }
        internal string CommandText { get; }
        internal string Database { get; }
        internal uint Id { get; }
        internal int ParameterCount { get; }
        internal int ColumnCount { get; }

        internal static PreparedStatement? Read(CommandExchange exchange, PhysicalSession session, string sql, bool allowFallback = false)
        {
            var header = exchange.Receive(allowFallback);
            if (ErrPacket.IsErr(header)) return null;
            var statement = new PreparedStatement(session, sql, header, out var columns);
            try
            {
                var bytes = 0;
                foreach (var count in new[] { statement.ParameterCount, columns })
                {
                    for (var i = 0; i < count; i++) bytes = Metadata(exchange.Receive(), exchange.Mode, bytes);
                    if (count > 0 && (exchange.Capabilities & CapabilityFlags.ClientDeprecateEof) == 0)
                        QueryResult.ReadEof(exchange.Receive());
                }
                exchange.ResponseComplete();
                return statement;
            }
            catch { statement.Release(); throw; }
        }

        internal static async Task<PreparedStatement?> ReadAsync(CommandExchange exchange, PhysicalSession session, string sql, CancellationToken token, bool allowFallback = false)
        {
            var header = await exchange.ReceiveAsync(token, allowFallback).ConfigureAwait(false);
            if (ErrPacket.IsErr(header)) return null;
            var statement = new PreparedStatement(session, sql, header, out var columns);
            try
            {
                var bytes = 0;
                foreach (var count in new[] { statement.ParameterCount, columns })
                {
                    for (var i = 0; i < count; i++) bytes = Metadata(await exchange.ReceiveAsync(token).ConfigureAwait(false), exchange.Mode, bytes);
                    if (count > 0 && (exchange.Capabilities & CapabilityFlags.ClientDeprecateEof) == 0)
                        QueryResult.ReadEof(await exchange.ReceiveAsync(token).ConfigureAwait(false));
                }
                exchange.ResponseComplete();
                return statement;
            }
            catch { statement.Release(); throw; }
        }

        private static int Metadata(byte[] packet, OceanBaseCompatibilityMode mode, int bytes)
        {
            if (packet.Length > 4 * 1024 * 1024 - bytes) throw new ProtocolException("Prepared statement metadata size limit exceeded.");
            ResultColumn.Parse(packet, mode);
            return bytes + packet.Length;
        }

        internal void Release() => Session.QueueStatementClose(Id);
        internal static byte[] ClosePacket(uint id) => new byte[] { 25, (byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24) };

        // A busy session flushes queued closes before its next command, never into another reader's response.
        internal static void CloseIdle(PhysicalSession session, int timeout)
        {
            using var budget = CloseBudget(timeout);
            if (budget == null) return;
            using var lease = session.TryAcquire();
            if (lease == null) return;
            using var cancellation = budget.Token.Register(lease.Dispose);
            try
            {
                foreach (var id in session.TakeStatementCloses())
                { budget.Token.ThrowIfCancellationRequested(); lease.Channel.BeginCommand(); lease.Channel.WritePacket(ClosePacket(id)); lease.Channel.CompleteResponse(expectsResponse: false); }
                lease.CompleteIfActive();
            }
            catch { session.Break(); }
        }

        internal static async Task CloseIdleAsync(PhysicalSession session, int timeout)
        {
            using var budget = CloseBudget(timeout);
            if (budget == null) return;
            using var lease = session.TryAcquire();
            if (lease == null) return;
            using var cancellation = budget.Token.Register(lease.Dispose);
            try
            {
                foreach (var id in session.TakeStatementCloses())
                { lease.Channel.BeginCommand(); await lease.Channel.WritePacketAsync(ClosePacket(id), budget.Token).ConfigureAwait(false); lease.Channel.CompleteResponse(expectsResponse: false); }
                lease.CompleteIfActive();
            }
            catch { session.Break(); }
        }

        private static CancellationTokenSource? CloseBudget(int timeout)
        {
            var budget = new CancellationTokenSource();
            try { if (timeout != 0) budget.CancelAfter(TimeSpan.FromSeconds(timeout)); return budget; }
            catch (ArgumentOutOfRangeException) { budget.Dispose(); return null; }
        }
    }
}
