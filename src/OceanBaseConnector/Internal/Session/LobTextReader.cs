using System.IO;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
using System;
using System.Threading;
#endif

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class LobTextReader : TextReader
    {
        private readonly LobReadStream _stream;
        private readonly StreamReader _reader;
        internal LobTextReader(LobReadStream stream)
        { _stream = stream; _reader = new StreamReader(stream, ResultColumn.Utf8, detectEncodingFromByteOrderMarks: false); }
        public override int Peek() { _stream.Check(); return _reader.Peek(); }
        public override int Read() { _stream.Check(); return _reader.Read(); }
        public override int Read(char[] buffer, int index, int count) { _stream.Check(); return _reader.Read(buffer, index, count); }
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            _stream.Check(); var read = await _reader.ReadAsync(buffer, index, count).ConfigureAwait(false); _stream.Check(); return read;
        }
        public override async Task<string> ReadToEndAsync()
        {
            _stream.Check(); var result = await _reader.ReadToEndAsync().ConfigureAwait(false); _stream.Check(); return result;
        }
        public override async Task<string?> ReadLineAsync()
        {
            _stream.Check(); var result = await _reader.ReadLineAsync().ConfigureAwait(false); _stream.Check(); return result;
        }
        public override async Task<int> ReadBlockAsync(char[] buffer, int index, int count)
        {
            _stream.Check(); var result = await _reader.ReadBlockAsync(buffer, index, count).ConfigureAwait(false); _stream.Check(); return result;
        }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            _stream.Check(cancellationToken); var read = await _reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _stream.Check(cancellationToken); return read;
        }
        public override async ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            _stream.Check(cancellationToken); var read = await _reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            _stream.Check(cancellationToken); return read;
        }
#endif
#if NET8_0_OR_GREATER
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _stream.Check(cancellationToken); var result = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            _stream.Check(cancellationToken); return result;
        }
        public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            _stream.Check(cancellationToken); var result = await _reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _stream.Check(cancellationToken); return result;
        }
#endif
        protected override void Dispose(bool disposing) { if (disposing) _reader.Dispose(); base.Dispose(disposing); }
    }
}
