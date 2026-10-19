using System;
using System.IO;
using System.Threading;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    /// <summary>An Oracle LOB locator bound to its original physical session and transaction.</summary>
    public sealed class OceanBaseLob : IDisposable
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        , IAsyncDisposable
#endif
    {
        private readonly byte[] _bytes;
        private int _opened;
        private int _disposed;
        private LobReadStream? _stream;
        internal LobSession Context { get; }
        internal LobLocator Locator { get; }
        /// <summary>True for CLOB, false for BLOB.</summary>
        public bool IsCharacter { get; }
        /// <summary>Byte length reported by the locator; CLOB character counts may differ.</summary>
        public long ByteLength => Locator.ByteLength;
        internal OceanBaseLob(LobSession context, byte[] bytes, bool character)
        { Context = context; _bytes = bytes; IsCharacter = character; Locator = LobLocator.Parse(bytes); }

        /// <summary>Opens a BLOB once, after its originating reader is consumed. Dispose the stream to release the session.</summary>
        public Stream OpenRead()
        {
            if (IsCharacter) throw new InvalidOperationException("A CLOB requires OpenTextReader.");
            return OpenStream();
        }
        /// <summary>Opens a UTF-8 CLOB once, after its originating reader is consumed.</summary>
        public TextReader OpenTextReader()
        {
            if (!IsCharacter) throw new InvalidOperationException("A BLOB requires OpenRead.");
            return new LobTextReader(OpenStream());
        }
        private LobReadStream OpenStream()
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _opened, 1, 0) != 0) throw new InvalidOperationException("A LOB handle can open only one stream.");
            var stream = new LobReadStream(this);
            try
            {
                Context.Connection.ValidateLobContext(Context, stream, reserve: true);
                Interlocked.Exchange(ref _stream, stream); ThrowIfDisposed(); return stream;
            }
            catch { stream.Dispose(); Interlocked.Exchange(ref _stream, null); Volatile.Write(ref _opened, 0); throw; }
        }
        internal void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OceanBaseLob)); }
        internal byte[] BindingBytes() { ThrowIfDisposed(); return _bytes; }
        internal void ValidateBinding(OceanBaseConnection connection, PhysicalSession session, OceanBaseTransaction? transaction, object? owner)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(Context.Connection, connection) || !ReferenceEquals(Context.Session, session) || !ReferenceEquals(Context.Transaction, transaction))
                throw new InvalidOperationException("LOB parameters must use their original connection and transaction.");
            connection.ValidateLobContext(Context, owner);
        }
        /// <summary>Releases the client handle and any open stream; database temporary LOB ownership stays with its creator.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        /// <summary>Asynchronously releases the client handle and any open stream.</summary>
        public async System.Threading.Tasks.ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream != null) await stream.CloseAsync().ConfigureAwait(false);
        }
#endif
    }
}
