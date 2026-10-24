using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
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
        private IOceanBaseAddressResolver? _addressResolver;
        private EndpointSelector _endpointSelector = new EndpointSelector();
        private PhysicalSessionPool? _pool;
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
                    if (_session != null) throw new InvalidOperationException("Close the connection before changing its configuration.");
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
                    if (_session != null) throw new InvalidOperationException("Close the connection before changing its address resolver.");
                    _addressResolver = value;
                    _endpointSelector = new EndpointSelector();
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
                        default: return _session == null ? ConnectionState.Closed : ConnectionState.Connecting;
                    }
                }
            }
        }

        public override void Open()
        {
            var session = StartOpen(CancellationToken.None, out var options);
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("open");
            _log.Opening(options.Endpoint);
            try
            {
                EnsureOpening(session);
                if (session.State == SessionState.Closed) session.Open();
                else if (session.State != SessionState.Open) throw new InvalidOperationException("The pooled physical session is not available.");
                PublishOpen(session);
            }
            catch (Exception error) { FailedOpen(session, timer.ElapsedMilliseconds, error); diagnostics?.Finish(null, error); throw; }
            diagnostics?.Finish(session.Info!.Mode, null);
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
            _log.Opened(session.Endpoint, session.Info!.Mode, timer.ElapsedMilliseconds);
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            var session = StartOpen(cancellationToken, out var options);
            var timer = Stopwatch.StartNew();
            var diagnostics = DriverDiagnostics.Start("open");
            _log.Opening(options.Endpoint);
            try
            {
                EnsureOpening(session);
                if (session.State == SessionState.Closed) await session.OpenAsync(cancellationToken).ConfigureAwait(false);
                else if (session.State != SessionState.Open) throw new InvalidOperationException("The pooled physical session is not available.");
                PublishOpen(session);
            }
            catch (Exception error) { FailedOpen(session, timer.ElapsedMilliseconds, error); diagnostics?.Finish(null, error); throw; }
            diagnostics?.Finish(session.Info!.Mode, null);
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
            _log.Opened(session.Endpoint, session.Info!.Mode, timer.ElapsedMilliseconds);
        }

        private PhysicalSession StartOpen(CancellationToken token, out OceanBaseConnectionOptions options)
        {
            token.ThrowIfCancellationRequested();
            RejectAmbientTransaction();
#if NET8_0_OR_GREATER
            _source?.EnsureAvailable();
#endif
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_session != null) throw new InvalidOperationException("The connection is already open or opening; close it first.");
                _options ??= _builder.Build();
                _hidePassword = true;
                options = _options;
                if (options.Pooling)
                {
                    _pool ??= PhysicalSessionPoolRegistry.Get(_connectionString);
                    return _session = _pool.Rent(options, _addressResolver, _endpointSelector, out _);
                }
                return _session = new PhysicalSession(options, _addressResolver, _endpointSelector);
            }
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

        private void FailedOpen(PhysicalSession session, long elapsed, Exception error)
        {
            session.Dispose();
            lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
            _log.Failed(session.Endpoint, elapsed, error);
        }

        public override void Close() => CloseCore(null);
        internal void CloseSession(PhysicalSession session) => CloseCore(session);
        private void CloseCore(PhysicalSession? requiredSession)
        {
            var closing = DetachForClose(requiredSession);
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

        public override void EnlistTransaction(Transaction? transaction) => throw new NotSupportedException("Ambient and distributed transactions are not supported.");
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
        internal static void RejectAmbientTransaction()
        {
            if (Transaction.Current != null) throw new NotSupportedException("Ambient transactions are not supported.");
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseConnection)); }

        private void FinishClose(PhysicalSession? session, string? endpoint)
        {
            if (session == null) return;
            if (_options?.Pooling == true && _pool != null)
            {
                _pool.Return(session, _log, _builder.CommandTimeout);
            }
            else session.Dispose();
            _log.Closed(endpoint!);
        }

        private async Task FinishCloseAsync(PhysicalSession? session, string? endpoint)
        {
            if (session == null) return;
            if (_options?.Pooling == true && _pool != null)
            {
                await _pool.ReturnAsync(session, _log, _builder.CommandTimeout).ConfigureAwait(false);
            }
            else session.Dispose();
            _log.Closed(endpoint!);
        }
    }
}
