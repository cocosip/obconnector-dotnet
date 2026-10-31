#if NET8_0_OR_GREATER
using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    internal sealed class OceanBaseBatchReader : DbDataReader, IDbColumnSchemaGenerator
    {
        private readonly OceanBaseBatch.Operation _operation;
        private DbDataReader? _current;
        private int _index;
        private bool _closed;
        private bool _failed;
        private CommandExchange? _multiExchange;
        private byte[][]? _multiRequests;
        private int _multiNext;
        private int _multiGroupEnd;
        private bool _multiSend;
        internal OceanBaseBatchReader(OceanBaseBatch.Operation operation) => _operation = operation;
        private CommandBehavior Behavior => _operation.Behavior & ~CommandBehavior.CloseConnection;
        private bool SingleResult => (_operation.Behavior & (CommandBehavior.SingleResult | CommandBehavior.SingleRow)) != 0;
        internal void Start()
        {
            _operation.ThrowIfCanceled();
            if (CanUseMultiSendReader()) { StartMultiSend(); return; }
            if (_index < _operation.Commands.Length)
            { _current = _operation.Commands[_index].ExecuteReaderCore(Behavior, _operation.Token); UpdateCount(); }
        }
        internal async Task StartAsync()
        {
            _operation.ThrowIfCanceled();
            if (CanUseMultiSendReader()) { await StartMultiSendAsync().ConfigureAwait(false); return; }
            if (_index < _operation.Commands.Length)
            { _current = await _operation.Commands[_index].ExecuteReaderAsync(Behavior, _operation.Token).ConfigureAwait(false); UpdateCount(); }
        }
        private bool CanUseMultiSendReader() => _operation.Commands.Length != 0 &&
            OceanBaseBatch.CanUseMultiSendReader(_operation.Behavior) && _operation.Session?.UseBatchMultiSend == true;
        private void StartMultiSend()
        {
            var requests = new byte[_operation.Commands.Length][];
            for (var i = 0; i < requests.Length; i++) requests[i] = _operation.Commands[i].BuildBatchMultiSendRequest(_operation.Token);
            _multiRequests = requests; _multiExchange = _operation.Batch.StartMultiSendExchange(_operation); _multiSend = true;
            try { SendNextMultiSendGroup(); StartCurrentMultiSend(); }
            catch (Exception error) { AbortMultiSend(error); throw; }
        }
        private async Task StartMultiSendAsync()
        {
            var requests = new byte[_operation.Commands.Length][];
            for (var i = 0; i < requests.Length; i++) requests[i] = await _operation.Commands[i].BuildBatchMultiSendRequestAsync(_operation.Token).ConfigureAwait(false);
            _multiRequests = requests; _multiExchange = _operation.Batch.StartMultiSendExchange(_operation); _multiSend = true;
            try { await SendNextMultiSendGroupAsync().ConfigureAwait(false); await StartCurrentMultiSendAsync().ConfigureAwait(false); }
            catch (Exception error) { AbortMultiSend(error); throw; }
        }
        private void SendNextMultiSendGroup()
        {
            var exchange = _multiExchange!;
            _operation.Batch.SendMultiSendGroup(_operation, exchange, _multiRequests!, ref _multiNext, out _multiGroupEnd);
        }
        private async Task SendNextMultiSendGroupAsync()
        {
            _multiGroupEnd = await _operation.Batch.SendMultiSendGroupAsync(_operation, _multiExchange!, _multiRequests!, _multiNext, _operation.Token).ConfigureAwait(false);
            _multiNext = _multiGroupEnd;
        }
        private void StartCurrentMultiSend()
        {
            var exchange = _multiExchange!;
            exchange.BatchCommand = _operation.Items[_index]; exchange.BeginBatchResponse();
            var result = new QueryResult(exchange, binary: true, completeExchange: false);
            result.Start(); _current = new OceanBaseDataReader(exchange, result, _operation.Connection, _operation.Session!, Behavior, leaveExchangeOpen: true); UpdateCount();
        }
        private async Task StartCurrentMultiSendAsync()
        {
            var exchange = _multiExchange!;
            exchange.BatchCommand = _operation.Items[_index]; exchange.BeginBatchResponse();
            var result = new QueryResult(exchange, binary: true, completeExchange: false);
            await result.StartAsync(_operation.Token).ConfigureAwait(false);
            _current = new OceanBaseDataReader(exchange, result, _operation.Connection, _operation.Session!, Behavior, leaveExchangeOpen: true); UpdateCount();
        }
        private void AbortMultiSend(Exception error)
        {
            var exchange = _multiExchange; _multiExchange = null; _multiRequests = null; _multiSend = false; _failed = true;
            if (exchange != null) exchange.Fail(error);
        }
        private void CompleteMultiSend()
        {
            var exchange = _multiExchange; _multiExchange = null; _multiRequests = null; _multiSend = false;
            exchange?.Complete();
        }
        private void UpdateCount()
        {
            if (!_failed && _current != null)
                _operation.Items[_index].Affected = _operation.Counts[_index] = _current.RecordsAffected;
        }
        private DbDataReader Current
        {
            get { EnsureOpen(); return _current ?? throw new InvalidOperationException("There is no current result."); }
        }
        internal void Abort() { UpdateCount(); _failed = true; }
        private void EnsureOpen() { if (_closed || _failed) throw new InvalidOperationException("The reader is closed or failed."); }
        public override bool Read()
        {
            EnsureOpen(); if (_current == null) return false;
            try { var result = _current.Read(); UpdateCount(); return result; }
            catch { Close(); throw; }
        }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            EnsureOpen(); if (_current == null) { cancellationToken.ThrowIfCancellationRequested(); return false; }
            try { var result = await _current.ReadAsync(cancellationToken).ConfigureAwait(false); UpdateCount(); return result; }
            catch { await CloseAsync().ConfigureAwait(false); throw; }
        }
        public override bool NextResult()
        {
            EnsureOpen(); if (_current == null) return false;
            if (_multiSend) return NextMultiSendResult();
            try
            {
                while (_current != null)
                {
                    var next = _current.NextResult(); UpdateCount();
                    if (next) { if (!SingleResult) return true; continue; }
                    _current.Dispose(); _current = null; _index++;
                    if (_index == _operation.Commands.Length) return false;
                    Start(); if (!SingleResult) return true;
                }
                return false;
            }
            catch { Close(); throw; }
        }
        public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            EnsureOpen(); if (_current == null) { cancellationToken.ThrowIfCancellationRequested(); return false; }
            if (_multiSend) return await NextMultiSendResultAsync(cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(_operation.Cancel);
            _operation.ReaderToken = cancellationToken;
            try
            {
                while (_current != null)
                {
                    var next = await _current.NextResultAsync(cancellationToken).ConfigureAwait(false); UpdateCount();
                    if (next) { if (!SingleResult) return true; continue; }
                    await _current.DisposeAsync().ConfigureAwait(false); _current = null; _index++;
                    if (_index == _operation.Commands.Length) return false;
                    await StartAsync().ConfigureAwait(false); if (!SingleResult) return true;
                }
                return false;
            }
            catch { await CloseAsync().ConfigureAwait(false); throw; }
            finally { _operation.ReaderToken = default; }
        }
        private bool NextMultiSendResult()
        {
            try
            {
                if (_current!.NextResult()) return true;
                _current.Dispose(); _current = null; _index++;
                if (_index == _operation.Commands.Length) { CompleteMultiSend(); return false; }
                if (_index == _multiGroupEnd) SendNextMultiSendGroup();
                StartCurrentMultiSend(); return true;
            }
            catch (Exception error) { AbortMultiSend(error); Close(); throw; }
        }
        private async Task<bool> NextMultiSendResultAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(_operation.Cancel); _operation.ReaderToken = cancellationToken;
            try
            {
                if (await _current!.NextResultAsync(cancellationToken).ConfigureAwait(false)) return true;
                await _current.DisposeAsync().ConfigureAwait(false); _current = null; _index++;
                if (_index == _operation.Commands.Length) { CompleteMultiSend(); return false; }
                if (_index == _multiGroupEnd)
                {
                    _multiGroupEnd = await _operation.Batch.SendMultiSendGroupAsync(_operation, _multiExchange!, _multiRequests!, _multiNext, cancellationToken).ConfigureAwait(false);
                    _multiNext = _multiGroupEnd;
                }
                await StartCurrentMultiSendAsync().ConfigureAwait(false); return true;
            }
            catch (Exception error) { AbortMultiSend(error); await CloseAsync().ConfigureAwait(false); throw; }
            finally { _operation.ReaderToken = default; }
        }
        public override int RecordsAffected
        {
            get
            {
                UpdateCount(); var total = 0; var any = _operation.Items.Length == 0;
                foreach (var count in _operation.Counts) if (count >= 0) { total = checked(total + count); any = true; }
                return any ? total : -1;
            }
        }
        public override void Close()
        {
            if (_closed) return; _closed = true;
            try { _current?.Dispose(); UpdateCount(); if (_multiExchange != null) AbortMultiSend(new InvalidOperationException("The batch reader was closed with unread multi-send responses.")); }
            finally { _current = null; _operation.Dispose(); if ((_operation.Behavior & CommandBehavior.CloseConnection) != 0) _operation.Connection.CloseSession(_operation.Session!, _operation.OpenGeneration); }
        }
        public override async Task CloseAsync()
        {
            if (_closed) return; _closed = true;
            try { if (_current != null) await _current.DisposeAsync().ConfigureAwait(false); UpdateCount(); if (_multiExchange != null) AbortMultiSend(new InvalidOperationException("The batch reader was closed with unread multi-send responses.")); }
            finally { _current = null; _operation.Dispose(); if ((_operation.Behavior & CommandBehavior.CloseConnection) != 0) await _operation.Connection.CloseSessionAsync(_operation.Session!, _operation.OpenGeneration).ConfigureAwait(false); }
        }
        protected override void Dispose(bool disposing) { if (disposing) Close(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await CloseAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
        public override bool IsClosed => _closed || _failed;
        public override int Depth => 0;
        public override int FieldCount { get { EnsureOpen(); return _current?.FieldCount ?? 0; } }
        public override int VisibleFieldCount { get { EnsureOpen(); return _current?.VisibleFieldCount ?? 0; } }
        public override bool HasRows { get { EnsureOpen(); return _current?.HasRows ?? false; } }
        public override object this[int ordinal] => Current[ordinal];
        public override object this[string name] => Current[name];
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
        public override DataTable? GetSchemaTable() { EnsureOpen(); return _current?.GetSchemaTable(); }
        public ReadOnlyCollection<DbColumn> GetColumnSchema() { EnsureOpen(); return _current?.GetColumnSchema() ?? Array.AsReadOnly(Array.Empty<DbColumn>()); }
        public override string GetName(int ordinal) => Current.GetName(ordinal);
        public override int GetOrdinal(string name) => Current.GetOrdinal(name);
        public override Type GetFieldType(int ordinal) => Current.GetFieldType(ordinal);
        public override string GetDataTypeName(int ordinal) => Current.GetDataTypeName(ordinal);
        public override object GetValue(int ordinal) => Current.GetValue(ordinal);
        public override int GetValues(object[] values) => Current.GetValues(values);
        public override bool IsDBNull(int ordinal) => Current.IsDBNull(ordinal);
        public override bool GetBoolean(int ordinal) => Current.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => Current.GetByte(ordinal);
        public override char GetChar(int ordinal) => Current.GetChar(ordinal);
        public override short GetInt16(int ordinal) => Current.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => Current.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => Current.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => Current.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => Current.GetDouble(ordinal);
        public override decimal GetDecimal(int ordinal) => Current.GetDecimal(ordinal);
        public override DateTime GetDateTime(int ordinal) => Current.GetDateTime(ordinal);
        public override Guid GetGuid(int ordinal) => Current.GetGuid(ordinal);
        public override string GetString(int ordinal) => Current.GetString(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => Current.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => Current.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override Stream GetStream(int ordinal) => Current.GetStream(ordinal);
        public override TextReader GetTextReader(int ordinal) => Current.GetTextReader(ordinal);
        public override Type GetProviderSpecificFieldType(int ordinal) => Current.GetProviderSpecificFieldType(ordinal);
        public override object GetProviderSpecificValue(int ordinal) => Current.GetProviderSpecificValue(ordinal);
        public override int GetProviderSpecificValues(object[] values) => Current.GetProviderSpecificValues(values);
        public override T GetFieldValue<T>(int ordinal) => Current.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => Current.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => Current.IsDBNullAsync(ordinal, cancellationToken);
    }
}
#endif
