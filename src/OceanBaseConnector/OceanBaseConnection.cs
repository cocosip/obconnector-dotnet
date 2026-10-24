using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection : DbConnection
    {
        private readonly object _gate = new object();
        private readonly ConnectionLog _log;
        private string _connectionString = string.Empty;
        private OceanBaseConnectionStringBuilder _builder = new OceanBaseConnectionStringBuilder();
        private OceanBaseConnectionOptions? _options;
        private PhysicalSession? _session;
        private bool _hidePassword;
        private bool _disposed;
        private bool _opening;
        private long _openGeneration;
        private CancellationTokenSource? _openingCancellation;
        private IOceanBaseAddressResolver? _addressResolver;
        private EndpointSelector _endpointSelector = new EndpointSelector();
        private PhysicalSessionPool? _pool;
        private Func<string, Stream?>? _localInfileStreamFactory;
        private Transaction? _ambientTransaction;
        private AmbientEnlistment? _ambientEnlistment;
#if NET8_0_OR_GREATER
        private readonly OceanBaseDataSource? _source;
#endif

        public OceanBaseConnection() : this(string.Empty, null) { }
        public OceanBaseConnection(string connectionString) : this(connectionString, null) { }
        public OceanBaseConnection(string connectionString, ILoggerFactory? loggerFactory)
        {
            _log = new ConnectionLog(loggerFactory);
            ConnectionString = connectionString;
        }

#if NET8_0_OR_GREATER
        internal OceanBaseConnection(OceanBaseDataSource source, OceanBaseConnectionOptions options, string connectionString, ConnectionLog log)
        {
            _source = source;
            _options = options;
            _connectionString = connectionString;
            _builder = new OceanBaseConnectionStringBuilder(connectionString);
            _hidePassword = true;
            _log = log;
            _addressResolver = source.AddressResolver;
            _endpointSelector = source.EndpointSelector;
            _pool = source.Pool;
        }
#endif

        public override string ConnectionString
        {
            get
            {
                lock (_gate) return _hidePassword && !_builder.PersistSecurityInfo
                ? _builder.ToSanitizedConnectionString(false) : _connectionString;
            }
            set
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
#if NET8_0_OR_GREATER
                    if (_source != null) throw new InvalidOperationException("A data-source connection has fixed configuration.");
#endif
                    if (_session != null || _opening) throw new InvalidOperationException("Close the connection before changing its configuration.");
                    var builder = new OceanBaseConnectionStringBuilder(value ?? string.Empty);
                    _connectionString = builder.ConnectionString;
                    _builder = builder;
                    _options = null;
                    _pool = null;
                    _hidePassword = false;
                    _endpointSelector = new EndpointSelector();
                }
            }
        }

        /// <summary>Optional caller-owned address source, frozen while open or opening.
        /// Data-source connections inherit a fixed resolver from their builder.</summary>
        public IOceanBaseAddressResolver? AddressResolver
        {
            get { lock (_gate) return _addressResolver; }
            set
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
#if NET8_0_OR_GREATER
                    if (_source != null) throw new InvalidOperationException("A data-source connection has fixed configuration.");
#endif
                    if (_session != null || _opening) throw new InvalidOperationException("Close the connection before changing its address resolver.");
                    _addressResolver = value;
                    _endpointSelector = new EndpointSelector();
                }
            }
        }

        /// <summary>
        /// Supplies a readable stream for a server-requested LOCAL INFILE filename.
        /// The filename is an opaque server value; the driver never opens it as a local path.
        /// Set this before opening the connection when <c>Allow Local Infile=true</c>.
        /// </summary>
        public Func<string, Stream?>? LocalInfileStreamFactory
        {
            get { lock (_gate) return _localInfileStreamFactory; }
            set
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (_session != null || _opening) throw new InvalidOperationException("Close the connection before changing the LOCAL INFILE stream factory.");
                    _localInfileStreamFactory = value;
                }
            }
        }

        public override string Database { get { lock (_gate) return _session?.Database ?? _builder.Database; } }
        public override string DataSource { get { lock (_gate) return _builder.Server; } }
        public override int ConnectionTimeout { get { lock (_gate) return _builder.ConnectionTimeout; } }
        public OceanBaseCompatibilityMode? CompatibilityMode { get { lock (_gate) return _session?.Info?.Mode; } }
        public override string ServerVersion
        {
            get { lock (_gate) return _session?.Info?.ServerVersion ?? throw new InvalidOperationException("Connection is not open."); }
        }
        public override ConnectionState State
        {
            get
            {
                lock (_gate)
                {
                    switch (_session?.State ?? SessionState.Closed)
                    {
                        case SessionState.Connecting: return ConnectionState.Connecting;
                        case SessionState.Open:
                        case SessionState.Busy: return ConnectionState.Open;
                        case SessionState.Broken: return ConnectionState.Broken;
                        default: return _opening || _session != null ? ConnectionState.Connecting : ConnectionState.Closed;
                    }
                }
            }
        }

        public override void Open()
        {
            var context = PrepareOpen(CancellationToken.None);
            PhysicalSession? session = null;
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("open");
            try
            {
                session = RentSession(context);
                _log.Opening(context.Options.Endpoint);
                EnsureOpening(session);
                if (session.State == SessionState.Closed) session.Open();
                else if (session.State != SessionState.Open) throw new InvalidOperationException("The pooled physical session is not available.");
                PublishOpen(session);
                if (context.AmbientTransaction != null) EnlistTransaction(context.AmbientTransaction);
            }
            catch (Exception error)
            {
                var failure = NormalizeOpenError(context, error);
                if (session != null) FailedOpen(context, session, timer.ElapsedMilliseconds, failure);
                else ClearOpening(context);
                diagnostics?.Finish(null, failure);
                throw failure;
            }
            finally { context.Dispose(); }
            diagnostics?.Finish(session.Info!.Mode, null);
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
            _log.Opened(session.Endpoint, session.Info!.Mode, timer.ElapsedMilliseconds);
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            var context = PrepareOpen(cancellationToken);
            PhysicalSession? session = null;
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("open");
            try
            {
                session = await RentSessionAsync(context, context.Token).ConfigureAwait(false);
                _log.Opening(context.Options.Endpoint);
                EnsureOpening(session);
                if (session.State == SessionState.Closed) await session.OpenAsync(context.Token).ConfigureAwait(false);
                else if (session.State != SessionState.Open) throw new InvalidOperationException("The pooled physical session is not available.");
                PublishOpen(session);
                if (context.AmbientTransaction != null) EnlistTransaction(context.AmbientTransaction);
            }
            catch (Exception error)
            {
                var failure = NormalizeOpenError(context, error);
                if (session != null) FailedOpen(context, session, timer.ElapsedMilliseconds, failure);
                else ClearOpening(context);
                diagnostics?.Finish(null, failure);
                throw failure;
            }
            finally { context.Dispose(); }
            diagnostics?.Finish(session.Info!.Mode, null);
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
            _log.Opened(session.Endpoint, session.Info!.Mode, timer.ElapsedMilliseconds);
        }

        private OpenContext PrepareOpen(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
#if NET8_0_OR_GREATER
            _source?.EnsureAvailable();
#endif
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_session != null || _opening) throw new InvalidOperationException("The connection is already open or opening; close it first.");
                _options ??= _builder.Build();
                _hidePassword = true;
                _opening = true;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _openingCancellation = cancellation;
                var pool = _options.Pooling ? _pool ??= PhysicalSessionPoolRegistry.Get(_connectionString) : null;
                return new OpenContext(_options, pool, _addressResolver, _endpointSelector, ++_openGeneration, cancellation, token, Transaction.Current);
            }
        }

        private PhysicalSession RentSession(OpenContext context)
        {
            PhysicalSession? session = null;
            try
            {
                session = context.Pool == null
                    ? new PhysicalSession(context.Options, context.Resolver, context.Selector)
                    : context.Pool.Rent(context.Options, context.Resolver, context.Selector, out _, context.Token);
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (!_opening || _session != null || context.Generation != _openGeneration) throw new OperationCanceledException("Connection opening was canceled.");
                    _session = session;
                    _opening = false;
                }
                return session;
            }
            catch
            {
                ClearOpening(context);
                if (session != null)
                {
                    if (context.Pool != null) context.Pool.Discard(session); else session.Dispose();
                }
                ReleasePoolIfEmpty(context.Pool);
                throw;
            }
        }

        private async Task<PhysicalSession> RentSessionAsync(OpenContext context, CancellationToken token)
        {
            PhysicalSession? session = null;
            try
            {
                session = context.Pool == null
                    ? new PhysicalSession(context.Options, context.Resolver, context.Selector)
                    : await context.Pool.RentAsync(context.Options, context.Resolver, context.Selector, token).ConfigureAwait(false);
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (!_opening || _session != null || context.Generation != _openGeneration) throw new OperationCanceledException("Connection opening was canceled.");
                    _session = session;
                    _opening = false;
                }
                return session;
            }
            catch
            {
                ClearOpening(context);
                if (session != null)
                {
                    if (context.Pool != null) context.Pool.Discard(session); else session.Dispose();
                }
                ReleasePoolIfEmpty(context.Pool);
                throw;
            }
        }

        private sealed class OpenContext
        {
            internal OpenContext(OceanBaseConnectionOptions options, PhysicalSessionPool? pool,
                IOceanBaseAddressResolver? resolver, EndpointSelector selector, long generation,
                CancellationTokenSource cancellation, CancellationToken callerToken, Transaction? ambientTransaction)
            { Options = options; Pool = pool; Resolver = resolver; Selector = selector; Generation = generation; Cancellation = cancellation; CallerToken = callerToken; AmbientTransaction = ambientTransaction; }
            internal OceanBaseConnectionOptions Options { get; }
            internal PhysicalSessionPool? Pool { get; }
            internal IOceanBaseAddressResolver? Resolver { get; }
            internal EndpointSelector Selector { get; }
            internal long Generation { get; }
            internal CancellationTokenSource Cancellation { get; }
            internal CancellationToken CallerToken { get; }
            internal Transaction? AmbientTransaction { get; }
            internal CancellationToken Token => Cancellation.Token;
            internal void Dispose() => Cancellation.Dispose();
        }

        private void EnsureOpening(PhysicalSession session)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_session, session)) throw new OperationCanceledException("Connection was closed during opening.");
            }
        }

        private void PublishOpen(PhysicalSession session)
        {
#if NET8_0_OR_GREATER
            if (_source != null) { _source.PublishOpen(() => FinishOpen(session)); return; }
#endif
            FinishOpen(session);
        }

        private void FinishOpen(PhysicalSession session)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!ReferenceEquals(_session, session) || session.State != SessionState.Open)
                    throw new OperationCanceledException("Connection was closed during opening.");
            }
        }

        private void FailedOpen(OpenContext context, PhysicalSession session, long elapsed, Exception error)
        {
            if (context.Pool != null) context.Pool.Discard(session); else session.Dispose();
            ReleasePoolIfEmpty(context.Pool);
            lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
            ClearOpening(context);
            _log.Failed(session.Endpoint, elapsed, error);
        }

        private void ClearOpening(OpenContext context)
        {
            lock (_gate)
            {
                if (context.Generation == _openGeneration && ReferenceEquals(_openingCancellation, context.Cancellation))
                {
                    _opening = false;
                    _openingCancellation = null;
                }
            }
        }

        private static Exception NormalizeOpenError(OpenContext context, Exception error) =>
            error is OperationCanceledException && context.CallerToken.IsCancellationRequested
                ? new OperationCanceledException("Connection opening canceled.", error, context.CallerToken)
                : error;

        public override void Close() => CloseCore(null);
        internal void CloseSession(PhysicalSession session) => CloseCore(session);
        private void CloseCore(PhysicalSession? requiredSession)
        {
            var closing = DetachForClose(requiredSession);
            try { closing.OpenCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                if (closing.Previous == ConnectionState.Open) OnStateChange(new StateChangeEventArgs(closing.Previous, ConnectionState.Closed));
                if (closing.Exchange != null) CleanupTransaction(closing.Transaction!, closing.Exchange);
            }
            finally { closing.Exchange?.Dispose(); FinishClose(closing.Session, closing.Endpoint); }
        }

