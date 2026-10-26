using System;
using System.Diagnostics;

namespace OceanBaseConnector.Internal
{
    internal static partial class DriverDiagnostics
    {
        internal static SessionIo? StartSessionIo(OceanBaseCompatibilityMode mode) =>
            ProtocolIoDuration?.Enabled == true ? new SessionIo(mode) : null;

        internal sealed class SessionIo
        {
            private readonly object _gate = new object();
            private readonly string _mode;
            private long _reads, _writes;
            private bool _readUsed, _writeUsed, _finished, _published;
            private int _pending;
            private string _outcome = "success";
            internal SessionIo(OceanBaseCompatibilityMode mode) => _mode = mode == OceanBaseCompatibilityMode.Oracle ? "oracle" : "mysql";
            internal long Start()
            {
                lock (_gate)
                {
                    if (_finished) return 0;
                    _pending++; return Stopwatch.GetTimestamp();
                }
            }
            internal void Stop(long started, bool write)
            {
                if (started == 0) return;
                var ticks = Stopwatch.GetTimestamp() - started;
                bool publish;
                lock (_gate)
                {
                    if (write) { _writes += ticks; _writeUsed = true; }
                    else { _reads += ticks; _readUsed = true; }
                    _pending--; publish = TakePublication();
                }
                if (publish) Publish();
            }
            internal void Finish(Exception? error)
            {
                bool publish;
                lock (_gate)
                {
                    if (_finished) return;
                    _finished = true; _outcome = Outcome(error); publish = TakePublication();
                }
                if (publish) Publish();
            }
            private bool TakePublication()
            {
                // Concurrent reader disposal may finish the exchange before its pending read returns.
                if (!_finished || _pending != 0 || _published) return false;
                _published = true; return true;
            }
            private void Publish()
            {
                if (_readUsed) Record("read", _reads);
                if (_writeUsed) Record("write", _writes);
            }
            private void Record(string direction, long ticks)
            {
                var tags = new TagList { { "oceanbase.protocol.direction", direction },
                    { "oceanbase.compatibility_mode", _mode }, { "oceanbase.outcome", _outcome } };
                var previous = Activity.Current;
                try { ProtocolIoDuration?.Record(ticks / (double)Stopwatch.Frequency, tags); }
                catch { }
                finally { Restore(previous); }
            }
        }
    }
}
