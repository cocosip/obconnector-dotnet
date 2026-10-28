using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class ConnectionPhaseDiagnosticsTests
    {
        private static string Configuration(int port, bool pooled = false) =>
            $"Server=127.0.0.1;Port={port};User ID=u;Password=private-password;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=1";
        private static Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token = default) =>
            asynchronous ? connection.OpenAsync(token) : Task.Run(connection.Open, token);
        private sealed class Capture : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener { MeasurementsCompleted = (_, _) => { } };
            private readonly ConcurrentQueue<(double Value, string? Unit, KeyValuePair<string, object?>[] Tags)> _samples = new();
            internal (string Phase, string Outcome)[] Samples => _samples.Select(sample =>
            {
                Assert.Equal("s", sample.Unit); Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0);
                var pairs = sample.Tags.ToDictionary(pair => pair.Key, pair => pair.Value);
                Assert.Equal(2, pairs.Count);
                return (Assert.IsType<string>(pairs["oceanbase.connection.phase"]), Assert.IsType<string>(pairs["oceanbase.outcome"]));
            }).ToArray();
            internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string[] Phases => Samples.Select(sample => sample.Phase).ToArray();
            internal double[] Seconds => _samples.Select(sample => sample.Value).ToArray();
            internal Capture()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && (instrument.Name == MetricName || instrument.Name == "oceanbase.client.pool.waiters")) listener.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<long>((_, value, _, _) => { if (value == 1) Waiting.TrySetResult(); });
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                    _samples.Enqueue((value, instrument.Unit, tags.ToArray())));
                _listener.Start();
            }
            internal void Clear() => _samples.Clear();
            public void Dispose() => _listener.Dispose();
        }
        private const string MetricName = "oceanbase.client.connection.phase.duration";
    }
}
