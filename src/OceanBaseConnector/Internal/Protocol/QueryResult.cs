using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class QueryResult
    {
        private readonly CommandExchange _query;
        private readonly bool _completeExchange;
        private byte[]?[]? _pending;
        private bool _ended;
        private bool _more;
        private long _affected = -1;
        private bool _faulted;
        private int _metadataBytes;
        internal QueryResult(CommandExchange query, bool binary = false, bool completeExchange = true, bool sequential = false, uint? cursorId = null, int fetchSize = 0)
        {
            _query = query; IsBinary = binary; _completeExchange = completeExchange; _sequential = sequential; _cursorId = cursorId; _fetchSize = fetchSize;
            if (cursorId.HasValue) query.OwnCursor();
        }
        internal bool IsBinary { get; }
        internal string? SessionTimeZone { get; private set; }
        private NationalCharacterSet? NationalCharacterSet { get; set; }
        internal ResultColumn[] Columns { get; private set; } = Array.Empty<ResultColumn>();
        internal byte[]?[]? Row { get; private set; }
        internal bool HasRows { get; private set; }
        internal int RecordsAffected => checked((int)_affected);
        private bool DeprecatedEof => (_query.Capabilities & CapabilityFlags.ClientDeprecateEof) != 0;

        internal void Start()
        {
            try
            {
                SessionTimeZone = _query.CaptureResultTimeZone();
                NationalCharacterSet = _query.CaptureResultNationalCharacterSet();
                if (!Header(_query.Receive())) return;
                for (var i = 0; i < Columns.Length; i++) Columns[i] = ParseColumn(_query.Receive());
                if (CursorMetadataOmitsEof) BeginCursorWithoutEof();
                else if (_cursorId.HasValue) BeginCursor(_query.Receive());
                else if (!DeprecatedEof) EndColumns(_query.Receive());
                if (_ended) return;
                if (_sequential) { _pendingSequential = OpenSequential(); HasRows = _pendingSequential != null; }
                else { _pending = ReadBufferedRow(); HasRows = _pending != null; }
            }
            catch (Exception error) { throw Fault(error); }
        }

        internal async Task StartAsync(CancellationToken token)
        {
            try
            {
                SessionTimeZone = _query.CaptureResultTimeZone();
                NationalCharacterSet = _query.CaptureResultNationalCharacterSet();
                if (!Header(await _query.ReceiveAsync(token).ConfigureAwait(false))) return;
                for (var i = 0; i < Columns.Length; i++) Columns[i] = ParseColumn(await _query.ReceiveAsync(token).ConfigureAwait(false));
                if (CursorMetadataOmitsEof) BeginCursorWithoutEof();
                else if (_cursorId.HasValue) BeginCursor(await _query.ReceiveAsync(token).ConfigureAwait(false));
                else if (!DeprecatedEof) EndColumns(await _query.ReceiveAsync(token).ConfigureAwait(false));
                if (_ended) return;
                if (_sequential) { _pendingSequential = await OpenSequentialAsync(token).ConfigureAwait(false); HasRows = _pendingSequential != null; }
                else { _pending = await ReadBufferedRowAsync(token).ConfigureAwait(false); HasRows = _pending != null; }
            }
            catch (Exception error) { throw Fault(error); }
        }

        private bool Header(byte[] packet)
        {
            Row = null;
            _pending = null;
            HasRows = false;
            _ended = false;
            _metadataBytes = 0;
            if (OkPacket.IsOk(packet))
            {
                if (_cursorId.HasValue) throw new ProtocolException("A cursor execution returned no result columns.");
                Columns = Array.Empty<ResultColumn>();
                var ok = OkPacket.Parse(packet, _query.Capabilities);
                _affected = checked(Math.Max(0, _affected) + ok.AffectedRows);
                End(ok.StatusFlags, ok);
                return false;
            }
            if (packet[0] == 251) throw new ProtocolException("LOCAL INFILE is not permitted.");
            var cursor = new ByteCursor(packet);
            var count = cursor.ReadLengthEncodedInteger();
            if (cursor.HasRemaining || count < 1 || count > 4096) throw new ProtocolException("Invalid or excessive result column count.");
            Columns = new ResultColumn[(int)count];
            return true;
        }

        internal static ServerStatusFlags ReadEof(byte[] packet)
        {
            if (packet.Length != 5 || packet[0] != 254) throw new ProtocolException("Expected a protocol 4.1 EOF packet.");
            return (ServerStatusFlags)(packet[3] | (packet[4] << 8));
        }
        private static void EndColumns(byte[] packet) => ReadEof(packet);
        private ResultColumn ParseColumn(byte[] packet)
        {
            if (packet.Length > 4 * 1024 * 1024 - _metadataBytes) throw new ProtocolException("Result metadata size limit exceeded.");
            _metadataBytes += packet.Length;
            return ResultColumn.Parse(packet, _query.Mode, SessionTimeZone, NationalCharacterSet);
        }
        private void End(ServerStatusFlags flags, OkPacket? ok = null)
        {
            _query.ObserveSessionState(ok);
            if (_cursorActive && !EndCursorBatch(flags)) return;
            _ended = true;
            _more = (flags & ServerStatusFlags.MoreResultsExists) != 0;
            if (!_completeExchange)
            {
                if (_more) throw new ProtocolException("The session state query returned multiple results.");
                _query.ResponseComplete();
            }
            else if (!_more) _query.Complete();
        }

        private byte[]?[]? ParseRow(byte[] packet)
        {
            // A canonical FE length prefix in a row represents at least 16 MiB of field data.
            if (packet[0] == 254 && packet.Length < PacketChannel.MaxFramePayload)
            {
                if (DeprecatedEof)
                {
                    var ok = OkPacket.Parse(packet, _query.Capabilities, allowEofHeader: true);
                    if (ok.AffectedRows != 0 || ok.LastInsertId != 0) throw new ProtocolException("Invalid result terminator counters.");
                    End(ok.StatusFlags, ok);
                }
                else End(ReadEof(packet));
                return null;
            }
            var cursor = new ByteCursor(packet);
            var row = new byte[]?[Columns.Length];
            if (IsBinary)
            {
                if (cursor.ReadUInt8() != 0) throw new ProtocolException("Invalid binary row header.");
                var nulls = cursor.ReadBytes((Columns.Length + 9) / 8);
                if ((nulls[0] & 3) != 0) throw new ProtocolException("Invalid binary row reserved bits.");
                for (var i = 0; i < row.Length; i++)
                    if ((nulls[(i + 2) / 8] & (1 << ((i + 2) % 8))) == 0) row[i] = Columns[i].ReadBinaryField(cursor);
            }
            else for (var i = 0; i < row.Length; i++) row[i] = cursor.ReadNullableLengthEncodedBytes();
            if (cursor.HasRemaining) throw new ProtocolException("Trailing row bytes.");
            TrackCursorRow();
            return row;
        }

        internal bool Read()
        {
            if (_sequential) return ReadSequential();
            EnsureValid();
            try
            {
                Row = null;
                if (_ended) return false;
                _query.EnsureReading();
                if (_pending != null) { Row = _pending; _pending = null; return true; }
                Row = ReadBufferedRow();
                return Row != null;
            }
            catch (Exception error) { throw Fault(error); }
        }

        internal async Task<bool> ReadAsync(CancellationToken token)
        {
            if (_sequential) return await ReadSequentialAsync(token).ConfigureAwait(false);
            EnsureValid();
            try
            {
                Row = null;
                if (_ended) return false;
                token.ThrowIfCancellationRequested();
                _query.EnsureReading();
                if (_pending != null) { Row = _pending; _pending = null; return true; }
                Row = await ReadBufferedRowAsync(token).ConfigureAwait(false);
                return Row != null;
            }
            catch (Exception error) { throw Fault(error); }
        }

        internal bool NextResult()
        {
            while (Read()) { }
            if (!_more) { ClearColumns(); return false; }
            Start();
            return true;
        }

        internal async Task<bool> NextResultAsync(CancellationToken token)
        {
            while (await ReadAsync(token).ConfigureAwait(false)) { }
            if (!_more) { ClearColumns(); return false; }
            await StartAsync(token).ConfigureAwait(false);
            return true;
        }

        internal void EnsureValid()
        {
            if (_faulted) throw new InvalidOperationException("The reader's command exchange has failed.");
            try { _query.EnsureReading(); }
            catch (Exception error) { throw Fault(error); }
        }

        private void ClearColumns() { Columns = Array.Empty<ResultColumn>(); HasRows = false; }

        private Exception Fault(Exception error)
        {
            _faulted = true;
            Row = _pending = null;
            InvalidateRows();
            return _query.Fail(error);
        }
    }
}
