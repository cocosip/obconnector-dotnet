using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseCommand
    {
        /// <summary>Replaces parameters from the unique visible routine signature without executing it.
        /// Requires StoredProcedure and an open idle connection. Overload is an optional exact Oracle selector.</summary>
        public void DeriveParameters(string? overload = null)
            => DeriveParametersCore(overload, false, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>Asynchronously derives parameters using this command's transaction and per-query timeout.</summary>
        public Task DeriveParametersAsync(CancellationToken cancellationToken = default)
            => DeriveParametersCore(null, true, cancellationToken);

        /// <summary>Asynchronously derives a selected Oracle overload; failures preserve the parameter collection.</summary>
        public Task DeriveParametersAsync(string? overload, CancellationToken cancellationToken = default)
            => DeriveParametersCore(overload, true, cancellationToken);

        private async Task DeriveParametersCore(string? overload, bool asynchronous, CancellationToken token)
        {
            OceanBaseConnection connection; OceanBaseTransaction? transaction;
            OceanBaseCommand metadata; CancellationTokenSource cancellation; string text; int timeout, version;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                if (_execution != null || _derivation != null) throw new InvalidOperationException("The command already has an active operation.");
                if (CommandType != CommandType.StoredProcedure) throw new InvalidOperationException("Parameter derivation requires CommandType.StoredProcedure.");
                token.ThrowIfCancellationRequested();
                connection = _connection ?? throw new InvalidOperationException("A connection is required.");
                text = CommandText; transaction = _transaction; timeout = _timeout; version = _parameters.Version;
                metadata = new OceanBaseCommand { Connection = connection, Transaction = transaction, CommandTimeout = timeout };
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _derivation = cancellation;
                metadata.CancellationOrigin = () => token.IsCancellationRequested ? token : cancellation.Token;
            }
            PhysicalSession? session = null; var metadataDisposed = false;
            try
            {
                session = connection.BeginParameterDerivation(metadata, RequiredSession);
                var parameters = await connection.ReadDerivedParameters(metadata, text, overload, asynchronous, cancellation.Token).ConfigureAwait(false);
                // Any potentially failing command cleanup must precede publication.
                if (asynchronous) await metadata.DisposeCoreAsync().ConfigureAwait(false); else metadata.Dispose();
                metadataDisposed = true;
                lock (_gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_disposed || !ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction) ||
                        CommandType != CommandType.StoredProcedure || CommandText != text || _timeout != timeout)
                        throw new InvalidOperationException("The command context changed during parameter derivation.");
                    connection.PublishDerivedParameters(metadata, session, _parameters, parameters, version);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested(); throw;
            }
            finally
            {
                try
                {
                    session?.ReleaseReservation(metadata);
                    if (!metadataDisposed)
                    {
                        if (asynchronous) await metadata.DisposeCoreAsync().ConfigureAwait(false); else metadata.Dispose();
                    }
                }
                finally
                {
                    lock (_gate) if (ReferenceEquals(_derivation, cancellation)) _derivation = null;
                    cancellation.Dispose();
                }
            }
        }
    }
}
