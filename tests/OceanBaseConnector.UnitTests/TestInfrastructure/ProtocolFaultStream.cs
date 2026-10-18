using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    // Deterministic transport faults after an authenticated loopback connection is established.
    internal sealed class ProtocolFaultStream : Stream
    {
        private readonly byte[] _input;
        private readonly MemoryStream _output = new MemoryStream();
        private int _position;
        private int _writeLimit;
        private bool _disposed;
        internal ProtocolFaultStream(byte[] input, int writeLimit = int.MaxValue) { _input = input; _writeLimit = writeLimit; }
        internal Action? AfterInputConsumed { get; set; }
        internal bool PauseAfterPartialWrite { get; set; }
        internal TaskCompletionSource PartialWriteStarted { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Written => _output.ToArray();
        internal int SyncCalls { get; private set; }
        internal int AsyncCalls { get; private set; }
        internal bool Disposed => _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanWrite => !_disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int ReadCore(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProtocolFaultStream));
            var length = Math.Min(count, _input.Length - _position);
            Array.Copy(_input, _position, buffer, offset, length); _position += length;
            if (length != 0 && _position == _input.Length)
            {
                var callback = AfterInputConsumed; AfterInputConsumed = null; callback?.Invoke();
            }
            return length;
        }
        private bool WriteCore(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProtocolFaultStream));
            var length = Math.Min(count, _writeLimit);
            _output.Write(buffer, offset, length); _writeLimit -= length;
            return length == count;
        }
        public override int Read(byte[] buffer, int offset, int count) { SyncCalls++; return ReadCore(buffer, offset, count); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { AsyncCalls++; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(ReadCore(buffer, offset, count)); }
        public override void Write(byte[] buffer, int offset, int count)
        { SyncCalls++; if (!WriteCore(buffer, offset, count)) throw new IOException("Injected partial transport write."); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            AsyncCalls++; cancellationToken.ThrowIfCancellationRequested();
            if (WriteCore(buffer, offset, count)) return;
            PartialWriteStarted.TrySetResult();
            if (PauseAfterPartialWrite) await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new IOException("Injected partial transport write.");
        }
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
