using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class LobReadStream : Stream
    {
        private readonly OceanBaseLob _lob;
        private byte[] _buffer = Array.Empty<byte>();
        private int _index;
        private int _end;
        private long _position;
        private long _offset = 1;
        private int _reading;
        private int _disposed;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        internal LobReadStream(OceanBaseLob lob) => _lob = lob;
        internal void Initialize()
        {
            if (_lob.Locator.HasInlineData)
            { var data = _lob.Locator.InlineData; _buffer = data.Array!; _index = data.Offset; _end = data.Offset + data.Count; }
        }
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get { Check(); return _lob.ByteLength; } }
        public override long Position { get { Check(); return _position; } set => throw new NotSupportedException(); }
        internal void Check(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!CanRead) throw new ObjectDisposedException(nameof(LobReadStream));
            _lob.ThrowIfDisposed(); _lob.Context.Connection.ValidateLobContext(_lob.Context, this);
        }
        private void Enter(byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if ((uint)offset > (uint)bytes.Length || (uint)count > (uint)(bytes.Length - offset)) throw new ArgumentOutOfRangeException(nameof(offset));
            lock (_gate)
            {
                Check(token);
                if (_reading != 0) throw new InvalidOperationException("Concurrent LOB reads are not supported.");
                _reading = 1;
            }
        }
        private void Exit()
        { lock (_gate) { _reading = 0; if (_disposed != 0) _stop.Dispose(); } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Enter(buffer, offset, count, CancellationToken.None);
            try
            {
                if (count == 0 || _position == _lob.ByteLength) return 0;
                if (_index == _end)
                {
                    using var command = ReadCommand(); using var reader = command.ExecuteReaderCore(CommandBehavior.Default, _stop.Token);
                    do { while (reader.Read()) { } } while (reader.NextResult());
                    Accept(command);
                }
                Check(); return Copy(buffer, offset, count);
            }
            catch { Dispose(); throw; }
            finally { Exit(); }
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Enter(buffer, offset, count, cancellationToken);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
                if (count == 0 || _position == _lob.ByteLength) return 0;
                if (_index == _end)
                {
                    var command = ReadCommand(); command.CancellationOrigin = () => cancellationToken;
                    try { await command.ExecuteNonQueryAsync(linked.Token).ConfigureAwait(false); Accept(command); }
                    finally { await command.DisposeCoreAsync().ConfigureAwait(false); }
                }
                Check(cancellationToken); return Copy(buffer, offset, count);
            }
            catch { await CloseAsync().ConfigureAwait(false); throw; }
            finally { Exit(); }
        }
        private int Copy(byte[] target, int offset, int count)
        {
            var length = Math.Min(count, _end - _index); Buffer.BlockCopy(_buffer, _index, target, offset, length);
            _index += length; _position += length; return length;
        }
        private OceanBaseCommand ReadCommand()
        {
            var context = _lob.Context;
            var command = new OceanBaseCommand
            {
                Connection = context.Connection, Transaction = context.Transaction, CommandTimeout = context.Timeout,
                CommandType = CommandType.StoredProcedure, CommandText = "DBMS_LOB.READ", RequiredSession = context.Session,
                RequiredTransactionEpoch = context.Epoch, SessionOwner = this, IsLobRead = true,
            };
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Value = _lob });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "amount", DbType = DbType.Int32, Direction = ParameterDirection.InputOutput, Value = Requested });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "offset", DbType = DbType.Int64, Value = _offset });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "buffer", DbType = _lob.IsCharacter ? DbType.String : DbType.Binary, Direction = ParameterDirection.Output });
            return command;
        }
        private int Requested => _lob.IsCharacter ? 8191 : (int)Math.Min(32767, _lob.ByteLength - _position);
        private void Accept(OceanBaseCommand command)
        {
            var amountValue = command.Parameters[1].Value;
            long amount = -1;
            try
            {
                var number = amountValue is OceanBaseDecimal precise ? precise.ToDecimal() :
                    amountValue is decimal decimalValue ? decimalValue : amountValue is long wide ? wide : amountValue is int narrow ? narrow : -1;
                if (number > 0 && number <= Requested && number == decimal.Truncate(number)) amount = (long)number;
            }
            catch (OverflowException) { }
            byte[]? bytes = null;
            if (_lob.IsCharacter && command.Parameters[3].Value is string text)
            {
                bytes = ResultColumn.Utf8.GetBytes(text); long scalars = 0;
                for (var i = 0; i < text.Length; i++) { if (char.IsHighSurrogate(text[i])) i++; scalars++; }
                if (scalars != amount) InvalidChunk();
            }
            else if (!_lob.IsCharacter) bytes = command.Parameters[3].Value as byte[];
            if (amount <= 0 || amount > Requested || bytes == null || bytes.Length == 0 || bytes.Length > _lob.ByteLength - _position ||
                (_lob.IsCharacter ? bytes.Length > 32764 : bytes.Length != amount)) InvalidChunk();
            _buffer = bytes!; _index = 0; _end = bytes!.Length;
            // The final chunk need not advance beyond Int64's largest valid locator offset.
            if (_position + bytes.Length < _lob.ByteLength) _offset = checked(_offset + amount);
        }
        private void InvalidChunk()
        { _lob.Context.Session.Break(); throw new OceanBaseException("The LOB read returned an invalid chunk or amount."); }
        protected override void Dispose(bool disposing)
        {
            if (Release() && disposing) PreparedStatement.CloseIdle(_lob.Context.Session, _lob.Context.Timeout);
            base.Dispose(disposing);
        }
        private bool Release()
        {
            lock (_gate)
            {
                if (_disposed != 0) return false;
                Volatile.Write(ref _disposed, 1); _stop.Cancel();
                if (_reading == 0) _stop.Dispose();
            }
            _lob.Context.Session.ReleaseReservation(this); return true;
        }
        internal async Task CloseAsync()
        { if (Release()) await PreparedStatement.CloseIdleAsync(_lob.Context.Session, _lob.Context.Timeout).ConfigureAwait(false); }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async ValueTask DisposeAsync()
        { await CloseAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
#endif
        public override void Flush() { Check(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
