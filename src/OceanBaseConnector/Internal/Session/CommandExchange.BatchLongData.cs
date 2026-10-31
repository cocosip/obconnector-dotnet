using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        internal void SendBatchLongData(uint statement, OceanBaseParameter[] parameters, int executionBytes)
        {
            if (Mode != OceanBaseCompatibilityMode.MySql)
                throw new NotSupportedException("Batch long-data packets require MySQL mode.");
            VerifyClientCharacterSet(24, false, CancellationToken.None).GetAwaiter().GetResult();
            FlushBatchStatementCloses();
            byte[]? packet = null;
            long remaining = PacketChannel.DefaultMaxPacketSize - executionBytes;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!(parameters[i].Value is Stream source)) continue;
                packet ??= new byte[8199];
                LongDataHeader(packet, statement, i);
                var first = true;
                while (true)
                {
                    EnsureReading();
                    var count = ReadSource(source, packet, 7, 8192);
                    EnsureReading();
                    CheckLongDataRead(count, ref remaining);
                    if (count == 0 && !first) break;
                    BeginSend(false); WritePacket(packet, count + 7); CompleteBatchNoResponse();
                    if (count == 0) break;
                    first = false;
                }
            }
        }

        internal async Task SendBatchLongDataAsync(uint statement, OceanBaseParameter[] parameters, int executionBytes, CancellationToken token)
        {
            if (Mode != OceanBaseCompatibilityMode.MySql)
                throw new NotSupportedException("Batch long-data packets require MySQL mode.");
            using var registration = token.Register(Cancel);
            await VerifyClientCharacterSet(24, true, token).ConfigureAwait(false);
            await FlushBatchStatementClosesAsync().ConfigureAwait(false);
            byte[]? packet = null;
            long remaining = PacketChannel.DefaultMaxPacketSize - executionBytes;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!(parameters[i].Value is Stream source)) continue;
                packet ??= new byte[8199];
                LongDataHeader(packet, statement, i);
                var first = true;
                while (true)
                {
                    EnsureReading();
                    var count = await ReadSourceAsync(source, packet, 7, 8192).ConfigureAwait(false);
                    EnsureReading();
                    CheckLongDataRead(count, ref remaining);
                    if (count == 0 && !first) break;
                    BeginSend(false); await WritePacketAsync(packet, count + 7).ConfigureAwait(false); CompleteBatchNoResponse();
                    if (count == 0) break;
                    first = false;
                }
            }
        }

        private void FlushBatchStatementCloses()
        {
            foreach (var id in _statementCloses)
            { BeginSend(false); WritePacket(PreparedStatement.ClosePacket(id)); CompleteBatchNoResponse(); }
            _statementCloses = System.Array.Empty<uint>();
        }

        private async Task FlushBatchStatementClosesAsync()
        {
            foreach (var id in _statementCloses)
            { BeginSend(false); await WritePacketAsync(PreparedStatement.ClosePacket(id)).ConfigureAwait(false); CompleteBatchNoResponse(); }
            _statementCloses = System.Array.Empty<uint>();
        }

        private void CompleteBatchNoResponse()
        {
            lock (_gate)
            {
                _channel.CompleteResponse(expectsResponse: false);
                _responsePending = _batchPendingResponses != 0;
            }
        }
    }
}
