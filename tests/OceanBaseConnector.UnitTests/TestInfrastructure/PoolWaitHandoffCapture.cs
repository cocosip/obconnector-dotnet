using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    // Interrupt the actual semaphore winner after its notification, before its next rent decision.
    internal sealed class PoolWaitHandoffCapture : IDisposable
    {
        private readonly AsyncLocal<Attempt?> _current = new();
        private readonly MeterListener _listener = new();
        private Attempt? _winner;
        internal TaskCompletionSource<Attempt> Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PoolWaitHandoffCapture()
        {
            _listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "OceanBaseConnector" &&
                    (instrument.Name == "oceanbase.client.pool.waiters" || instrument.Name == "oceanbase.client.pool.wait.duration"))
                    owner.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (value == 1) _current.Value?.Waiting.TrySetResult();
            });
            _listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
            {
                var attempt = _current.Value;
                if (attempt == null || Interlocked.CompareExchange(ref _winner, attempt, null) != null) return;
                attempt.Interrupt();
                Interrupted.TrySetResult(attempt);
            });
            _listener.Start();
        }

        internal Task<T> Run<T>(Attempt attempt, Func<Task<T>> operation) => Task.Run(async () =>
        {
            _current.Value = attempt;
            try { return await operation(); }
            finally { _current.Value = null; }
        });

        public void Dispose() => _listener.Dispose();

        internal sealed class Attempt(Action interrupt)
        {
            internal Action Interrupt { get; } = interrupt;
            internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
