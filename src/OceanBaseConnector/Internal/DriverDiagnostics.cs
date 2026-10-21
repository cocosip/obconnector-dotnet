using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal static class DriverDiagnostics
    {
        private static readonly string Version = typeof(OceanBaseConnection).Assembly.GetName().Version!.ToString();
        private static readonly ActivitySource? Source = Create(() => new ActivitySource("OceanBaseConnector", Version));
        private static readonly Meter? Meter = Create(() => new Meter("OceanBaseConnector", Version));
        private static readonly Counter<long>? Operations = Create(() => Meter?.CreateCounter<long>("oceanbase.client.operations", "{operation}"));
        private static readonly Histogram<double>? Duration = Create(() => Meter?.CreateHistogram<double>("oceanbase.client.operation.duration", "s"));

        private static T? Create<T>(Func<T?> create) where T : class
        {
            var previous = Activity.Current;
            try { return create(); }
            catch { return null; }
            finally { Restore(previous); }
        }

        private static void Restore(Activity? previous)
        {
            try { Activity.Current = previous; }
            catch { } // Activity.CurrentChanged is also application callback code.
        }

        internal static Operation? Start(string operation)
        {
            if (Source?.HasListeners() != true && Operations?.Enabled != true && Duration?.Enabled != true) return null;
            var previous = Activity.Current;
            Activity? activity = null;
            try { activity = Source?.CreateActivity(SpanName(operation), ActivityKind.Client); activity?.Start(); }
            catch { }
            finally { Restore(previous); }
            return activity == null && Operations?.Enabled != true && Duration?.Enabled != true ? null : new Operation(operation, activity);
        }

        private static string SpanName(string operation) => operation switch
        {
            "open" => "oceanbase.open",
            "execute" => "oceanbase.execute",
            "prepare" => "oceanbase.prepare",
            "change_database" => "oceanbase.change_database",
            "cursor.fetch" => "oceanbase.cursor.fetch",
            "transaction.begin" => "oceanbase.transaction.begin",
            "transaction.commit" => "oceanbase.transaction.commit",
            "transaction.rollback" => "oceanbase.transaction.rollback",
            "transaction.savepoint" => "oceanbase.transaction.savepoint",
            "xa.start" => "oceanbase.xa.start",
            "xa.end" => "oceanbase.xa.end",
            "xa.prepare" => "oceanbase.xa.prepare",
            "xa.commit" => "oceanbase.xa.commit",
            "xa.rollback" => "oceanbase.xa.rollback",
            "xa.recover" => "oceanbase.xa.recover",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        internal sealed class Operation
        {
            private readonly string _name;
            private readonly Activity? _activity;
            private readonly long _started = Stopwatch.GetTimestamp();
            private int _finished;
            internal Operation(string name, Activity? activity) { _name = name; _activity = activity; }
            internal ActivityContext Context => _activity?.Context ?? default;
            internal void Finish(OceanBaseCompatibilityMode? mode, Exception? error)
            {
                if (Interlocked.Exchange(ref _finished, 1) != 0) return;
                var databaseError = error as OceanBaseException;
                var cause = databaseError?.InnerException ?? error;
                var outcome = cause is OperationCanceledException ? "canceled" : cause is TimeoutException ? "timeout" : error == null ? "success" : "error";
                var tags = new TagList
                {
                    { "db.operation.name", _name },
                    { "oceanbase.compatibility_mode", mode == OceanBaseCompatibilityMode.Oracle ? "oracle" : mode == OceanBaseCompatibilityMode.MySql ? "mysql" : "unknown" },
                    { "oceanbase.outcome", outcome },
                    { "oceanbase.execution_outcome_unknown", databaseError?.IsExecutionOutcomeUnknown ?? false }
                };
                var elapsed = (Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency;
                // Listener callbacks are application code; each channel must survive the others failing.
                var previous = Activity.Current;
                try
                {
                    if (_activity != null)
                    {
                        _activity.SetTag("db.system.name", "oceanbase");
                        foreach (var tag in tags) _activity.SetTag(tag.Key, tag.Value);
                        _activity.SetStatus(error == null ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                        _activity.Stop();
                    }
                }
                catch { }
                finally { Restore(previous); }
                try { Operations?.Add(1, tags); } catch { } finally { Restore(previous); }
                try { Duration?.Record(elapsed, tags); } catch { } finally { Restore(previous); }
            }
        }
    }
}
