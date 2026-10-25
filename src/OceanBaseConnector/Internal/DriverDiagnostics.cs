using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal static partial class DriverDiagnostics
    {
        private static readonly string Version = typeof(OceanBaseConnection).Assembly.GetName().Version!.ToString();
        private static readonly ActivitySource? Source = Create(() => new ActivitySource("OceanBaseConnector", Version));
        private static readonly Meter? Meter = Create(() => new Meter("OceanBaseConnector", Version));
        private static readonly Counter<long>? Operations = Create(() => Meter?.CreateCounter<long>("oceanbase.client.operations", "{operation}"));
        private static readonly Histogram<double>? Duration = Create(() => Meter?.CreateHistogram<double>("oceanbase.client.operation.duration", "s"));
        private static readonly UpDownCounter<long>? PoolSessions = Create(() => Meter?.CreateUpDownCounter<long>("oceanbase.client.pool.sessions", "{session}"));
        private static readonly Counter<long>? PoolBorrows = Create(() => Meter?.CreateCounter<long>("oceanbase.client.pool.borrowed", "{borrow}"));
        private static readonly UpDownCounter<long>? PoolWaiters = Create(() => Meter?.CreateUpDownCounter<long>("oceanbase.client.pool.waiters", "{waiter}"));
        private static readonly Counter<long>? PoolDiscards = Create(() => Meter?.CreateCounter<long>("oceanbase.client.pool.discarded", "{session}"));
        private static readonly Counter<long>? PoolResetFailures = Create(() => Meter?.CreateCounter<long>("oceanbase.client.pool.reset.failures", "{failure}"));
        private static readonly Histogram<double>? PoolWait = Create(() => Meter?.CreateHistogram<double>("oceanbase.client.pool.wait.duration", "s"));
        private static readonly Histogram<double>? ConnectionPhaseDuration = Create(() => Meter?.CreateHistogram<double>("oceanbase.client.connection.phase.duration", "s"));

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

        internal static void PoolSessionAdded() => Add(PoolSessions, 1);
        internal static void PoolSessionRemoved() => Add(PoolSessions, -1);
        internal static void PoolBorrowed() => Add(PoolBorrows, 1);
        internal static void PoolWaiterAdded() => Add(PoolWaiters, 1);
        internal static void PoolWaiterRemoved() => Add(PoolWaiters, -1);
        internal static void PoolDiscarded() => Add(PoolDiscards, 1);
        internal static void PoolResetFailed() => Add(PoolResetFailures, 1);
        internal static void PoolWaitDuration(double seconds) => Record(PoolWait, seconds);

        private static void Add(Counter<long>? instrument, long value)
        {
            var previous = Activity.Current;
            try { instrument?.Add(value); }
            catch { }
            finally { Restore(previous); }
        }

        private static void Add(UpDownCounter<long>? instrument, long value)
        {
            var previous = Activity.Current;
            try { instrument?.Add(value); }
            catch { }
            finally { Restore(previous); }
        }

        private static void Record(Histogram<double>? instrument, double value)
        {
            var previous = Activity.Current;
            try { instrument?.Record(value); }
            catch { }
            finally { Restore(previous); }
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
