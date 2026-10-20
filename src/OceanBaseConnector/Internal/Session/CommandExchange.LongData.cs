using System;
using System.IO;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        internal void SendLongData(uint statement, OceanBaseParameter[] parameters, int executionBytes)
        {
            if (Mode == OceanBaseCompatibilityMode.Oracle) { SendPieceData(statement, parameters, executionBytes); return; }
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
                    var count = source.Read(packet, 7, 8192);
                    EnsureReading();
                    CheckLongDataRead(count, ref remaining);
                    if (count == 0 && !first) break;
                    lock (_gate) _longDataPending = true;
                    Send(packet, executesSql: false, count: count + 7);
                    ResponseComplete(expectsResponse: false);
                    if (count == 0) break;
                    first = false;
                }
            }
        }

        internal async Task SendLongDataAsync(uint statement, OceanBaseParameter[] parameters, int executionBytes)
        {
            if (Mode == OceanBaseCompatibilityMode.Oracle) { await SendPieceDataAsync(statement, parameters, executionBytes).ConfigureAwait(false); return; }
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
                    var count = await source.ReadAsync(packet, 7, 8192, _budget.Token).ConfigureAwait(false);
                    EnsureReading();
                    CheckLongDataRead(count, ref remaining);
                    if (count == 0 && !first) break;
                    lock (_gate) _longDataPending = true;
                    await SendAsync(packet, executesSql: false, count: count + 7).ConfigureAwait(false);
                    ResponseComplete(expectsResponse: false);
                    if (count == 0) break;
                    first = false;
                }
            }
        }

        private static void LongDataHeader(byte[] packet, uint statement, int index)
        {
            packet[0] = 24;
            for (var b = 0; b < 4; b++) packet[b + 1] = (byte)(statement >> (8 * b));
            packet[5] = (byte)index; packet[6] = (byte)(index >> 8);
        }

        private static void CheckLongDataRead(int count, ref long remaining)
        {
            if (count < 0 || count > 8192) throw new IOException("The parameter stream returned an invalid byte count.");
            remaining -= count;
            if (remaining < 0) throw new ArgumentException("Stream parameters exceed the execution byte limit.");
        }
    }
}
