using System;
using System.IO;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private void SendPieceData(uint statement, OceanBaseParameter[] parameters, int executionBytes)
        {
            byte[]? raw = null, packet = null;
            long remaining = PacketChannel.DefaultMaxPacketSize - executionBytes;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!(parameters[i].Value is Stream source)) continue;
                raw ??= new byte[8193]; packet ??= new byte[16401];
                var buffered = 0; var first = true;
                while (true)
                {
                    var eof = false;
                    while (buffered < raw.Length)
                    {
                        EnsureReading();
                        var requested = Math.Min(8192, raw.Length - buffered);
                        var read = source.Read(raw, buffered, requested);
                        EnsureReading();
                        CheckPieceRead(read, requested, ref remaining);
                        buffered += read;
                        if (read == 0) { eof = true; break; }
                    }
                    var count = Math.Min(8192, buffered);
                    var length = PieceDataPacket(packet, raw, count, statement, i, eof ? (byte)3 : first ? (byte)1 : (byte)2);
                    lock (_gate) _longDataPending = true;
                    Send(packet, executesSql: false, count: length);
                    FinishPiece(Receive());
                    if (eof) break;
                    raw[0] = raw[8192]; buffered = 1; first = false;
                }
            }
        }

        private async Task SendPieceDataAsync(uint statement, OceanBaseParameter[] parameters, int executionBytes)
        {
            byte[]? raw = null, packet = null;
            long remaining = PacketChannel.DefaultMaxPacketSize - executionBytes;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!(parameters[i].Value is Stream source)) continue;
                raw ??= new byte[8193]; packet ??= new byte[16401];
                var buffered = 0; var first = true;
                while (true)
                {
                    var eof = false;
                    while (buffered < raw.Length)
                    {
                        EnsureReading();
                        var requested = Math.Min(8192, raw.Length - buffered);
                        var read = await source.ReadAsync(raw, buffered, requested, _budget.Token).ConfigureAwait(false);
                        EnsureReading();
                        CheckPieceRead(read, requested, ref remaining);
                        buffered += read;
                        if (read == 0) { eof = true; break; }
                    }
                    var count = Math.Min(8192, buffered);
                    var length = PieceDataPacket(packet, raw, count, statement, i, eof ? (byte)3 : first ? (byte)1 : (byte)2);
                    lock (_gate) _longDataPending = true;
                    await SendAsync(packet, executesSql: false, count: length).ConfigureAwait(false);
                    FinishPiece(await ReceiveAsync(default).ConfigureAwait(false));
                    if (eof) break;
                    raw[0] = raw[8192]; buffered = 1; first = false;
                }
            }
        }

        private static void CheckPieceRead(int count, int requested, ref long remaining)
        {
            if (count < 0 || count > requested) throw new IOException("The parameter stream returned an invalid byte count.");
            remaining -= count * 2;
            if (remaining < 0) throw new ArgumentException("Stream parameters exceed the execution byte limit.");
        }

        private static int PieceDataPacket(byte[] packet, byte[] raw, int count, uint statement, int index, byte piece)
        {
            LongDataHeader(packet, statement, index); packet[0] = 162; packet[7] = piece; packet[8] = 0;
            var length = count * 2;
            for (var b = 0; b < 8; b++) packet[9 + b] = (byte)((ulong)length >> (b * 8));
            const string hex = "0123456789ABCDEF";
            for (var b = 0; b < count; b++)
            { packet[17 + 2 * b] = (byte)hex[raw[b] >> 4]; packet[18 + 2 * b] = (byte)hex[raw[b] & 15]; }
            return 17 + length;
        }

        private void FinishPiece(byte[] packet)
        {
            var ok = OkPacket.Parse(packet, Capabilities);
            if ((ok.StatusFlags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.CursorExists | ServerStatusFlags.LastRowSent | ServerStatusFlags.PsOutParameters)) != 0)
                throw new ProtocolException("Unexpected result flags in a piece acknowledgement.");
            ObserveSessionState(ok);
            ResponseComplete();
        }
    }
}
