using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseLob
    {
        /// <summary>Writes BLOB bytes at a zero-based byte offset. Multiple chunks are not atomic.</summary>
        public void WriteBytes(long byteOffset, byte[] buffer, int offset, int count)
            => WriteBytesCore(byteOffset, buffer, offset, count, false, CancellationToken.None).GetAwaiter().GetResult();
        /// <summary>Asynchronously writes BLOB bytes. A failure may leave an already completed prefix.</summary>
        public Task WriteBytesAsync(long byteOffset, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
            => WriteBytesCore(byteOffset, buffer, offset, count, true, cancellationToken);
        private async Task WriteBytesCore(long byteOffset, byte[] buffer, int offset, int count, bool async, CancellationToken token)
        {
            if (IsCharacter) throw new InvalidOperationException("WriteBytes requires a BLOB.");
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(offset));
            ValidateWriteOffset(byteOffset, count); token.ThrowIfCancellationRequested();
            var operation = BeginMutation();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token);
                while (count > 0)
                {
                    var length = Math.Min(count, 32767); var chunk = new byte[length]; Buffer.BlockCopy(buffer, offset, chunk, 0, length);
                    await operation.Write(chunk, length, byteOffset, async, linked.Token, token).ConfigureAwait(false);
                    offset += length; count -= length; byteOffset += length;
                }
            }
            finally { await EndMutation(operation, async).ConfigureAwait(false); }
        }

        /// <summary>Writes CLOB text at a zero-based Unicode scalar offset. Multiple chunks are not atomic.</summary>
        public void WriteText(long characterOffset, string text)
            => WriteTextCore(characterOffset, text, false, CancellationToken.None).GetAwaiter().GetResult();
        /// <summary>Asynchronously writes CLOB text through the client parameter encoding, preserving surrogate pairs between chunks.</summary>
        public Task WriteTextAsync(long characterOffset, string text, CancellationToken cancellationToken = default)
            => WriteTextCore(characterOffset, text, true, cancellationToken);
        private async Task WriteTextCore(long characterOffset, string text, bool async, CancellationToken token)
        {
            if (!IsCharacter) throw new InvalidOperationException("WriteText requires a CLOB.");
            if (text == null) throw new ArgumentNullException(nameof(text));
            _ = ResultColumn.Utf8.GetByteCount(text); var total = 0;
            var encoding = Context.Session.ClientEncoding;
            // Prevalidate every write-sized chunk without allocating an encoded copy of the whole CLOB.
            for (var start = 0; start < text.Length;)
            {
                var end = TextChunkEnd(text, start, out var amount);
                if (!ReferenceEquals(encoding, ResultColumn.Utf8)) _ = encoding.GetByteCount(text.Substring(start, end - start));
                total += amount; start = end;
            }
            ValidateWriteOffset(characterOffset, total); token.ThrowIfCancellationRequested();
            var operation = BeginMutation();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token);
                var start = 0;
                while (start < text.Length)
                {
                    var end = TextChunkEnd(text, start, out var amount);
                    await operation.Write(text.Substring(start, end - start), amount, characterOffset, async, linked.Token, token).ConfigureAwait(false);
                    characterOffset += amount; start = end;
                }
            }
            finally { await EndMutation(operation, async).ConfigureAwait(false); }
        }

        private static int TextChunkEnd(string text, int start, out int amount)
        {
            var end = start; amount = 0;
            while (end < text.Length && amount < 8191) { if (char.IsHighSurrogate(text[end])) end++; end++; amount++; }
            return end;
        }

        /// <summary>Trims a BLOB to bytes, or a CLOB to Unicode scalars. This does not commit the transaction.</summary>
        public void Truncate(long length)
            => TruncateCore(length, false, CancellationToken.None).GetAwaiter().GetResult();
        /// <summary>Asynchronously trims the LOB through its original session and transaction.</summary>
        public Task TruncateAsync(long length, CancellationToken cancellationToken = default)
            => TruncateCore(length, true, cancellationToken);
        private async Task TruncateCore(long length, bool async, CancellationToken token)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length)); token.ThrowIfCancellationRequested();
            var operation = BeginMutation();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token);
                await operation.Trim(length, async, linked.Token, token).ConfigureAwait(false);
            }
            finally { await EndMutation(operation, async).ConfigureAwait(false); }
        }
        private static void ValidateWriteOffset(long offset, int count)
        { if (offset < 0 || offset > long.MaxValue - count) throw new ArgumentOutOfRangeException(nameof(offset)); }

        /// <summary>Explicitly frees a temporary LOB created by CreateTemporaryLob, then invalidates this handle.</summary>
        public void FreeTemporary() => FreeTemporaryCore(false, CancellationToken.None).GetAwaiter().GetResult();
        /// <summary>Asynchronously frees a temporary LOB on its original session before disposing its handle.</summary>
        public Task FreeTemporaryAsync(CancellationToken cancellationToken = default) => FreeTemporaryCore(true, cancellationToken);
        private async Task FreeTemporaryCore(bool async, CancellationToken token)
        {
            ThrowIfDisposed();
            if (!_ownsTemporary) throw new InvalidOperationException("Only the handle returned by CreateTemporaryLob owns temporary LOB release.");
            token.ThrowIfCancellationRequested(); var operation = BeginMutation();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token);
                await operation.Free(async, linked.Token, token).ConfigureAwait(false);
            }
            finally { await EndMutation(operation, async).ConfigureAwait(false); }
        }

        private LobMutation BeginMutation()
        {
            ThrowIfDisposed(); var operation = new LobMutation(this);
            if (Interlocked.CompareExchange(ref _mutation, operation, null) != null)
            { operation.Dispose(); throw new InvalidOperationException("A LOB mutation is already active."); }
            try { Context.Connection.ValidateLobContext(Context, operation, reserve: true); ThrowIfDisposed(); return operation; }
            catch { Interlocked.CompareExchange(ref _mutation, null, operation); operation.Dispose(); throw; }
        }
        private async Task EndMutation(LobMutation operation, bool async)
        {
            Interlocked.CompareExchange(ref _mutation, null, operation); operation.Dispose();
            if (async) await PreparedStatement.CloseIdleAsync(Context.Session, Context.Timeout).ConfigureAwait(false);
            else PreparedStatement.CloseIdle(Context.Session, Context.Timeout);
        }
        internal void ReplaceLocator(OceanBaseLob updated)
        { ThrowIfDisposed(); _bytes = updated.BindingBytes(); Locator = updated.Locator; }
    }
}
