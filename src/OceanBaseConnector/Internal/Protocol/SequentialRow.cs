using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class SequentialRow
    {
        private readonly CommandExchange.CommandPayload _payload;
        private readonly ResultColumn[] _columns;
        private readonly bool _binary;
        private readonly byte[] _nulls;
        private readonly byte[] _scratch;
        private int _ordinal = -1;
        private long _length;
        private long _remaining;
        private byte[]? _materialized;
        private int _access; // 0 untouched, 1 bytes, 2 characters, 3 explicit whole value.
        private bool _closed;
        private FieldTextReader? _text;

        internal SequentialRow(CommandExchange.CommandPayload payload, ResultColumn[] columns, bool binary, byte[] scratch)
        { _payload = payload; _columns = columns; _binary = binary; _scratch = scratch; _nulls = binary ? new byte[(columns.Length + 9) / 8] : Array.Empty<byte>(); }

        internal void Start()
        {
            if (!_binary) return;
            if (_payload.ReadByte() != 0) throw _payload.Fault("Invalid binary row header.");
            _payload.ReadExactly(_nulls); ValidateNulls();
        }

        internal async Task StartAsync(CancellationToken token)
        {
            if (!_binary) return;
            if (await _payload.ReadByteAsync(token).ConfigureAwait(false) != 0) throw _payload.Fault("Invalid binary row header.");
            await _payload.ReadExactlyAsync(_nulls, token).ConfigureAwait(false); ValidateNulls();
        }

        private void ValidateNulls() { if ((_nulls[0] & 3) != 0) throw _payload.Fault("Invalid binary row reserved bits."); }

        internal void EnsureCurrent(int ordinal)
        {
            if (_closed || ordinal != _ordinal) throw new InvalidOperationException("The sequential field is no longer current.");
            _payload.EnsureActive();
        }

        private void ValidateOrdinal(int ordinal)
        {
            if (_closed) throw new InvalidOperationException("The sequential row is no longer current.");
            if (ordinal < _ordinal) throw new InvalidOperationException("SequentialAccess cannot revisit an earlier column.");
        }

        internal void MoveTo(int ordinal)
        {
            ValidateOrdinal(ordinal);
            while (_ordinal < ordinal)
            {
                Skip(_remaining); NextColumn();
                try { SetLength(FieldLength()); }
                catch (Exception error) when (error is ProtocolException || error is NotSupportedException)
                { throw _payload.Fault("Invalid or unsupported sequential field framing."); }
            }
        }

        internal async Task MoveToAsync(int ordinal, CancellationToken token)
        {
            ValidateOrdinal(ordinal);
            while (_ordinal < ordinal)
            {
                await SkipAsync(_remaining, token).ConfigureAwait(false); NextColumn();
                try { SetLength(await FieldLengthAsync(token).ConfigureAwait(false)); }
                catch (Exception error) when (error is ProtocolException || error is NotSupportedException)
                { throw _payload.Fault("Invalid or unsupported sequential field framing."); }
            }
        }

        private void NextColumn() { _ordinal++; _materialized = null; _access = 0; _text = null; }
        private bool IsBinaryNull => _binary && (_nulls[(_ordinal + 2) / 8] & (1 << ((_ordinal + 2) % 8))) != 0;
        private void SetLength(long length)
        {
            if (length > _payload.RemainingLimit) throw _payload.Fault("Row field length exceeds the remaining packet limit.");
            _length = length; _remaining = Math.Max(0, length);
        }

        private long FieldLength()
        {
            var column = _columns[_ordinal]; _ = column.FieldType;
            if (IsBinaryNull) return -1;
            if (!_binary) return LengthEncoded(_payload.ReadByte(), nullable: true);
            if (column.IsTemporal)
            {
                var length = column.TypeCode == 200 ? LengthEncoded(_payload.ReadByte(), false) : _payload.ReadByte();
                try { TemporalCodec.ValidateFieldLength(column.TypeCode, length); }
                catch (ProtocolException) { throw _payload.Fault("Invalid binary temporal field length."); }
                return length;
            }
            var fixedLength = column.BinaryFixedLength;
            return fixedLength < 0 ? LengthEncoded(_payload.ReadByte(), false) : fixedLength;
        }

        private async Task<long> FieldLengthAsync(CancellationToken token)
        {
            var column = _columns[_ordinal]; _ = column.FieldType;
            if (IsBinaryNull) return -1;
            if (!_binary) return await LengthEncodedAsync(await _payload.ReadByteAsync(token).ConfigureAwait(false), true, token).ConfigureAwait(false);
            if (column.IsTemporal)
            {
                var length = column.TypeCode == 200
                    ? await LengthEncodedAsync(await _payload.ReadByteAsync(token).ConfigureAwait(false), false, token).ConfigureAwait(false)
                    : await _payload.ReadByteAsync(token).ConfigureAwait(false);
                try { TemporalCodec.ValidateFieldLength(column.TypeCode, length); }
                catch (ProtocolException) { throw _payload.Fault("Invalid binary temporal field length."); }
                return length;
            }
            var fixedLength = column.BinaryFixedLength;
            return fixedLength < 0 ? await LengthEncodedAsync(await _payload.ReadByteAsync(token).ConfigureAwait(false), false, token).ConfigureAwait(false) : fixedLength;
        }

        private int LengthWidth(int first, bool nullable)
        {
            if (first == 255 || first == 251 && !nullable) throw _payload.Fault("Invalid row field length marker.");
            return first == 252 ? 2 : first == 253 ? 3 : first == 254 ? 8 : 0;
        }

        private long LengthEncoded(int first, bool nullable)
        {
            var width = LengthWidth(first, nullable);
            if (width == 0) return first == 251 ? -1 : first;
            ulong value = 0;
            for (var i = 0; i < width; i++) value |= (ulong)_payload.ReadByte() << (i * 8);
            return CheckedLength(value);
        }

        private async Task<long> LengthEncodedAsync(int first, bool nullable, CancellationToken token)
        {
            var width = LengthWidth(first, nullable);
            if (width == 0) return first == 251 ? -1 : first;
            ulong value = 0;
            for (var i = 0; i < width; i++) value |= (ulong)await _payload.ReadByteAsync(token).ConfigureAwait(false) << (i * 8);
            return CheckedLength(value);
        }

        private long CheckedLength(ulong value)
        {
            if (value > long.MaxValue) throw _payload.Fault("Row field length exceeds Int64.");
            return (long)value;
        }

        internal bool IsNull(int ordinal) { MoveTo(ordinal); return _length < 0; }
        internal async Task<bool> IsNullAsync(int ordinal, CancellationToken token)
        { await MoveToAsync(ordinal, token).ConfigureAwait(false); return _length < 0; }

        private void StartMaterialize()
        {
            if (_access == 1 || _access == 2) throw new InvalidOperationException("A partially accessed sequential field cannot be materialized.");
            _access = 3;
            if (_length >= 0 && _materialized == null) _materialized = new byte[(int)_length];
        }

        internal byte[]? Materialize(int ordinal)
        {
            MoveTo(ordinal); StartMaterialize();
            while (_remaining != 0) ReadBytes(_materialized!, (int)(_length - _remaining), (int)_remaining);
            return _materialized;
        }

        internal async Task<byte[]?> MaterializeAsync(int ordinal, CancellationToken token)
        {
            await MoveToAsync(ordinal, token).ConfigureAwait(false); StartMaterialize();
            while (_remaining != 0) await ReadBytesAsync(_materialized!, (int)(_length - _remaining), (int)_remaining, token).ConfigureAwait(false);
            return _materialized;
        }

        private int ReadBytes(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0 || count == 0) return 0;
            var read = _payload.Read(buffer, offset, (int)Math.Min(count, _remaining));
            if (read == 0) throw _payload.Fault("Truncated row field.");
            _remaining -= read; return read;
        }

        private async Task<int> ReadBytesAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (_remaining == 0 || count == 0) return 0;
            var read = await _payload.ReadAsync(buffer, offset, (int)Math.Min(count, _remaining), token).ConfigureAwait(false);
            if (read == 0) throw _payload.Fault("Truncated row field.");
            _remaining -= read; return read;
        }

        private void Skip(long count)
        {
            while (count > 0) count -= ReadBytes(_scratch, 0, (int)Math.Min(_scratch.Length, count));
        }

        private async Task SkipAsync(long count, CancellationToken token)
        {
            while (count > 0) count -= await ReadBytesAsync(_scratch, 0, (int)Math.Min(_scratch.Length, count), token).ConfigureAwait(false);
        }

        internal void Finish()
        {
            MoveTo(_columns.Length - 1); Skip(_remaining);
            if (_payload.Read(_scratch, 0, 1) != 0) throw _payload.Fault("Trailing row bytes.");
            Invalidate();
        }

        internal async Task FinishAsync(CancellationToken token)
        {
            await MoveToAsync(_columns.Length - 1, token).ConfigureAwait(false); await SkipAsync(_remaining, token).ConfigureAwait(false);
            if (await _payload.ReadAsync(_scratch, 0, 1, token).ConfigureAwait(false) != 0) throw _payload.Fault("Trailing row bytes.");
            Invalidate();
        }

        internal void Invalidate() { _closed = true; _materialized = null; _payload.Dispose(); }
    }
}
