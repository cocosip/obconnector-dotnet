using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal
{
    internal static partial class DriverDiagnostics
    {
        internal static ConnectionPhase? StartConnectionPhase(string phase) =>
            ConnectionPhaseDuration?.Enabled == true ? new ConnectionPhase(phase) : null;

        internal static T MeasureConnectionPhase<T>(string phase, Func<T> action)
        {
            var measurement = StartConnectionPhase(phase);
            try
            {
                var result = action(); measurement?.Finish(null); return result;
            }
            catch (Exception error) { measurement?.Finish(error); throw; }
        }

        internal static async Task<T> MeasureConnectionPhaseAsync<T>(string phase, Func<Task<T>> action)
        {
            var measurement = StartConnectionPhase(phase);
            try
            {
                var result = await action().ConfigureAwait(false); measurement?.Finish(null); return result;
            }
            catch (Exception error) { measurement?.Finish(error); throw; }
        }

        internal sealed class ConnectionPhase
        {
            private readonly string _phase;
            private readonly long _started = Stopwatch.GetTimestamp();
            private long _stopped;
            private int _finished;
            internal ConnectionPhase(string phase) => _phase = phase;
            internal void Stop() => Interlocked.CompareExchange(ref _stopped, Stopwatch.GetTimestamp(), 0);
            internal void Finish(Exception? error, bool elapsed = false)
            {
                if (Interlocked.Exchange(ref _finished, 1) != 0) return;
                Stop();
                var cause = error is OceanBaseException databaseError ? databaseError.InnerException ?? error : error;
                var outcome = cause is OperationCanceledException ? "canceled" : cause is TimeoutException ? "timeout" :
                    error != null ? "error" : elapsed ? "elapsed" : "success";
                var tags = new TagList { { "oceanbase.connection.phase", _phase }, { "oceanbase.outcome", outcome } };
                var seconds = (Interlocked.Read(ref _stopped) - _started) / (double)Stopwatch.Frequency;
                var previous = Activity.Current;
                try { ConnectionPhaseDuration?.Record(seconds, tags); }
                catch { }
                finally { Restore(previous); }
            }
        }
    }
}
