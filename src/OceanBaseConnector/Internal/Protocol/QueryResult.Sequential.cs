using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed partial class QueryResult
    {
        private readonly bool _sequential;
        private SequentialRow? _pendingSequential;
        private byte[]? _rowScratch;
        internal SequentialRow? Sequential { get; private set; }

        private SequentialRow? OpenSequential()
        {
            while (true)
            {
                FetchCursor();
                var payload = _query.OpenResponse();
                if (payload.FirstByte == 254 && payload.FirstFrameLength < PacketChannel.MaxFramePayload)
                {
                    ParseRow(payload.ReadAll()); payload.Dispose();
                    if (_cursorNeedsFetch) continue;
                    return null;
                }
                TrackCursorRow();
                var row = new SequentialRow(payload, Columns, IsBinary, _rowScratch ??= new byte[8192]); row.Start(); return row;
            }
        }

        private async Task<SequentialRow?> OpenSequentialAsync(CancellationToken token)
        {
            while (true)
            {
                await FetchCursorAsync(token).ConfigureAwait(false);
                var payload = await _query.OpenResponseAsync(token).ConfigureAwait(false);
                if (payload.FirstByte == 254 && payload.FirstFrameLength < PacketChannel.MaxFramePayload)
                {
                    ParseRow(await payload.ReadAllAsync(token).ConfigureAwait(false)); payload.Dispose();
                    if (_cursorNeedsFetch) continue;
                    return null;
                }
                TrackCursorRow();
                var row = new SequentialRow(payload, Columns, IsBinary, _rowScratch ??= new byte[8192]); await row.StartAsync(token).ConfigureAwait(false); return row;
            }
        }

        private bool ReadSequential()
        {
            EnsureValid();
            try
            {
                if (Sequential != null) { Sequential.Finish(); Sequential = null; }
                if (_ended) return false;
                if (_pendingSequential != null) { Sequential = _pendingSequential; _pendingSequential = null; }
                else Sequential = OpenSequential();
                return Sequential != null;
            }
            catch (System.Exception error) { throw Fault(error); }
        }

        private async Task<bool> ReadSequentialAsync(CancellationToken token)
        {
            EnsureValid();
            try
            {
                token.ThrowIfCancellationRequested();
                if (Sequential != null) { await Sequential.FinishAsync(token).ConfigureAwait(false); Sequential = null; }
                if (_ended) return false;
                if (_pendingSequential != null) { Sequential = _pendingSequential; _pendingSequential = null; }
                else Sequential = await OpenSequentialAsync(token).ConfigureAwait(false);
                return Sequential != null;
            }
            catch (System.Exception error) { throw Fault(error); }
        }

        internal void InvalidateRows()
        {
            Sequential?.Invalidate(); _pendingSequential?.Invalidate();
            Sequential = _pendingSequential = null;
        }
    }
}
