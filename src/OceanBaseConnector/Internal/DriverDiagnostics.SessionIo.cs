using System;
using System.Diagnostics;

namespace OceanBaseConnector.Internal
{
    internal static partial class DriverDiagnostics
    {
        internal static SessionIo? StartSessionIo(OceanBaseCompatibilityMode mode)
        {
            var protocol = ProtocolIoDuration?.Enabled == true;
            var source = SourceReadDuration?.Enabled == true;
            return protocol || source ? new SessionIo(mode, protocol, source) : null;
        }

        internal sealed class SessionIo
        {
            private readonly object _gate = new object();
            private readonly string _mode;
            internal bool ProtocolEnabled { get; }
            internal bool SourceReadsEnabled { get; }
            private long _reads, _writes;
            private long _parameterReads, _localInfileReads;
            private bool _parameterUsed, _localInfileUsed;
            private bool _readUsed, _writeUsed, _finished, _published;
            private int _pending;
            private string _outcome = "success";
            internal SessionIo(OceanBaseCompatibilityMode mode, bool protocol, bool source)
            {
                _mode = mode == OceanBaseCompatibilityMode.Oracle ? "oracle" : "mysql";
                ProtocolEnabled = protocol; SourceReadsEnabled = source;
            }
            internal long Start() => ProtocolEnabled ? Begin() : 0;
            internal long StartSourceRead() => SourceReadsEnabled ? Begin() : 0;
            private long Begin()
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
            internal void StopSourceRead(long started, bool localInfile)
            {
                if (started == 0) return;
                var ticks = Stopwatch.GetTimestamp() - started;
                bool publish;
                lock (_gate)
                {
                    if (localInfile) { _localInfileReads += ticks; _localInfileUsed = true; }
                    else { _parameterReads += ticks; _parameterUsed = true; }
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
                if (_parameterUsed) RecordSource("parameter", _parameterReads);
                if (_localInfileUsed) RecordSource("local_infile", _localInfileReads);
            }
            private void RecordSource(string kind, long ticks)
            {
                var tags = new TagList { { "oceanbase.source.kind", kind },
                    { "oceanbase.compatibility_mode", _mode }, { "oceanbase.outcome", _outcome } };
                var previous = Activity.Current;
                try { SourceReadDuration?.Record(ticks / (double)Stopwatch.Frequency, tags); }
                catch { }
                finally { Restore(previous); }
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
