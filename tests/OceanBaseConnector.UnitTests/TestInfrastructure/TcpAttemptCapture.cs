using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal sealed class TcpAttemptCapture : IDisposable
    {
        private readonly object _gate = new();
        private readonly MeterListener _listener = new();
        private readonly Activity _scope = new Activity("tcp-attempt-test").Start();
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly List<string> _outcomes = new();
        internal string[] Outcomes { get { lock (_gate) return _outcomes.ToArray(); } }
        internal TcpAttemptCapture(Action<int, string>? observed = null)
        {
            _scope.SetBaggage("tcp-attempt-test", _id);
            _listener.InstrumentPublished = (instrument, listener) =>
            { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.connection.phase.duration") listener.EnableMeasurementEvents(instrument); };
            _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                if (Activity.Current?.GetBaggageItem("tcp-attempt-test") != _id) return;
                var values = tags.ToArray();
                if (!values.Any(tag => tag.Key == "oceanbase.connection.phase" && Equals(tag.Value, "tcp"))) return;
                var outcome = (string)values.Single(tag => tag.Key == "oceanbase.outcome").Value!;
                int count;
                lock (_gate) { _outcomes.Add(outcome); count = _outcomes.Count; }
                observed?.Invoke(count, outcome);
            });
            _listener.Start();
        }
        public void Dispose() { _listener.Dispose(); _scope.Dispose(); }
    }
}
