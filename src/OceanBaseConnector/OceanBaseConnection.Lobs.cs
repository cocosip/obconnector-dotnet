using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        /// <summary>Creates a session-duration temporary BLOB or CLOB. Call FreeTemporary before disposing its handle.</summary>
        public OceanBaseLob CreateTemporaryLob(OceanBaseDbType lobType, OceanBaseTransaction? transaction = null)
            => CreateTemporaryLobCore(lobType, transaction, false, CancellationToken.None).GetAwaiter().GetResult();
        /// <summary>Asynchronously creates a temporary LOB on this connection and its explicitly bound transaction.</summary>
        public Task<OceanBaseLob> CreateTemporaryLobAsync(OceanBaseDbType lobType, OceanBaseTransaction? transaction = null, CancellationToken cancellationToken = default)
            => CreateTemporaryLobCore(lobType, transaction, true, cancellationToken);
        private async Task<OceanBaseLob> CreateTemporaryLobCore(OceanBaseDbType lobType, OceanBaseTransaction? transaction, bool async, CancellationToken token)
        {
            if (lobType != OceanBaseDbType.Blob && lobType != OceanBaseDbType.Clob) throw new ArgumentOutOfRangeException(nameof(lobType));
            RejectAmbientTransaction(); token.ThrowIfCancellationRequested();
            OceanBaseCommand command;
            lock (_gate)
            {
                var session = GetCommandSession(transaction);
                if (!session.UseLobLocators) throw new NotSupportedException("Temporary LOBs require negotiated Use Lob Locators=true.");
                command = new OceanBaseCommand
                {
                    Connection = this, Transaction = transaction, CommandTimeout = _builder.CommandTimeout,
                    RequiredSession = session, RequiredTransactionEpoch = _transactionEpoch,
                    CommandText = "DECLARE lob_value " + (lobType == OceanBaseDbType.Clob ? "CLOB" : "BLOB") +
                        "; BEGIN DBMS_LOB.CREATETEMPORARY(lob_value, FALSE, DBMS_LOB.SESSION); :lob := lob_value; END;",
                };
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Direction = ParameterDirection.Output, OceanBaseDbType = lobType });
            }
            try
            {
                try
                {
                    if (async) await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    else
                    {
                        using var reader = command.ExecuteReaderCore(CommandBehavior.Default, token);
                        do { while (reader.Read()) { } } while (reader.NextResult());
                    }
                }
                finally { if (async) await command.DisposeCoreAsync().ConfigureAwait(false); else command.Dispose(); }
                token.ThrowIfCancellationRequested();
                if (!(command.Parameters[0].Value is OceanBaseLob value)) throw new OceanBaseException("Temporary LOB creation completed without a valid locator.");
                using (value)
                {
                    lock (_gate)
                    {
                        ValidateLobContext(value.Context);
                        return new OceanBaseLob(value.Context, value.BindingBytes(), value.IsCharacter, ownsTemporary: true);
                    }
                }
            }
            catch
            {
                // A created LOB that cannot be delivered must not survive on an otherwise reusable session.
                if (command.HasSentSql) command.RequiredSession!.Break();
                (command.Parameters[0].Value as OceanBaseLob)?.Dispose(); throw;
            }
        }
    }
}
