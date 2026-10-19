using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class SequentialRow
    {
        private char[]? _charSkip;
        private void SetAccess(int access)
        {
            if (_length < 0) throw new InvalidCastException("The field is NULL.");
            if (_access != 0 && _access != access) throw new InvalidOperationException("SequentialAccess cannot mix whole-value, byte and character access on a field.");
            _access = access;
        }

        internal long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int count)
        {
            ValidateBuffer(offset, buffer, bufferOffset, count); MoveTo(ordinal);
            if (_length < 0) throw new InvalidCastException("The field is NULL.");
            if (offset > _length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer == null) return _length;
            SetAccess(1);
            var position = _length - _remaining;
            if (offset < position) throw new InvalidOperationException("SequentialAccess cannot rewind a field.");
            Skip(offset - position);
            var total = 0;
            while (total < count && _remaining != 0) total += ReadBytes(buffer, bufferOffset + total, count - total);
            return total;
        }

        internal Stream GetStream(int ordinal) { MoveTo(ordinal); SetAccess(1); return new FieldStream(this, ordinal); }
        internal TextReader GetTextReader(int ordinal) { MoveTo(ordinal); SetAccess(2); return _text ??= new FieldTextReader(this, ordinal); }

        internal long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int count)
        {
            ValidateBuffer(offset, buffer, bufferOffset, count);
            if (buffer == null) throw new NotSupportedException("A sequential character length probe requires buffering; use GetTextReader instead.");
            var text = (FieldTextReader)GetTextReader(ordinal);
            if (offset < text.Position) throw new InvalidOperationException("SequentialAccess cannot rewind a field.");
            while (text.Position < offset)
            {
                var scratch = _charSkip ??= new char[1024];
                var read = text.Read(scratch, 0, (int)Math.Min(scratch.Length, offset - text.Position));
                if (read == 0) throw new ArgumentOutOfRangeException(nameof(offset));
            }
            var total = 0; int size;
            while (total < count && (size = text.Read(buffer, bufferOffset + total, count - total)) != 0) total += size;
            return total;
        }

        private static void ValidateBuffer<T>(long offset, T[]? buffer, int bufferOffset, int count)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (bufferOffset < 0 || count < 0 || buffer != null && (bufferOffset > buffer.Length || count > buffer.Length - bufferOffset))
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        }

        private sealed class FieldStream : Stream
        {
            private readonly SequentialRow _row;
            private readonly int _ordinal;
            private bool _disposed;
            internal FieldStream(SequentialRow row, int ordinal) { _row = row; _ordinal = ordinal; }
            public override bool CanRead => !_disposed && !_row._closed && _ordinal == _row._ordinal;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            private void Validate(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                ValidateBuffer(0, buffer, offset, count);
                if (_disposed) throw new ObjectDisposedException(nameof(FieldStream));
                _row.EnsureCurrent(_ordinal);
            }
            public override int Read(byte[] buffer, int offset, int count) { Validate(buffer, offset, count); return _row.ReadBytes(buffer, offset, count); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { Validate(buffer, offset, count); _row._payload.CheckCancellation(token); return _row.ReadBytesAsync(buffer, offset, count, token); }
            protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class FieldTextReader : TextReader
        {
            private readonly SequentialRow _row;
            private readonly int _ordinal;
            private readonly StreamReader _inner;
            private readonly char[] _one = new char[1];
            private bool _disposed;
            internal FieldTextReader(SequentialRow row, int ordinal)
            { _row = row; _ordinal = ordinal; _inner = new StreamReader(new FieldStream(row, ordinal), row._columns[ordinal].TextEncoding, false, 1024); }
            internal long Position { get; private set; }
            private void Validate()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FieldTextReader));
                _row.EnsureCurrent(_ordinal);
            }
            public override int Peek() { Validate(); return _inner.Peek(); }
            public override int Read() => Read(_one, 0, 1) == 0 ? -1 : _one[0];
            public override int Read(char[] buffer, int index, int count)
            { Validate(); var read = _inner.Read(buffer, index, count); Position += read; return read; }
            public override async Task<int> ReadAsync(char[] buffer, int index, int count)
            { Validate(); var read = await _inner.ReadAsync(buffer, index, count).ConfigureAwait(false); Position += read; return read; }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            {
                Validate(); _row._payload.CheckCancellation(cancellationToken);
                using var registration = _row._payload.RegisterCancellation(cancellationToken);
                var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _row._payload.CheckCancellation(cancellationToken); Position += read; return read;
            }
#endif
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); _disposed = true; base.Dispose(disposing); }
        }
    }
}
