using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class DiagnosticsTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SessionIoSummariesPreserveSuccessAndConfirmedError(bool asynchronous, bool error)
        {
            var server = Server(async channel =>
            {
                await Request(channel, "private sql"); await channel.WritePacketAsync(error ? Error() : Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var capture = new IoCapture();
                using var parent = new Activity("application").Start();
                using var command = connection.CreateCommand(); command.CommandText = "private sql";
                if (error)
                {
                    var failure = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.Equal(1064, failure.ServerErrorCode); Assert.False(failure.IsExecutionOutcomeUnknown);
                }
                else Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                AssertIo(capture, error ? "error" : "success"); Assert.Same(parent, Activity.Current);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SessionIoStreamingPublishesOnlyAtCompletion(bool asynchronous, bool sequential)
        {
            var server = Server(async channel =>
            {
                await Request(channel, "private row"); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 118, 1, 118, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 });
                await channel.WritePacketAsync(new byte[] { 3, 65, 66, 67 });
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var capture = new IoCapture();
                using var command = connection.CreateCommand(); command.CommandText = "private row";
                var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                Assert.Empty(capture.Samples); Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                using (var text = reader.GetTextReader(0)) Assert.Equal("ABC", asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                Assert.Empty(capture.Samples); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                AssertIo(capture, "success"); reader.Close(); Assert.Equal(2, capture.Samples.Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static void AssertIo(IoCapture capture, string outcome)
        {
            var samples = capture.Samples; Assert.Equal(2, samples.Length);
            Assert.Equal(new[] { "read", "write" }, samples.Select(x => x.Tags["oceanbase.protocol.direction"]).OrderBy(x => x));
            Assert.All(samples, sample =>
            {
                Assert.Equal("s", sample.Unit); Assert.True(double.IsFinite(sample.Seconds) && sample.Seconds >= 0);
                Assert.Equal(3, sample.Tags.Count); Assert.Equal("oracle", sample.Tags["oceanbase.compatibility_mode"]);
                Assert.Equal(outcome, sample.Tags["oceanbase.outcome"]);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionIoListenersRunAfterReleaseAndCannotPoisonContext(bool throwing)
        {
            var server = Server(async channel =>
            {
                await Request(channel, "first"); await channel.WritePacketAsync(Ok()); channel.BeginCommand();
                await Request(channel, "after"); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var capture = new IoCapture();
                using var parent = new Activity("application").Start(); var entered = false; var nested = false;
                capture.OnSample = () =>
                {
                    if (entered) return;
                    entered = true;
                    using var next = connection.CreateCommand(); next.CommandText = "after";
                    nested = next.ExecuteNonQuery() == 0;
                    Activity.Current = new Activity("observer");
                    if (throwing) throw new InvalidOperationException("private observer detail");
                };
                using var command = connection.CreateCommand(); command.CommandText = "first";
                Assert.Equal(0, command.ExecuteNonQuery()); Assert.True(nested); Assert.Same(parent, Activity.Current);
                Assert.Equal(4, capture.Samples.Length); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SessionIoTransportFailuresRetainUnknownOutcome(bool asynchronous, bool write)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(_ => release.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var capture = new IoCapture();
                var fault = new ProtocolFaultStream(Array.Empty<byte>(), write ? 5 : int.MaxValue);
                using (var lease = connection.GetOpenSession().Acquire()) { lease.Channel.ReplaceUnderlyingStream(fault); lease.Complete(); }
                using var command = connection.CreateCommand(); command.CommandText = "private fault";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Equal(write ? 1 : 2, capture.Samples.Length);
                Assert.All(capture.Samples, sample => Assert.Equal("error", sample.Tags["oceanbase.outcome"]));
                Assert.Contains(capture.Samples, sample => Equals(sample.Tags["oceanbase.protocol.direction"], "write"));
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("caller")]
        [InlineData("command")]
        [InlineData("timeout")]
        public async Task SessionIoPartialAsyncWriteUsesMappedCancellationOutcome(string cause)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(_ => release.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var capture = new IoCapture(); using var cancellation = new CancellationTokenSource();
                var fault = new ProtocolFaultStream(Array.Empty<byte>(), 5) { PauseAfterPartialWrite = true };
                using (var lease = connection.GetOpenSession().Acquire()) { lease.Channel.ReplaceUnderlyingStream(fault); lease.Complete(); }
                using var command = connection.CreateCommand(); command.CommandText = "private partial"; command.CommandTimeout = cause == "timeout" ? 1 : 10;
                var pending = command.ExecuteNonQueryAsync(cancellation.Token);
                await fault.PartialWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(capture.Samples);
                if (cause == "caller") cancellation.Cancel(); else if (cause == "command") command.Cancel();
                var failure = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(failure.IsExecutionOutcomeUnknown);
                var sample = Assert.Single(capture.Samples);
                Assert.Equal(cause == "timeout" ? "timeout" : "canceled", sample.Tags["oceanbase.outcome"]);
                Assert.Equal("write", sample.Tags["oceanbase.protocol.direction"]);
                Assert.Equal(ConnectionState.Broken, connection.State);
                if (cause == "caller") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(failure.InnerException).CancellationToken);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql)]
        [InlineData(OceanBaseCompatibilityMode.Oracle)]
        public void SessionIoPendingMeasurementsOutliveFinishAndExcludeIdleTime(OceanBaseCompatibilityMode mode)
        {
            using var capture = new IoCapture();
            var measurement = DriverDiagnostics.StartSessionIo(mode)!;
            var first = measurement.Start(); measurement.Stop(first, write: true);
            var idle = Stopwatch.StartNew(); Thread.Sleep(150); idle.Stop();
            var pending = measurement.Start(); measurement.Finish(new TimeoutException("private"));
            measurement.Finish(null); Assert.Empty(capture.Samples);
            Assert.Equal(0, measurement.Start()); measurement.Stop(pending, write: false);
            Assert.Equal(2, capture.Samples.Length);
            var sample = Assert.Single(capture.Samples, value => Equals(value.Tags["oceanbase.protocol.direction"], "write"));
            Assert.True(sample.Seconds < idle.Elapsed.TotalSeconds);
            Assert.All(capture.Samples, value =>
            {
                Assert.Equal("timeout", value.Tags["oceanbase.outcome"]);
                Assert.Equal(mode == OceanBaseCompatibilityMode.Oracle ? "oracle" : "mysql", value.Tags["oceanbase.compatibility_mode"]);
            });
            measurement.Finish(null); Assert.Equal(2, capture.Samples.Length);
        }

        [Fact]
        public void SessionIoDisabledAndEmptyExchangesDoNotMeasure()
        {
            Assert.Null(DriverDiagnostics.StartSessionIo(OceanBaseCompatibilityMode.MySql));
            using var capture = new IoCapture(); var measurement = DriverDiagnostics.StartSessionIo(OceanBaseCompatibilityMode.MySql)!;
            measurement.Finish(null); Assert.Empty(capture.Samples);
        }

        private sealed class IoCapture : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener();
            private readonly ConcurrentQueue<(double Seconds, string? Unit, Dictionary<string, object?> Tags)> _samples = new();
            internal (double Seconds, string? Unit, Dictionary<string, object?> Tags)[] Samples => _samples.ToArray();
            internal Action? OnSample;
            internal IoCapture()
            {
                _listener.InstrumentPublished = (instrument, owner) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.protocol.io.duration") owner.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                { _samples.Enqueue((value, instrument.Unit, tags.ToArray().ToDictionary(x => x.Key, x => x.Value))); OnSample?.Invoke(); });
                _listener.Start();
            }
            public void Dispose() => _listener.Dispose();
        }
    }
}
