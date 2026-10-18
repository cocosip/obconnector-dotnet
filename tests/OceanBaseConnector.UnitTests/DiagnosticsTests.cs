using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.UnitTests
{
    [CollectionDefinition("Driver diagnostics", DisableParallelization = true)]
    public sealed class DiagnosticsCollection { }

    [Collection("Driver diagnostics")]
    public sealed class DiagnosticsTests
    {
        private readonly ITestOutputHelper _output;
        public DiagnosticsTests(ITestOutputHelper output) => _output = output;
        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;Password=secret;TLS Mode=Disabled";
        private static byte[] Ok(byte status = 2) => new byte[] { 0, 0, 0, status, 0, 0, 0 };
        private static byte[] Error() => new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48, 115, 101, 99, 114, 101, 116 };
        private static LoopbackServer Server(Func<PacketChannel, Task> action) => LoopbackServer.Start(async peer =>
        {
            var channel = new PacketChannel(peer.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
            await channel.ReadPacketAsync(); await channel.WritePacketAsync(TimeZone()); channel.BeginCommand(); await action(channel);
        });
        private static byte[] TimeZone() => new byte[] { 0, 0, 0, 6, 64, 0, 0, 0, 19, 0, 17, 9, 116, 105, 109, 101, 95, 122, 111, 110, 101, 6, 43, 48, 48, 58, 48, 48 };
        private static async Task Request(PacketChannel channel, string sql)
        {
            var request = await channel.ReadPacketAsync(); Assert.Equal(3, request[0]);
            Assert.Equal(sql, System.Text.Encoding.UTF8.GetString(request, 1, request.Length - 1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpenAndReaderDiagnosticsKeepParentAndEndOnlyAtTerminalResponse(bool asynchronous)
        {
            using var parent = new Activity("application").Start(); using var capture = new Capture();
            var server = Server(async channel =>
            {
                await Request(channel, "secret sql");
                await channel.WritePacketAsync(new byte[] { 1 });
                var column = new List<byte>(); foreach (var value in new[] { "def", "", "", "", "s", "s" }) { column.Add((byte)value.Length); column.AddRange(System.Text.Encoding.UTF8.GetBytes(value)); }
                column.AddRange(new byte[] { 12, 45, 0, 20, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(column.ToArray()); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 });
                await channel.WritePacketAsync(new byte[] { 6, 115, 101, 99, 114, 101, 116 }); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Same(parent, Activity.Current);
                var opened = Assert.Single(capture.Spans); Check(opened, "open", "oracle", "success", false); Assert.Equal(parent.Id, opened.ParentId);
                using var command = connection.CreateCommand(); command.CommandText = "secret sql";
                using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                Assert.Same(parent, Activity.Current); Assert.Single(capture.Spans);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("secret", reader.GetString(0));
                Assert.Single(capture.Spans);
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                var executed = capture.Spans.Last(); Check(executed, "execute", "oracle", "success", false); Assert.Equal(parent.Id, executed.ParentId);
                Assert.Equal(2, capture.Spans.Count); Assert.Equal(2, capture.Counts.Count); Assert.Equal(2, capture.Durations.Count);
                Assert.All(capture.Counts, x => Assert.Equal(1, x.Value)); Assert.All(capture.Durations, x => Assert.True(x.Value >= 0));
                Assert.All(capture.Counts, x => Assert.Equal(4, x.Tags.Count));
                Assert.All(capture.Spans, x => { Assert.Empty(x.Events); Assert.DoesNotContain("secret", string.Join(";", x.TagObjects)); Assert.Equal(ActivityKind.Client, x.Kind); });
                Assert.Same(parent, Activity.Current);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("server", "error", false)]
        [InlineData("cancel", "canceled", true)]
        [InlineData("timeout", "timeout", true)]
        [InlineData("close", "error", true)]
        public async Task CommandDiagnosticsSeparateFailureCauseFromUnknownOutcome(string failure, string outcome, bool unknown)
        {
            using var capture = new Capture(); var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Request(channel, "secret sql"); request.SetResult();
                if (failure == "server") await channel.WritePacketAsync(Error()); else await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "secret sql"; command.CommandTimeout = failure == "timeout" ? 1 : 0;
                using var cancel = new CancellationTokenSource(); var running = command.ExecuteNonQueryAsync(cancel.Token);
                await request.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (failure == "cancel") cancel.Cancel(); if (failure == "close") connection.Close();
                await Assert.ThrowsAsync<OceanBaseException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
                Check(capture.Spans.Last(), "execute", "oracle", outcome, unknown);
                var count = capture.Counts.Last(); Assert.Equal(outcome, count.Tags["oceanbase.outcome"]); Assert.Equal(unknown, count.Tags["oceanbase.execution_outcome_unknown"]);
                Assert.DoesNotContain("secret", string.Join(";", capture.Spans.Last().TagObjects));
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TransactionDiagnosticsPublishAfterStateChanges(bool asynchronous)
        {
            using var capture = new Capture();
            var server = Server(async channel =>
            {
                foreach (var item in new[] { ("SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", (byte)2), ("set autocommit = 0", (byte)0), ("COMMIT", (byte)0), ("set autocommit = 1", (byte)2) })
                { await Request(channel, item.Item1); await channel.WritePacketAsync(Ok(item.Item2)); channel.BeginCommand(); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
                if (asynchronous) await transaction.CommitAsync(); else transaction.Commit();
                Assert.Null(transaction.Connection);
                Assert.Equal(new[] { "oceanbase.open", "oceanbase.transaction.begin", "oceanbase.transaction.commit" }, capture.Spans.Select(x => x.OperationName));
                Assert.Equal(3, capture.Counts.Count); Assert.All(capture.Spans, x => Assert.Equal(ActivityStatusCode.Ok, x.Status));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("off")]
        [InlineData("none")]
        [InlineData("on")]
        public void DiagnosticDisabledPathAllocatesNothingAndSamplingHasBoundedOverhead(string setting)
        {
            using var listener = new ActivityListener { ShouldListenTo = source => source.Name == "OceanBaseConnector", Sample = (ref ActivityCreationOptions<ActivityContext> _) => setting == "on" ? ActivitySamplingResult.AllData : ActivitySamplingResult.None };
            if (setting != "off") ActivitySource.AddActivityListener(listener);
            for (var i = 0; i < 1000; i++) DriverDiagnostics.Start("execute")?.Finish(OceanBaseCompatibilityMode.Oracle, null);
            var timer = new Stopwatch(); var before = GC.GetAllocatedBytesForCurrentThread(); timer.Start();
            for (var i = 0; i < 10000; i++) DriverDiagnostics.Start("execute")?.Finish(OceanBaseCompatibilityMode.Oracle, null);
            timer.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            _output.WriteLine($"diagnostics={setting} runtime={Environment.Version} iterations=10000 elapsed_ms={timer.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated}");
            if (setting == "off") Assert.Equal(0, allocated);
            if (setting == "none") Assert.InRange(allocated, 0, 64 * 10000);
        }

        [Theory]
        [InlineData("sample")]
        [InlineData("start")]
        [InlineData("stop")]
        [InlineData("count")]
        [InlineData("duration")]
        public async Task ThrowingListenersCannotChangeResultsOrLeakAmbientContext(string failure)
        {
            using var parent = new Activity("application").Start();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "OceanBaseConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => failure == "sample" ? throw new InvalidOperationException("listener") : ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => failure == "sample" ? throw new InvalidOperationException("listener") : ActivitySamplingResult.AllData,
                ActivityStarted = _ => { if (failure == "start") throw new InvalidOperationException("listener"); },
                ActivityStopped = _ => { if (failure == "stop") throw new InvalidOperationException("listener"); }
            };
            ActivitySource.AddActivityListener(listener);
            using var meter = new MeterListener(); meter.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument); };
            meter.SetMeasurementEventCallback<long>((_, _, _, _) => { if (failure == "count") throw new InvalidOperationException("listener"); });
            meter.SetMeasurementEventCallback<double>((_, _, _, _) => { if (failure == "duration") throw new InvalidOperationException("listener"); }); meter.Start();
            var server = Server(async channel => { await Request(channel, "first"); await channel.WritePacketAsync(TimeZone()); channel.BeginCommand(); await Request(channel, "second"); await channel.WritePacketAsync(Error()); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "first";
                Assert.Equal(0, command.ExecuteNonQuery()); Assert.Same(parent, Activity.Current);
                command.CommandText = "second"; var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Same(parent, Activity.Current); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task FailedAuthenticationIsUnknownModeAndValidationDoesNotCount()
        {
            using var capture = new Capture();
            var server = LoopbackServer.Start(async peer =>
            {
                var channel = new PacketChannel(peer.GetStream()); await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(Error());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => connection.OpenAsync()); Check(Assert.Single(capture.Spans), "open", "unknown", "error", false);
                using var command = connection.CreateCommand(); Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
                using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.OpenAsync(cancel.Token));
                Assert.Single(capture.Spans); Assert.Single(capture.Counts);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("off", "prepared")]
        [InlineData("none", "prepared")]
        [InlineData("on", "prepared")]
        [InlineData("off", "multi_result")]
        [InlineData("none", "multi_result")]
        [InlineData("on", "multi_result")]
        [InlineData("off", "large")]
        [InlineData("none", "large")]
        [InlineData("on", "large")]
        public async Task WindowsProtocolPerformanceBaseline(string diagnostics, string workload)
        {
            using var activities = new ActivityListener { ShouldListenTo = source => source.Name == "OceanBaseConnector", Sample = (ref ActivityCreationOptions<ActivityContext> _) => diagnostics == "on" ? ActivitySamplingResult.AllData : ActivitySamplingResult.None };
            if (diagnostics != "off") ActivitySource.AddActivityListener(activities);
            using var metrics = new MeterListener(); metrics.InstrumentPublished = (instrument, owner) => { if (diagnostics == "on" && instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument); };
            metrics.SetMeasurementEventCallback<long>((_, _, _, _) => { }); metrics.SetMeasurementEventCallback<double>((_, _, _, _) => { }); metrics.Start();
            const int length = 4 * 1024 * 1024; var repetitions = workload == "large" ? 8 : 400;
            var prepares = 0; var executions = 0;
            var server = Server(async channel =>
            {
                if (workload == "prepared")
                {
                    var prepare = await channel.ReadPacketAsync(); Assert.Equal(22, prepare[0]); prepares++;
                    await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }); channel.BeginCommand();
                }
                var large = workload == "large" ? new byte[length + 4] : Array.Empty<byte>();
                if (large.Length != 0) { Array.Fill(large, (byte)42); large[0] = 253; large[1] = 0; large[2] = 0; large[3] = 64; }
                for (var i = 0; i < repetitions + 1; i++)
                {
                    var request = await channel.ReadPacketAsync();
                    if (request[0] == 3 && System.Text.Encoding.UTF8.GetString(request, 1, request.Length - 1) == "SELECT SESSIONTIMEZONE FROM DUAL")
                    {
                        await StringResult(channel, new byte[] { 6, 43, 48, 48, 58, 48, 48 }, false); channel.BeginCommand(); request = await channel.ReadPacketAsync();
                    }
                    Assert.Equal(workload == "prepared" ? 23 : 3, request[0]); executions++;
                    if (workload == "large") await StringResult(channel, large, true);
                    else
                    {
                        var ok = TimeZone(); if (workload == "multi_result") { ok[3] |= 8; await channel.WritePacketAsync(ok); }
                        await channel.WritePacketAsync(TimeZone());
                    }
                    channel.BeginCommand();
                }
                if (workload == "prepared") { var close = await channel.ReadPacketAsync(); Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, close); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "baseline";
                if (workload == "prepared") command.Prepare(); var buffer = new byte[8192];
                long Execute()
                {
                    if (workload != "large") { Assert.Equal(0, command.ExecuteNonQuery()); return 0; }
                    using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess); Assert.True(reader.Read()); using var stream = reader.GetStream(0);
                    long bytes = 0; int read; while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) bytes += read;
                    Assert.False(reader.NextResult()); Assert.Equal(length, bytes); return bytes;
                }
                Execute(); var timer = new Stopwatch(); var before = GC.GetAllocatedBytesForCurrentThread(); timer.Start(); long bytes = 0;
                for (var i = 0; i < repetitions; i++) bytes += Execute();
                timer.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                _output.WriteLine($"protocol_baseline diagnostics={diagnostics} workload={workload} runtime={Environment.Version} os={Environment.OSVersion} iterations={repetitions} elapsed_ms={timer.Elapsed.TotalMilliseconds:F3} client_thread_allocated_bytes={allocated} streamed_bytes={bytes}");
                if (workload == "large") Assert.InRange(allocated, 0, 2 * 1024 * 1024);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(repetitions + 1, executions); Assert.Equal(workload == "prepared" ? 1 : 0, prepares);
        }

        private static async Task StringResult(PacketChannel channel, byte[] row, bool binary)
        {
            await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 118, 1, 118, 12, binary ? (byte)63 : (byte)45, 0, 255, 0, 0, 0, binary ? (byte)252 : (byte)253, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }); await channel.WritePacketAsync(row); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareAndDatabaseSwitchReportAuthenticatedMySqlMode(bool asynchronous)
        {
            using var capture = new Capture();
            var server = Server(async channel =>
            {
                var prepare = await channel.ReadPacketAsync(); Assert.Equal(22, prepare[0]);
                await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }); channel.BeginCommand();
                var execute = await channel.ReadPacketAsync(); Assert.Equal(23, execute[0]); await channel.WritePacketAsync(Ok()); channel.BeginCommand();
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await channel.ReadPacketAsync()); channel.BeginCommand();
                var change = await channel.ReadPacketAsync(); Assert.Equal(2, change[0]); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                // A05 public MySQL authentication remains closed; use an explicit post-auth fixture.
                var session = connection.GetOpenSession(); var info = session.Info!;
                typeof(PhysicalSession).GetField("_info", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session,
                    new ConnectResult(info.ServerVersion, info.ConnectionId, OceanBaseCompatibilityMode.MySql, info.Capabilities, info.ServerStatus, info.ObCapabilityFlag, info.SessionVariables));
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "secret sql"; if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                if (asynchronous) await connection.ChangeDatabaseAsync("secret database"); else connection.ChangeDatabase("secret database");
                Assert.Equal(new[] { "oceanbase.open", "oceanbase.prepare", "oceanbase.execute", "oceanbase.change_database" }, capture.Spans.Select(x => x.OperationName));
                foreach (var span in capture.Spans.Skip(1)) { Assert.Equal("mysql", span.GetTagItem("oceanbase.compatibility_mode")); Assert.DoesNotContain("secret", string.Join(";", span.TagObjects)); }
                Assert.Equal(4, capture.Counts.Count);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EarlyReaderClosureEndsUnknownSpanOnce(bool asynchronous)
        {
            using var capture = new Capture();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Request(channel, "secret sql"); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 118, 1, 118, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }); await channel.WritePacketAsync(new byte[] { 1, 65 }); await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "secret sql";
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.Single(capture.Spans);
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                reader.Close(); command.Cancel();
                Assert.Equal(2, capture.Spans.Count); Assert.Equal(2, capture.Counts.Count); Check(capture.Spans.Last(), "execute", "oracle", "error", true);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task DiagnosticCallbacksSeePublishedOwnershipAndReleasedSession()
        {
            OceanBaseConnection? connection = null; var nested = false; var blocked = false; var unlocked = true; var completed = false;
            using var activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "OceanBaseConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = span =>
                {
                    if (span.OperationName != "oceanbase.execute" || nested) return;
                    unlocked &= Task.Run(() => connection!.State).Wait(TimeSpan.FromSeconds(2));
                    using var other = connection!.CreateCommand(); other.CommandText = "not sent";
                    blocked = Record.Exception(() => other.ExecuteNonQuery()) is InvalidOperationException;
                },
                ActivityStopped = span =>
                {
                    if (span.OperationName != "oceanbase.execute" || nested) return;
                    nested = true; using var after = connection!.CreateCommand(); after.CommandText = "after";
                    completed = after.ExecuteNonQuery() == 0;
                }
            };
            ActivitySource.AddActivityListener(activities);
            var server = Server(async channel => { await Request(channel, "first"); await channel.WritePacketAsync(TimeZone()); channel.BeginCommand(); await Request(channel, "after"); await channel.WritePacketAsync(TimeZone()); });
            using var owned = new OceanBaseConnection(Configuration(server.Port)); connection = owned;
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "first"; Assert.Equal(0, command.ExecuteNonQuery());
                Assert.True(blocked); Assert.True(unlocked); Assert.True(completed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ThrowingAmbientContextChangeCallbackCannotStrandCommandOwnership(bool asynchronous)
        {
            using var parent = new Activity("application").Start(); using var capture = new Capture();
            var server = Server(async channel => { await Request(channel, "first"); await channel.WritePacketAsync(TimeZone()); channel.BeginCommand(); await Request(channel, "after"); await channel.WritePacketAsync(TimeZone()); });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); var restored = 0;
            void Changed(object? sender, ActivityChangedEventArgs change)
            {
                if (change.Previous?.Source.Name == "OceanBaseConnector" && ReferenceEquals(change.Current, parent)) { restored++; throw new InvalidOperationException("ambient listener"); }
            }
            try
            {
                await connection.OpenAsync(); Activity.CurrentChanged += Changed;
                using var command = connection.CreateCommand(); command.CommandText = "first";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.CommandText = "after"; Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.True(restored >= 2); Assert.Same(parent, Activity.Current); Assert.Equal(3, capture.Spans.Count);
            }
            finally { Activity.CurrentChanged -= Changed; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static void Check(Activity span, string operation, string mode, string outcome, bool unknown)
        {
            Assert.Equal("OceanBaseConnector", span.Source.Name); Assert.Equal("oceanbase." + operation, span.OperationName);
            Assert.Equal("oceanbase", span.GetTagItem("db.system.name")); Assert.Equal(operation, span.GetTagItem("db.operation.name"));
            Assert.Equal(mode, span.GetTagItem("oceanbase.compatibility_mode")); Assert.Equal(outcome, span.GetTagItem("oceanbase.outcome"));
            Assert.Equal(unknown, span.GetTagItem("oceanbase.execution_outcome_unknown")); Assert.Equal(outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error, span.Status);
            Assert.Null(span.StatusDescription);
        }

        private sealed class Capture : IDisposable
        {
            private readonly ActivityListener _activities;
            private readonly MeterListener _metrics = new MeterListener();
            internal List<Activity> Spans { get; } = new List<Activity>();
            internal List<(long Value, Dictionary<string, object?> Tags)> Counts { get; } = new();
            internal List<(double Value, Dictionary<string, object?> Tags)> Durations { get; } = new();
            internal Capture()
            {
                _activities = new ActivityListener { ShouldListenTo = source => source.Name == "OceanBaseConnector", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded, SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded, ActivityStopped = activity => Spans.Add(activity) };
                ActivitySource.AddActivityListener(_activities);
                _metrics.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == "OceanBaseConnector") listener.EnableMeasurementEvents(instrument); };
                _metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) => { Assert.Equal("oceanbase.client.operations", instrument.Name); Assert.Equal("{operation}", instrument.Unit); Counts.Add((value, Tags(tags))); });
                _metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) => { Assert.Equal("oceanbase.client.operation.duration", instrument.Name); Assert.Equal("s", instrument.Unit); Durations.Add((value, Tags(tags))); });
                _metrics.Start();
            }
            private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
            { var result = new Dictionary<string, object?>(); foreach (var tag in tags) result.Add(tag.Key, tag.Value); return result; }
            public void Dispose() { _activities.Dispose(); _metrics.Dispose(); }
        }
    }
}
