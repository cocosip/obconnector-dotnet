using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class SequentialRow
    {
        private async Task MaterializeComplex(bool async, CancellationToken token)
        {
            var reader = new ComplexFieldReader(_payload, _scratch, async, token);
            _materialized = await reader.Read(_columns[_ordinal].ComplexType!.Layout).ConfigureAwait(false);
            _length = _materialized.Length; _remaining = 0;
        }

        // A complex field has no outer length. Copy only its recursively framed bytes, never the row tail.
        private sealed class ComplexFieldReader
        {
            private readonly CommandExchange.CommandPayload _payload;
            private readonly byte[] _scratch;
            private readonly bool _async;
            private readonly CancellationToken _token;
            private readonly MemoryStream _bytes = new MemoryStream();
            private int _budget = ComplexValueCodec.MaximumMembers;
            internal ComplexFieldReader(CommandExchange.CommandPayload payload, byte[] scratch, bool async, CancellationToken token)
            { _payload = payload; _scratch = scratch; _async = async; _token = token; }
            internal async Task<byte[]> Read(ComplexType type)
            {
                using (_bytes) { await ReadType(type, 0).ConfigureAwait(false); return _bytes.ToArray(); }
            }
            private async Task ReadType(ComplexType type, int depth)
            {
                if (depth >= 32) throw _payload.Fault("Complex nesting exceeds the supported depth.");
                var count = type.IsCollection ? await Length().ConfigureAwait(false) : type.Members.Count;
                if (count > _budget) throw _payload.Fault("Complex member count exceeds the supported limit.");
                _budget -= (int)count;
                var bitmap = new byte[((int)count + 9) / 8];
                for (var i = 0; i < bitmap.Length; i++) bitmap[i] = (byte)await Byte().ConfigureAwait(false);
                for (var i = 0; i < count; i++)
                {
                    if ((bitmap[(i + 2) / 8] & 1 << ((i + 2) % 8)) != 0) continue;
                    var member = type.Members[type.IsCollection ? 0 : i];
                    if (member.TypeCode == 160) { await ReadType(member, depth + 1).ConfigureAwait(false); continue; }
                    var length = await Length().ConfigureAwait(false);
                    if (length > int.MaxValue || member.TypeCode == 0 && length > 512) throw _payload.Fault("Invalid complex scalar length.");
                    if (member.TypeCode == 12 || member.TypeCode == 202) TemporalCodec.ValidateFieldLength(member.TypeCode, length);
                    CheckBudget(length);
                    while (length > 0)
                    {
                        var take = (int)Math.Min(length, _scratch.Length);
                        var read = _async ? await _payload.ReadAsync(_scratch, 0, take, _token).ConfigureAwait(false) : _payload.Read(_scratch, 0, take);
                        if (read == 0) throw _payload.Fault("Truncated complex field.");
                        _bytes.Write(_scratch, 0, read); length -= read;
                    }
                }
            }
            private void CheckBudget(long count)
            {
                if (count > PacketChannel.DefaultMaxPacketSize - 10 - _bytes.Length || count > _payload.RemainingLimit)
                    throw _payload.Fault("Complex field exceeds the supported byte limit.");
            }
            private async Task<int> Byte()
            {
                CheckBudget(1);
                var value = _async ? await _payload.ReadByteAsync(_token).ConfigureAwait(false) : _payload.ReadByte();
                _bytes.WriteByte((byte)value); return value;
            }
            private async Task<long> Length()
            {
                var first = await Byte().ConfigureAwait(false);
                if (first == 251 || first == 255) throw _payload.Fault("Invalid complex length marker.");
                var width = first == 252 ? 2 : first == 253 ? 3 : first == 254 ? 8 : 0;
                if (width == 0) return first;
                ulong value = 0;
                for (var i = 0; i < width; i++) value |= (ulong)await Byte().ConfigureAwait(false) << (8 * i);
                if (value > long.MaxValue) throw _payload.Fault("Complex length exceeds Int64.");
                return (long)value;
            }
        }
    }
}
