using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    // Owns one exchange through its terminal response, including a reader's lifetime.
    internal sealed partial class CommandExchange : IDisposable
    {
        private readonly PhysicalSession _session;
        private readonly PhysicalSession.CommandLease _lease;
        private readonly PacketChannel _channel;
        private readonly ConnectionLog _log;
        private readonly Action _release;
        private readonly CancellationToken _caller;
        private readonly CancellationToken _operationToken;
        private readonly Func<CancellationToken>? _cancellationOrigin;
        private readonly Func<string, Stream?>? _localInfileStreamFactory;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly CancellationTokenSource _budget;
        private readonly CancellationTokenRegistration _registration;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private bool _responsePending;
        private bool _longDataPending;
        private bool _sqlSent;
        private bool _anySqlSent;
        private readonly bool _logCommand;
        private uint[] _statementCloses;
        private bool _terminal;
        private bool _cursorOwned;
        // REF CURSOR fetch is independent of the already completed procedure.
        private bool _refCursorCompletion;
        private int _finished;
        private int _packets;
        private DriverDiagnostics.Operation? _diagnostics;
        private string? _diagnosticOperation;
        private ActivityContext _traceContext;
        internal void SetTraceContext(ActivityContext context) => _traceContext = context;
        internal void StartDiagnostics(string operation)
        { _diagnosticOperation = operation; _diagnostics = DriverDiagnostics.Start(operation); _traceContext = _diagnostics?.Context ?? default; }

        internal CommandExchange(PhysicalSession session, ConnectionLog log, int timeout, CancellationToken caller, Action release, bool logCommand = true, object? owner = null, Func<CancellationToken>? cancellationOrigin = null, Func<string, Stream?>? localInfileStreamFactory = null)
        {
            _session = session;
            _log = log;
            _release = release;
            _caller = caller;
            _cancellationOrigin = cancellationOrigin;
            _localInfileStreamFactory = localInfileStreamFactory;
            _operationToken = caller;
            _logCommand = logCommand;
            _budget = CancellationTokenSource.CreateLinkedTokenSource(caller, _cancel.Token);
            try
            {
                // Validate the platform timer range before acquiring the physical session.
                if (timeout != 0) _budget.CancelAfter(TimeSpan.FromSeconds(timeout));
                _lease = session.Acquire(owner);
                _channel = _lease.Channel;
                _statementCloses = session.TakeStatementCloses();
                _registration = _budget.Token.Register(() =>
                {
                    lock (_gate) { if ((_responsePending || _longDataPending) && !_terminal) session.Break(); }
                });
            }
            catch
            {
                _lease?.Dispose();
                _budget.Dispose();
                _cancel.Dispose();
                throw;
            }
        }

        internal CapabilityFlags Capabilities => _session.Info!.Capabilities.Effective;
        internal string? SessionTimeZone { get; private set; }
        internal NationalCharacterSet? NationalCharacterSet { get; private set; }
        internal OceanBaseCompatibilityMode Mode => _session.Info!.Mode;
        internal bool TreatTinyAsBoolean => _session.TreatTinyAsBoolean;
        internal bool DiscardSessionAfterExecution { get; set; }
        internal bool InvalidatePreparedStatements { get; set; }
        internal bool InvalidateComplexTypes { get; set; }
        internal bool Succeeded { get; private set; }
        internal bool IsActive => Volatile.Read(ref _finished) == 0;
        internal LobSession? Lobs { get; set; }
        internal IReadOnlyDictionary<(string, string), OceanBaseComplexType>? ComplexTypes { get; set; }
        internal void EnsureReading()
        {
            if (_terminal) return;
            if (Volatile.Read(ref _finished) != 0) throw new InvalidOperationException("The command exchange has ended.");
            _budget.Token.ThrowIfCancellationRequested();
            if (_session.State != SessionState.Busy) throw new InvalidOperationException("The command session is no longer active.");
        }

        private void BeginSend(bool executesSql)
        {
            lock (_gate)
            {
                _budget.Token.ThrowIfCancellationRequested();
                if (executesSql)
                {
                    if (InvalidateComplexTypes) { _session.InvalidateComplexTypes(); InvalidateComplexTypes = false; }
                    if (InvalidatePreparedStatements) _session.InvalidatePreparedStatements();
                    if (DiscardSessionAfterExecution) _session.MarkSessionStateChanged();
                    SessionTimeZone = _session.TakeSessionTimeZone(_lease);
                    NationalCharacterSet = _session.TakeNationalCharacterSet(_lease);
                }
                _channel.BeginCommand();
                if (_channel.FullLinkTraceEnabled)
                {
                    if (_traceContext.TraceId == default)
                    {
                        _traceContext = Activity.Current?.Context ?? default;
                        if (_traceContext.TraceId == default) _traceContext = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
                    }
                    _channel.SetTraceContext(_traceContext);
                }
                _responsePending = true;
                _sqlSent |= executesSql;
                _anySqlSent |= executesSql;
            }
        }

        internal string? CaptureResultTimeZone()
        {
            var value = SessionTimeZone;
            SessionTimeZone = null;
            _session.TakeSessionTimeZone(_lease);
            return value;
        }

        internal NationalCharacterSet? CaptureResultNationalCharacterSet()
        {
            var value = NationalCharacterSet;
            NationalCharacterSet = null;
            _session.TakeNationalCharacterSet(_lease);
            return value;
        }

        internal void ObserveSessionState(OkPacket? ok)
        {
            SessionTimeZone = _session.ApplySessionVariables(_lease, ok?.SessionVariables);
            NationalCharacterSet = _session.NationalCharacterSet;
        }

        internal void OwnCursor(bool refCursor = false) { _cursorOwned = true; _refCursorCompletion |= refCursor; }
        internal void CaptureCursorSession()
        {
            SessionTimeZone = _session.TakeSessionTimeZone(_lease);
            NationalCharacterSet = _session.TakeNationalCharacterSet(_lease);
        }
        internal Action? BeforeSuccessfulCompletion { get; set; }
        internal bool IsXa { get; set; }
#if NET8_0_OR_GREATER
        internal System.Data.Common.DbBatchCommand? BatchCommand { get; set; }
        internal Action? BatchFailed { get; set; }
#endif
        internal void SetDatabase(string database) => _session.SetDatabase(_lease, database);

        internal bool HasSentSql { get { lock (_gate) return _anySqlSent; } }
        internal void ValidateResponseBoundary() => _channel.CompleteResponse();
        internal void ResponseComplete(bool expectsResponse = true)
        { lock (_gate) { _channel.CompleteResponse(expectsResponse); _responsePending = false; _sqlSent = false; } }

        internal void Send(byte[] request, bool executesSql = true, int? count = null)
        {
            try
            {
                foreach (var id in _statementCloses)
                { BeginSend(false); _channel.WritePacket(PreparedStatement.ClosePacket(id)); ResponseComplete(expectsResponse: false); }
                _statementCloses = Array.Empty<uint>();
                BeginSend(executesSql);
                _channel.WritePacket(request, 0, count ?? request.Length);
            }
            catch (Exception error) { throw Fail(error); }
        }

        internal async Task SendAsync(byte[] request, bool executesSql = true, CancellationToken readerToken = default, int? count = null)
        {
            using var registration = readerToken.Register(Cancel);
            try
            {
                foreach (var id in _statementCloses)
                { BeginSend(false); await _channel.WritePacketAsync(PreparedStatement.ClosePacket(id), _budget.Token).ConfigureAwait(false); ResponseComplete(expectsResponse: false); }
                _statementCloses = Array.Empty<uint>();
                BeginSend(executesSql);
                await _channel.WritePacketAsync(request, 0, count ?? request.Length, _budget.Token).ConfigureAwait(false);
            }
            catch (Exception error) { throw Fail(ReaderCanceled(error, readerToken)); }
        }

        internal byte[] Receive(bool allowUnsupportedPrepare = false)
        {
            try { return CheckPacket(_channel.ReadPacket(), allowUnsupportedPrepare: allowUnsupportedPrepare); }
            catch (Exception error) { throw Fail(error); }
        }

        internal async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken, bool allowUnsupportedPrepare = false)
        {
            using var registration = cancellationToken.Register(Cancel);
            try { return CheckPacket(await _channel.ReadPacketAsync(_budget.Token).ConfigureAwait(false), allowUnsupportedPrepare: allowUnsupportedPrepare); }
            catch (Exception error)
            {
                throw Fail(!_terminal && cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException("Reader operation canceled.", error, cancellationToken) : error);
            }
        }

        internal byte[] ReceiveLocalInfile(byte[] request)
        {
            try
            {
                UploadLocalInfile(request);
                return Receive();
            }
            catch (Exception error) { throw Fail(error); }
        }

        internal async Task<byte[]> ReceiveLocalInfileAsync(byte[] request, CancellationToken readerToken)
        {
            using var registration = readerToken.Register(Cancel);
            try
            {
                await UploadLocalInfileAsync(request, readerToken).ConfigureAwait(false);
                return await ReceiveAsync(readerToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw Fail(ReaderCanceled(error, readerToken));
            }
        }

        private void UploadLocalInfile(byte[] request)
        {
            var filename = ParseLocalInfileFilename(request);
            using var stream = OpenLocalInfileStream(filename);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                _budget.Token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > _channel.MaxPacketSize) throw new ProtocolException("LOCAL INFILE payload exceeds the maximum packet size.");
                _channel.WritePacket(buffer, 0, read);
            }
            _channel.WritePacket(Array.Empty<byte>());
        }

        private async Task UploadLocalInfileAsync(byte[] request, CancellationToken readerToken)
        {
            var filename = ParseLocalInfileFilename(request);
            using var stream = OpenLocalInfileStream(filename);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                readerToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, _budget.Token).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > _channel.MaxPacketSize) throw new ProtocolException("LOCAL INFILE payload exceeds the maximum packet size.");
                await _channel.WritePacketAsync(buffer, 0, read, _budget.Token).ConfigureAwait(false);
            }
            await _channel.WritePacketAsync(Array.Empty<byte>(), _budget.Token).ConfigureAwait(false);
        }

        private Stream OpenLocalInfileStream(string filename)
        {
            if ((Capabilities & CapabilityFlags.ClientLocalFiles) == 0)
                throw new NotSupportedException("LOCAL INFILE is not enabled for this connection.");
            if (_localInfileStreamFactory == null)
                throw new NotSupportedException("LOCAL INFILE stream factory is not configured.");
            var stream = _localInfileStreamFactory(filename);
            if (stream == null)
                throw new NotSupportedException("LOCAL INFILE stream factory returned null.");
            if (!stream.CanRead) throw new NotSupportedException("LOCAL INFILE stream factory must return a readable stream.");
            return stream;
        }

        private static string ParseLocalInfileFilename(byte[] request)
        {
            if (request.Length <= 1) throw new ProtocolException("LOCAL INFILE request has no filename.");
            return Encoding.UTF8.GetString(request, 1, request.Length - 1);
        }

        private void CountPacket()
        {
            if (++_packets > 1_000_000) throw new ProtocolException("Command response packet limit exceeded.");
        }

        private byte[] CheckPacket(byte[] packet, bool count = true, bool allowUnsupportedPrepare = false)
        {
            if (count) CountPacket();
            if (packet.Length == 0) throw new ProtocolException("Empty command response packet.");
            if (ErrPacket.IsErr(packet))
            {
                var server = ErrPacket.Parse(packet);
                if (allowUnsupportedPrepare && server.ErrorCode == 1295 && server.SqlState == "HY000")
                {
                    // Only the first prepare response may keep this lease for text execution.
                    ResponseComplete();
                    return packet;
                }
                var error = new OceanBaseException("The server rejected the command.", server.ErrorCode, server.SqlState);
#if NET8_0_OR_GREATER
                error.FailedBatchCommand = BatchCommand;
#endif
                if (_longDataPending && !_sqlSent && Mode == OceanBaseCompatibilityMode.Oracle)
                {
                    ResponseComplete();
                    _session.Break(); // A piece error does not prove that staged parameter data was cleared.
                }
                if (_cursorOwned) _session.Break();
                Complete(error);
                throw error;
            }
            return packet;
        }

        internal void Complete(Exception? error = null)
        {
            lock (_gate)
            {
                if (error == null && (BeforeSuccessfulCompletion != null || _refCursorCompletion))
                {
                    _budget.Token.ThrowIfCancellationRequested();
                    if (_session.State != SessionState.Busy) throw new InvalidOperationException("The result session is no longer active.");
                }
                if (_session.State == SessionState.Busy) _channel.CompleteResponse();
                if (IsXa && error is OceanBaseException xaError &&
                    (xaError.ServerErrorCode == 1402 || xaError.ServerErrorCode == 1613 || xaError.ServerErrorCode == 1614))
                    _session.Break();
                if (error == null) BeforeSuccessfulCompletion?.Invoke();
                _terminal = true; _responsePending = false;
            }
            _lease.CompleteIfActive();
            Finish(error);
        }

        internal Exception Fail(Exception error)
        {
            if (_terminal || Volatile.Read(ref _finished) != 0) return error;
            if (error is OceanBaseException databaseError && databaseError.IsExecutionOutcomeUnknown) return error;
            if (_responsePending || _longDataPending) _lease.Dispose();
            else _lease.CompleteIfActive();
            Exception cause = error;
            var caller = _cancellationOrigin?.Invoke() ?? _caller;
            if (caller.IsCancellationRequested) cause = new OperationCanceledException("Command canceled.", error, caller);
            else if (error is OperationCanceledException canceled && canceled.CancellationToken.CanBeCanceled && canceled.CancellationToken != _budget.Token) cause = error;
            else if (_cancel.IsCancellationRequested || _operationToken.IsCancellationRequested) cause = new OperationCanceledException("Command canceled.", error);
            else if (_budget.IsCancellationRequested) cause = new TimeoutException("Command timed out.", error);
            var mapped = _sqlSent
                ? new OceanBaseException("The command response was not fully consumed; execution outcome is unknown.",
                    isExecutionOutcomeUnknown: true, innerException: cause)
                : _responsePending && !(cause is OperationCanceledException) && !(cause is TimeoutException)
                    ? new OceanBaseException("Command initialization failed before execution.", innerException: cause) : cause;
#if NET8_0_OR_GREATER
            if (mapped is OceanBaseException batchError) batchError.FailedBatchCommand = BatchCommand;
#endif
            Finish(mapped);
            return mapped;
        }

        internal void Cancel()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void Finish(Exception? error)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            var elapsed = _elapsed.Elapsed.TotalMilliseconds;
            var threshold = _session.SlowQueryThresholdMilliseconds;
            var slow = threshold > 0 && elapsed >= threshold && (_diagnosticOperation == "execute" || _diagnosticOperation == "cursor.fetch");
            foreach (var id in _statementCloses) _session.QueueStatementClose(id);
            _statementCloses = Array.Empty<uint>();
            _registration.Dispose();
            _budget.Dispose();
            _cancel.Dispose();
            Succeeded = error == null;
            _release();
#if NET8_0_OR_GREATER
            if (error != null) BatchFailed?.Invoke();
#endif
            _diagnostics?.Finish(_session.Info!.Mode, error, slow);
            if (slow)
            {
                DriverDiagnostics.SlowOperation(_diagnosticOperation!, _session.Info!.Mode, error);
                _log.SlowCommand(_diagnosticOperation!, _session.Endpoint, _session.Info!.Mode, elapsed, threshold, error);
            }
            if (_logCommand) _log.CommandFinished(_session.Endpoint, _session.Info!.Mode, _elapsed.ElapsedMilliseconds, error);
        }

        public void Dispose()
        {
            if (!_terminal && Volatile.Read(ref _finished) == 0)
                Fail(new InvalidOperationException("The reader was closed with an unread response."));
        }
    }
}
