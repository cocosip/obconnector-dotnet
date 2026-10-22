using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        internal void EnsureOraclePrepareExecute()
        {
            if (!_session.UseOraclePrepareExecute || Mode != OceanBaseCompatibilityMode.Oracle ||
                _session.Info?.ProductVersion is not Version version || version < new Version(2, 2, 76))
                throw new NotSupportedException("Oracle PrepareExecute requires explicit opt-in and OceanBase version 2.2.76 or later.");
        }

        internal Task EnsureOraclePrepareExecuteAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EnsureOraclePrepareExecute();
            return Task.CompletedTask;
        }

        internal void EnsureCursorOffset()
        {
            if (Mode != OceanBaseCompatibilityMode.Oracle ||
                _session.Info?.ProductVersion is not Version version || version < new Version(2, 2, 5))
                throw new NotSupportedException("Offset fetch requires OceanBase Oracle mode version 2.2.5 or later.");
        }

        internal Task EnsureCursorOffsetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EnsureCursorOffset();
            return Task.CompletedTask;
        }
    }
}