#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override Task CloseAsync() => CloseCoreAsync(null);
        internal Task CloseSessionAsync(PhysicalSession session) => CloseCoreAsync(session);
        private async Task CloseCoreAsync(PhysicalSession? requiredSession)
        {
            var closing = DetachForClose(requiredSession);
            try { closing.OpenCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                if (closing.Previous == ConnectionState.Open) OnStateChange(new StateChangeEventArgs(closing.Previous, ConnectionState.Closed));
                if (closing.Exchange != null) await CleanupTransactionAsync(closing.Transaction!, closing.Exchange).ConfigureAwait(false);
            }
            finally { closing.Exchange?.Dispose(); await FinishCloseAsync(closing.Session, closing.Endpoint).ConfigureAwait(false); }
        }
        public override async ValueTask DisposeAsync()
        {
            lock (_gate) _disposed = true;
            await CloseAsync().ConfigureAwait(false);
            base.Dispose(true);
            GC.SuppressFinalize(this);
        }
#endif
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate) _disposed = true;
                Close();
            }
            base.Dispose(disposing);
        }

        public override void EnlistTransaction(Transaction? transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (transaction.TransactionInformation.Status != TransactionStatus.Active)
                throw new InvalidOperationException("The transaction is not active.");
            if (transaction.TransactionInformation.DistributedIdentifier != Guid.Empty)
                throw new NotSupportedException("Distributed transaction promotion is not supported.");
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_session == null || _session.State != SessionState.Open)
                    throw new InvalidOperationException("The connection must be open before enlisting a transaction.");
                if (_ambientTransaction != null)
                {
                    if (SameTransaction(_ambientTransaction, transaction))
                    {
                        if (_transaction == null) throw new InvalidOperationException("The connection's ambient transaction has already ended on this connection.");
                        return;
                    }
                    throw new InvalidOperationException("The connection is already enlisted in another transaction.");
                }
                if (_transaction != null) throw new InvalidOperationException("A local transaction is already active.");
                _ambientTransaction = transaction;
            }

            var enlistment = new AmbientEnlistment(this, transaction);
            OceanBaseTransaction? local = null;
            try
            {
                local = BeginAmbientTransaction(transaction);
                transaction.EnlistVolatile(enlistment, EnlistmentOptions.None);
                lock (_gate) _ambientEnlistment = enlistment;
            }
            catch
            {
                enlistment.Deactivate();
                if (local != null)
                {
                    try { DisposeTransaction(local); }
                    catch { local.Session.Break(); }
                }
                lock (_gate)
                {
                    if (SameTransaction(_ambientTransaction, transaction)) _ambientTransaction = null;
                    _ambientEnlistment = null;
                }
                throw;
            }
        }
        protected override DbCommand CreateDbCommand()
        {
            lock (_gate) { ThrowIfDisposed(); return new OceanBaseCommand { Connection = this, CommandTimeout = _builder.CommandTimeout }; }
        }
        protected override DbProviderFactory DbProviderFactory => OceanBaseProviderFactory.Instance;
        internal PhysicalSession GetOpenSession(object? owner = null)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_session == null || _session.State != SessionState.Open || _session.IsReservedForOther(owner))
                    throw new InvalidOperationException("The connection must be open and have no active operation.");
                return _session;
            }
        }
        internal ConnectionLog Log => _log;
        internal void EnsureAmbientTransaction()
        {
            var current = Transaction.Current;
            if (current == null) return;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_ambientTransaction == null) throw new InvalidOperationException("The connection is not enlisted in the current transaction.");
                if (!SameTransaction(_ambientTransaction, current)) throw new InvalidOperationException("The connection is enlisted in a different transaction.");
                if (_transaction == null) throw new InvalidOperationException("The connection's ambient transaction has already ended on this connection.");
            }
        }

        internal void EndAmbientTransaction(Transaction transaction, bool commit)
        {
            OceanBaseTransaction? local;
            lock (_gate)
            {
                if (!SameTransaction(_ambientTransaction, transaction)) return;
                local = _transaction;
            }
            try
            {
                if (local != null) FinishTransaction(local, StartTransactionEnd(local, CancellationToken.None, allowAmbient: true), commit);
            }
            finally
            {
                lock (_gate)
                {
                    if (SameTransaction(_ambientTransaction, transaction)) _ambientTransaction = null;
                    _ambientEnlistment = null;
                }
            }
        }

        internal void PrepareAmbientTransaction(Transaction transaction)
        {
            lock (_gate)
            {
                if (!SameTransaction(_ambientTransaction, transaction) || _transaction == null ||
                    _transaction.InProgress || !IsActiveTransaction(_transaction))
                    throw new InvalidOperationException("The ambient transaction is no longer active on this connection.");
            }
        }

        internal void EndAmbientInDoubt(Transaction transaction)
        {
            lock (_gate)
            {
                if (SameTransaction(_ambientTransaction, transaction))
                {
                    _ambientTransaction = null;
                    _ambientEnlistment = null;
                    _session?.Break();
                }
            }
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseConnection)); }

        private static bool SameTransaction(Transaction? left, Transaction? right) =>
            left != null && right != null && left.Equals(right);

        private void FinishClose(PhysicalSession? session, string? endpoint)
        {
            if (session == null) return;
            var pool = _pool;
            if (_options?.Pooling == true && pool != null)
            {
                try { pool.Return(session, _log, _builder.CommandTimeout); }
                finally { ReleasePoolIfEmpty(pool); }
            }
            else session.Dispose();
            _log.Closed(endpoint!);
        }

        private async Task FinishCloseAsync(PhysicalSession? session, string? endpoint)
        {
            if (session == null) return;
            var pool = _pool;
            if (_options?.Pooling == true && pool != null)
            {
                try { await pool.ReturnAsync(session, _log, _builder.CommandTimeout).ConfigureAwait(false); }
                finally { ReleasePoolIfEmpty(pool); }
            }
            else session.Dispose();
            _log.Closed(endpoint!);
        }

        private void ReleasePoolIfEmpty(PhysicalSessionPool? pool)
        {
            if (pool == null) return;
#if NET8_0_OR_GREATER
            if (_source != null) return;
#endif
            if (PhysicalSessionPoolRegistry.ReleaseIfEmpty(_connectionString, pool))
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_pool, pool)) _pool = null;
                }
            }
        }
    }
}
