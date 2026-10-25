using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed class SlowQueryDiagnosticsTests
    {
        private const string Metric = "oceanbase.client.slow.operations";
        private const string Sql = "select private_sql_token";
        private static byte[] Ok() => new byte[] { 0, 0, 0, 2, 0, 0, 0 };
        private static string Configuration(int port, int threshold) =>
            $"Server=127.0.0.1;Port={port};User ID=private_user;Password=private_password;TLS Mode=Disabled;Slow Query Threshold={threshold}";

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(int.MaxValue)]
        public void ThresholdSurvivesSerializationEndpointAndCredentialSnapshots(int threshold)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;Slow Query Threshold=" + threshold);
            Assert.Equal(threshold.ToString(), builder["Slow Query Threshold"]);
            Assert.Equal(threshold, builder.SlowQueryThreshold);
            var options = new OceanBaseConnectionStringBuilder(builder.ConnectionString).Build();
            var endpoint = options.ForEndpoint(OceanBaseEndpoint.Parse("127.0.0.1:2882", 2881)[0]);
            var credential = endpoint.WithCredential(new OceanBaseCredential("user", "password", 1));
            foreach (var snapshot in new[] { options, endpoint, credential })
                Assert.Equal(threshold, snapshot.SlowQueryThresholdMilliseconds);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("1.5")]
        [InlineData("2147483648")]
        [InlineData("true")]
        public void InvalidThresholdIsRejectedBeforeNetwork(string threshold) =>
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Slow Query Threshold=" + threshold));

        [Fact]
        public void DefaultAndExplicitZeroHaveSameEffectivePoolIdentity()
        {
            var defaults = new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u");
            Assert.Equal(0, defaults.SlowQueryThreshold);
            var explicitZero = new OceanBaseConnectionStringBuilder(defaults.ConnectionString) { SlowQueryThreshold = 0 };
            Assert.Equal(defaults.ToPoolConfiguration(), explicitZero.ToPoolConfiguration());
            Assert.Throws<ArgumentException>(() => explicitZero.SlowQueryThreshold = -1);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task RefCursorProfileCoversWholeReaderAndPreservesParentOwnership(bool asynchronous, bool deprecated)
        {
            using var capture = new Capture();
            await TextCommandTests.ReadRefCursorPagesAndRelease(asynchronous, deprecated, sequential: true, slowThreshold: 1);
            var sample = Assert.Single(capture.Samples, sample => sample.Tags.Any(tag => tag.Key == "db.operation.name" && Equals(tag.Value, "cursor.fetch")));
            Assert.Contains(sample.Tags, x => x.Key == "oceanbase.outcome" && Equals(x.Value, "success"));
            Assert.Equal(true, Assert.Single(capture.Spans, x => x.OperationName == "oceanbase.cursor.fetch").GetTagItem("oceanbase.slow"));
        }

        public static IEnumerable<object[]> Outcomes()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var oracle in new[] { false, true })
                    foreach (var outcome in new[] { "success", "error", "canceled", "timeout" })
                        yield return new object[] { asynchronous, oracle, outcome };
        }

        [Theory]
        [MemberData(nameof(Outcomes))]
        public async Task SlowExecutionReportsOnceWithSafeOutcomeAndKeepsOriginalResult(bool asynchronous, bool oracle, string outcome)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(oracle, async (channel, stream) =>
            {
                await Request(channel, budget.Token); received.SetResult();
                if (outcome == "canceled" || outcome == "timeout")
                    Assert.Equal(0, await stream.ReadAsync(new byte[1], budget.Token));
                else
                {
                    await Task.Delay(30, budget.Token);
                    await channel.WritePacketAsync(outcome == "error" ? new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48, 115, 101, 99, 114, 101, 116 } : Ok(), budget.Token);
                }
            }, budget.Token);
            using var parent = new Activity("application").Start();
            using var capture = new Capture(); using var log = new ProfileLogger();
            using var connection = new OceanBaseConnection(Configuration(server.Port, 1), log);
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(budget.Token);
                using var command = connection.CreateCommand(); command.CommandText = Sql;
                command.CommandTimeout = outcome == "timeout" ? 1 : 5;
                var executing = Task.Run(() => asynchronous ? command.ExecuteNonQueryAsync(cancellation.Token) : Task.FromResult(command.ExecuteNonQuery()));
                await received.Task.WaitAsync(budget.Token);
                if (outcome == "canceled")
                { await Task.Delay(30, budget.Token); if (asynchronous) cancellation.Cancel(); else command.Cancel(); }
                var error = await Record.ExceptionAsync(() => executing.WaitAsync(budget.Token));
                if (outcome == "success") Assert.Null(error);
                else if (outcome == "error") Assert.Equal(1064, Assert.IsType<OceanBaseException>(error).ServerErrorCode);
                else if (outcome == "timeout") Assert.IsType<TimeoutException>(Assert.IsType<OceanBaseException>(error).InnerException);
                else
                {
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(Assert.IsType<OceanBaseException>(error).InnerException);
                    if (asynchronous) Assert.Equal(cancellation.Token, canceled.CancellationToken);
                }
                var sample = Assert.Single(capture.Samples);
                Assert.Equal(1, sample.Value); Assert.Equal("{operation}", sample.Unit); Assert.Equal(4, sample.Tags.Length);
                Assert.Contains(sample.Tags, x => x.Key == "db.operation.name" && Equals(x.Value, "execute"));
                Assert.Contains(sample.Tags, x => x.Key == "oceanbase.compatibility_mode" && Equals(x.Value, oracle ? "oracle" : "mysql"));
                Assert.Contains(sample.Tags, x => x.Key == "oceanbase.outcome" && Equals(x.Value, outcome));
                Assert.Contains(sample.Tags, x => x.Key == "oceanbase.execution_outcome_unknown" && Equals(x.Value, outcome == "canceled" || outcome == "timeout"));
                var span = Assert.Single(capture.Spans, x => x.OperationName == "oceanbase.execute");
                Assert.Equal(true, span.GetTagItem("oceanbase.slow")); Assert.Equal(parent.Id, span.ParentId);
                var entry = Assert.Single(log.Entries, x => x.Id.Id == 1102);
                Assert.Equal("OceanBaseConnector.Command", entry.Category); Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Null(entry.Error); Assert.Equal("SlowCommand", entry.Id.Name);
                Assert.True(Convert.ToDouble(entry.State["ElapsedMilliseconds"]) >= 1);
                Assert.Equal(1, entry.State["ThresholdMilliseconds"]);
                Assert.Equal(outcome, entry.State["Outcome"]);
                foreach (var text in new[] { entry.Text, string.Join(";", sample.Tags), string.Join(";", span.TagObjects) })
                    foreach (var secret in new[] { "private_sql_token", "private_password", "private_user", "secret" }) Assert.DoesNotContain(secret, text);
                Assert.Same(parent, Activity.Current);
            }
            finally { connection.Close(); server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, int.MaxValue)]
        [InlineData(true, int.MaxValue)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        public async Task ReaderConsumptionIsIncludedAndDisabledOrBelowThresholdDoesNotReport(bool asynchronous, int threshold)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Server(true, async (channel, _) =>
            {
                await Request(channel, budget.Token);
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token);
                await channel.WritePacketAsync(Column(), budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 2, 52, 50 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
            }, budget.Token);
            using var capture = new Capture(); using var log = new ProfileLogger();
            using var connection = new OceanBaseConnection(Configuration(server.Port, threshold), log);
            try
            {
                await connection.OpenAsync(budget.Token); using var command = connection.CreateCommand(); command.CommandText = Sql;
                using var reader = asynchronous ? await command.ExecuteReaderAsync(budget.Token) : command.ExecuteReader();
                Assert.Empty(capture.Samples); Assert.DoesNotContain(log.Entries, x => x.Id.Id == 1102);
                Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(42, reader.GetInt32(0));
                await Task.Delay(30, budget.Token);
                Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                reader.Close(); reader.Close();
                var slow = threshold == 1;
                Assert.Equal(slow ? 1 : 0, capture.Samples.Count);
                Assert.Equal(slow ? 1 : 0, log.Entries.Count(x => x.Id.Id == 1102));
                Assert.Equal(slow ? true : null, Assert.Single(capture.Spans, x => x.OperationName == "oceanbase.execute").GetTagItem("oceanbase.slow"));
            }
            finally { connection.Close(); server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Theory]
        [InlineData("log")]
        [InlineData("counter")]
        [InlineData("span")]
        [InlineData("throw.log")]
        [InlineData("throw.counter")]
        [InlineData("throw.span")]
        [InlineData("throw.filter")]
        public async Task ChannelsWorkIndependentlyAndCallbackFailuresPreserveActivityAndReuse(string channelKind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Server(true, async (channel, _) =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await Request(channel, budget.Token); await Task.Delay(30, budget.Token);
                    await channel.WritePacketAsync(TextCommandTests.TimeZoneOk("+00:00"), budget.Token); channel.BeginCommand();
                }
            }, budget.Token);
            using var parent = new Activity("caller").Start(); using var poison = new Activity("private_callback_token");
            Action corrupt = () => { Activity.Current = poison; throw new InvalidOperationException("private_callback_token"); };
            var failing = channelKind.StartsWith("throw.", StringComparison.Ordinal);
            using var capture = new Capture(metrics: failing || channelKind == "counter", activities: failing || channelKind == "span",
                onSlow: channelKind == "throw.counter" ? corrupt : null, onStopped: channelKind == "throw.span" ? _ => corrupt() : null);
            using var log = new ProfileLogger { Enabled = failing || channelKind == "log", OnSlow = channelKind == "throw.log" ? corrupt : null, ThrowFilter = channelKind == "throw.filter" };
            using var connection = new OceanBaseConnection(Configuration(server.Port, 1), log);
            try
            {
                await connection.OpenAsync(budget.Token);
                for (var i = 0; i < 2; i++)
                {
                    using var command = connection.CreateCommand(); command.CommandText = Sql;
                    Assert.Equal(0, await command.ExecuteNonQueryAsync(budget.Token)); Assert.Same(parent, Activity.Current);
                }
                Assert.Equal(failing || channelKind == "counter" ? 2 : 0, capture.Samples.Count);
                Assert.Equal(failing || channelKind == "span" ? 2 : 0, capture.Spans.Count(x => x.OperationName == "oceanbase.execute"));
                Assert.Equal(channelKind == "throw.filter" || !log.Enabled ? 0 : 2, log.Entries.Count(x => x.Id.Id == 1102));
            }
            finally { connection.Close(); server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SlowPrepareRemainsAnOrdinaryPrepareWithoutSlowQueryReport(bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Server(true, async (channel, _) =>
            {
                var request = await channel.ReadPacketAsync(budget.Token); Assert.Equal(22, request[0]);
                Assert.Equal(Sql, Encoding.UTF8.GetString(request, 1, request.Length - 1)); await Task.Delay(30, budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                channel.BeginCommand(); Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await channel.ReadPacketAsync(budget.Token));
            }, budget.Token);
            using var capture = new Capture(); using var log = new ProfileLogger();
            using var connection = new OceanBaseConnection(Configuration(server.Port, 1), log);
            try
            {
                await connection.OpenAsync(budget.Token); using var command = connection.CreateCommand(); command.CommandText = Sql;
                if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare();
                command.Dispose(); Assert.Empty(capture.Samples); Assert.DoesNotContain(log.Entries, x => x.Id.Id == 1102);
                Assert.Null(Assert.Single(capture.Spans, x => x.OperationName == "oceanbase.prepare").GetTagItem("oceanbase.slow"));
            }
            finally { connection.Close(); server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static LoopbackServer Server(bool oracle, Func<PacketChannel, NetworkStream, Task> action, CancellationToken token) => LoopbackServer.Start(async peer =>
        {
            var stream = peer.GetStream(); var channel = new PacketChannel(stream);
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, token);
            await channel.ReadPacketAsync(token);
            var authenticated = TextCommandTests.TimeZoneOk("+00:00");
            if (!oracle) authenticated[3] &= unchecked((byte)~4);
            await channel.WritePacketAsync(authenticated, token);
            await ServerIdentity.ReplyAsync(channel, oracle: oracle, token: token); channel.BeginCommand();
            await action(channel, stream);
        });
        private static async Task Request(PacketChannel channel, CancellationToken token)
        { var packet = await channel.ReadPacketAsync(token); Assert.Equal(3, packet[0]); Assert.Equal(Sql, Encoding.UTF8.GetString(packet, 1, packet.Length - 1)); }
        private static byte[] Column()
        {
            using var buffer = new MemoryStream();
            foreach (var text in new[] { "def", "", "", "", "value", "value" }) { var bytes = Encoding.UTF8.GetBytes(text); buffer.WriteByte((byte)bytes.Length); buffer.Write(bytes); }
            buffer.Write(new byte[] { 12, 63, 0, 11, 0, 0, 0, 9, 0, 0, 0, 0, 0 }); return buffer.ToArray();
        }

        private sealed class Capture : IDisposable
        {
            private readonly MeterListener _meter = new(); private readonly ActivityListener? _activities;
            internal ConcurrentQueue<(long Value, string? Unit, KeyValuePair<string, object?>[] Tags)> Samples { get; } = new();
            internal ConcurrentQueue<Activity> Spans { get; } = new();
            internal Capture(bool metrics = true, bool activities = true, Action? onSlow = null, Action<Activity>? onStopped = null)
            {
                _meter.InstrumentPublished = (instrument, listener) => { if (metrics && instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == Metric) listener.EnableMeasurementEvents(instrument); };
                _meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) => { Samples.Enqueue((value, instrument.Unit, tags.ToArray())); onSlow?.Invoke(); }); _meter.Start();
                if (activities)
                {
                    _activities = new ActivityListener { ShouldListenTo = x => x.Name == "OceanBaseConnector", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = x => { Spans.Enqueue(x); onStopped?.Invoke(x); } };
                    ActivitySource.AddActivityListener(_activities);
                }
            }
            public void Dispose() { _activities?.Dispose(); _meter.Dispose(); }
        }

        private sealed class ProfileLogger : ILoggerFactory
        {
            internal ConcurrentQueue<(string Category, LogLevel Level, EventId Id, Dictionary<string, object?> State, Exception? Error, string Text)> Entries { get; } = new();
            internal bool Enabled { get; init; } = true;
            internal bool ThrowFilter { get; init; }
            internal Action? OnSlow { get; init; }
            public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
            public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
            public void Dispose() { }
            private sealed class Logger(ProfileLogger owner, string category) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => owner.ThrowFilter ? throw new InvalidOperationException("filter") : owner.Enabled && category == "OceanBaseConnector.Command";
                public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                {
                    owner.Entries.Enqueue((category, level, id, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value), error, formatter(state, error)));
                    if (id.Id == 1102) owner.OnSlow?.Invoke();
                }
            }
        }
    }
}
