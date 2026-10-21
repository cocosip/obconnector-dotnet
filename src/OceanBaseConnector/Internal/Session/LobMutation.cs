using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class LobMutation : IDisposable
    {
        private readonly OceanBaseLob _lob;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private bool _disposed;
        internal LobMutation(OceanBaseLob lob) => _lob = lob;
        internal CancellationToken Token => _stop.Token;
        internal Task Write(object buffer, int amount, long offset, bool async, CancellationToken token, CancellationToken caller)
        {
            var command = Command("DBMS_LOB.WRITE");
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "amount", Value = amount });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "offset", Value = checked(offset + 1) });
            // Connector/J sends BLOB write buffers as uppercase hexadecimal text (VARSTRING).
            // OceanBase's Oracle DBMS_LOB.WRITE path rejects binary RAW bindings above 2 KiB.
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "buffer",
                DbType = _lob.IsCharacter ? DbType.String : DbType.AnsiString,
                Value = _lob.IsCharacter ? buffer : Hex((byte[])buffer) });
            return Execute(command, async, token, caller);
        }

        private static string Hex(byte[] bytes)
        {
            const string digits = "0123456789ABCDEF";
            var result = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++) { result[i * 2] = digits[bytes[i] >> 4]; result[i * 2 + 1] = digits[bytes[i] & 15]; }
            return new string(result);
        }
        internal Task Trim(long length, bool async, CancellationToken token, CancellationToken caller)
        {
            var command = Command("DBMS_LOB.TRIM");
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "length", Value = length });
            return Execute(command, async, token, caller);
        }
        internal Task Free(bool async, CancellationToken token, CancellationToken caller)
        {
            var command = Command("DBMS_LOB.FREETEMPORARY"); command.CommandType = CommandType.Text;
            // FREETEMPORARY marks its locator invalid; return a scalar acknowledgement instead.
            command.CommandText = "DECLARE lob_value " + (_lob.IsCharacter ? "CLOB" : "BLOB") +
                " := :lob; BEGIN DBMS_LOB.FREETEMPORARY(lob_value); :released := '1'; END;";
            command.Parameters[0].Direction = ParameterDirection.Input;
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "released", Direction = ParameterDirection.Output, DbType = DbType.String });
            return Execute(command, async, token, caller, freeing: true);
        }
        private OceanBaseCommand Command(string procedure)
        {
            var context = _lob.Context;
            var command = new OceanBaseCommand
            {
                Connection = context.Connection, Transaction = context.Transaction, CommandTimeout = context.Timeout,
                CommandType = CommandType.StoredProcedure, CommandText = procedure, RequiredSession = context.Session,
                RequiredTransactionEpoch = context.Epoch, SessionOwner = this,
            };
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Direction = ParameterDirection.InputOutput,
                OceanBaseDbType = _lob.IsCharacter ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, Value = _lob });
            return command;
        }
        private async Task Execute(OceanBaseCommand command, bool async, CancellationToken token, CancellationToken caller, bool freeing = false)
        {
            command.CancellationOrigin = () => caller;
            try
            {
                if (async) await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                else
                {
                    using var reader = command.ExecuteReaderCore(CommandBehavior.Default, token);
                    do { while (reader.Read()) { } } while (reader.NextResult());
                }
                token.ThrowIfCancellationRequested(); _lob.ThrowIfDisposed(); _lob.Context.Connection.ValidateLobContext(_lob.Context, this);
                if (freeing)
                {
                    if (!(command.Parameters[1].Value is string marker) || marker != "1")
                    { _lob.Context.Session.Break(); throw new OceanBaseException("Temporary LOB release completed without its acknowledgement."); }
                    _lob.Dispose(); return;
                }
                if (!(command.Parameters[0].Value is OceanBaseLob updated))
                {
                    _lob.Context.Session.Break(); _lob.Dispose();
                    throw new OceanBaseException("The LOB mutation completed without a valid locator.");
                }
                using (updated) _lob.ReplaceLocator(updated);
            }
            // A failed mutation may have changed the server object without returning a usable locator.
            catch { _lob.Dispose(); throw; }
            finally { if (async) await command.DisposeCoreAsync().ConfigureAwait(false); else command.Dispose(); }
        }
        internal void Cancel()
        { lock (_gate) { if (!_disposed) _stop.Cancel(); } }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _stop.Dispose(); }
            _lob.Context.Session.ReleaseReservation(this);
        }
    }
}
