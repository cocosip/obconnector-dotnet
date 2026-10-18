using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class QueryResult
    {
        private readonly uint? _cursorId;
        private readonly int _fetchSize;
        private bool _cursorActive;
        private bool _cursorNeedsFetch;
        private int _cursorRows;

        private bool CursorMetadataOmitsEof => _cursorId.HasValue && DeprecatedEof && _query.Mode == OceanBaseCompatibilityMode.Oracle;

        // Oracle omits metadata EOF when negotiated; the first fetch must confirm completion.
        private void BeginCursorWithoutEof() { _cursorActive = true; _cursorNeedsFetch = true; }

        private void BeginCursor(byte[] packet)
        {
            OkPacket? ok = null;
            ServerStatusFlags flags;
            if (DeprecatedEof)
            {
                if (packet[0] != 254) throw new ProtocolException("Expected a cursor FE-OK terminator.");
                ok = OkPacket.Parse(packet, _query.Capabilities, allowEofHeader: true);
                if (ok.AffectedRows != 0 || ok.LastInsertId != 0) throw new ProtocolException("Invalid cursor terminator counters.");
                flags = ok.StatusFlags;
            }
            else flags = ReadEof(packet);
            ValidateCursorFlags(flags);
            if ((flags & ServerStatusFlags.LastRowSent) != 0) { End(flags, ok); return; }
            if ((flags & ServerStatusFlags.CursorExists) == 0) throw new ProtocolException("The server did not confirm a read-only cursor.");
            _query.ObserveSessionState(ok);
            _cursorActive = true; _cursorNeedsFetch = true;
        }

        private static void ValidateCursorFlags(ServerStatusFlags flags)
        {
            if ((flags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.PsOutParameters)) != 0)
                throw new ProtocolException("Multiple results and output parameters are not supported for server cursors.");
        }

        private bool EndCursorBatch(ServerStatusFlags flags)
        {
            ValidateCursorFlags(flags);
            if ((flags & ServerStatusFlags.LastRowSent) != 0) { _cursorActive = false; return true; }
            if ((flags & ServerStatusFlags.CursorExists) == 0 || _cursorRows == 0)
                throw new ProtocolException("A cursor batch ended without completion or forward progress.");
            _cursorNeedsFetch = true;
            return false;
        }

        private void TrackCursorRow()
        {
            if (_cursorActive && ++_cursorRows > _fetchSize) throw new ProtocolException("Cursor batch exceeds the requested row count.");
        }

        private byte[] FetchPacket()
        {
            var id = _cursorId!.Value; var count = (uint)_fetchSize;
            return new byte[] { 28, (byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24),
                (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24) };
        }

        private void FetchCursor()
        {
            if (!_cursorNeedsFetch) return;
            _query.Send(FetchPacket(), executesSql: false); _cursorNeedsFetch = false; _cursorRows = 0;
            if (_query.Mode != OceanBaseCompatibilityMode.Oracle) return;
            ValidateFetchHeader(_query.Receive());
            for (var i = 0; i < Columns.Length; i++) ValidateFetchColumn(i, _query.Receive());
            if (!DeprecatedEof) ReadEof(_query.Receive());
        }

        private async Task FetchCursorAsync(CancellationToken token)
        {
            if (!_cursorNeedsFetch) return;
            token.ThrowIfCancellationRequested();
            await _query.SendAsync(FetchPacket(), executesSql: false, readerToken: token).ConfigureAwait(false); _cursorNeedsFetch = false; _cursorRows = 0;
            if (_query.Mode != OceanBaseCompatibilityMode.Oracle) return;
            ValidateFetchHeader(await _query.ReceiveAsync(token).ConfigureAwait(false));
            for (var i = 0; i < Columns.Length; i++) ValidateFetchColumn(i, await _query.ReceiveAsync(token).ConfigureAwait(false));
            if (!DeprecatedEof) ReadEof(await _query.ReceiveAsync(token).ConfigureAwait(false));
        }

        private void ValidateFetchHeader(byte[] packet)
        {
            var cursor = new ByteCursor(packet);
            if (cursor.ReadLengthEncodedInteger() != Columns.Length || cursor.HasRemaining)
                throw new ProtocolException("Cursor fetch column count changed.");
            _metadataBytes = 0;
        }

        private void ValidateFetchColumn(int ordinal, byte[] packet)
        {
            var column = ParseColumn(packet); var expected = Columns[ordinal];
            if (column.TypeCode != expected.TypeCode || column.Charset != expected.Charset || column.Flags != expected.Flags ||
                column.Length != expected.Length || column.Precision != expected.Precision || column.Scale != expected.Scale ||
                column.Name != expected.Name || column.OriginalName != expected.OriginalName || column.Database != expected.Database ||
                column.Table != expected.Table || column.OriginalTable != expected.OriginalTable)
                throw new ProtocolException("Cursor fetch metadata changed.");
        }

        private byte[]?[]? ReadBufferedRow()
        {
            do
            {
                FetchCursor(); var row = ParseRow(_query.Receive());
                if (row != null || !_cursorNeedsFetch) return row;
            } while (true);
        }

        private async Task<byte[]?[]?> ReadBufferedRowAsync(CancellationToken token)
        {
            do
            {
                await FetchCursorAsync(token).ConfigureAwait(false); var row = ParseRow(await _query.ReceiveAsync(token).ConfigureAwait(false));
                if (row != null || !_cursorNeedsFetch) return row;
            } while (true);
        }
    }
}
