using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [CollectionDefinition("Consumer diagnostics", DisableParallelization = true)]
    public sealed class DiagnosticsCollection { }

    [Collection("Consumer diagnostics")]
    public sealed class DiagnosticsConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", "normal")]
        [InlineData("netstandard2.1", "normal")]
        [InlineData("net8.0", "normal")]
        [InlineData("net9.0", "normal")]
        [InlineData("net10.0", "normal")]
        [InlineData("netstandard2.0", "source")]
        [InlineData("netstandard2.1", "instrument")]
        [InlineData("net8.0", "source")]
        [InlineData("net9.0", "instrument")]
        [InlineData("net10.0", "instrument")]
        [InlineData("netstandard2.0", "phase")]
        [InlineData("netstandard2.1", "phase")]
        [InlineData("net8.0", "phase")]
        [InlineData("net9.0", "phase")]
        [InlineData("net10.0", "phase")]
        [InlineData("netstandard2.0", "slow")]
        [InlineData("netstandard2.1", "slow")]
        [InlineData("net8.0", "slow")]
        [InlineData("net9.0", "slow")]
        [InlineData("net10.0", "slow")]
        [InlineData("netstandard2.0", "slow.throw")]
        [InlineData("netstandard2.1", "slow.throw")]
        [InlineData("net8.0", "slow.throw")]
        [InlineData("net9.0", "slow.throw")]
        [InlineData("net10.0", "slow.throw")]
        [InlineData("netstandard2.0", "io")]
        [InlineData("netstandard2.1", "io")]
        [InlineData("net8.0", "io")]
        [InlineData("net9.0", "io")]
        [InlineData("net10.0", "io")]
        [InlineData("netstandard2.0", "io.throw")]
        [InlineData("netstandard2.1", "io.throw")]
        [InlineData("net8.0", "io.throw")]
        [InlineData("net9.0", "io.throw")]
        [InlineData("net10.0", "io.throw")]
        public async Task EveryAssetPublishesSafeDiagnosticsWithoutInitializationPoisoning(string target, string failure)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var profile = failure.StartsWith("slow", StringComparison.Ordinal);
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token); await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token);
                channel.BeginCommand(); var sql = await channel.ReadPacketAsync(budget.Token); Assert.Equal(3, sql[0]);
                if (profile) await Task.Delay(30, budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
            });
            var context = new AssemblyLoadContext("diagnostics-" + target, true);
            var armed = false; var spans = new List<Activity>(); var counts = new List<KeyValuePair<string, object?>[]>();
            var phases = new List<(double Value, string? Unit, KeyValuePair<string, object?>[] Tags)>();
            var io = new List<(double Value, string? Unit, KeyValuePair<string, object?>[] Tags)>();
            var slow = new List<(long Value, string? Unit, KeyValuePair<string, object?>[] Tags)>();
            using var activities = new ActivityListener
            {
                ShouldListenTo = source => armed && source.Name == "OceanBaseConnector" ? failure == "source" ? throw new InvalidOperationException("listener initialization") : true : false,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => spans.Add(activity)
            };
            ActivitySource.AddActivityListener(activities);
            using var metrics = new MeterListener { MeasurementsCompleted = (_, _) => { } };
            metrics.InstrumentPublished = (instrument, owner) =>
            {
                if (!armed || instrument.Meter.Name != "OceanBaseConnector") return;
                if (failure == "instrument") throw new InvalidOperationException("instrument initialization");
                owner.EnableMeasurementEvents(instrument);
            };
            metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "oceanbase.client.operations") counts.Add(tags.ToArray());
                if (instrument.Name != "oceanbase.client.slow.operations") return;
                slow.Add((value, instrument.Unit, tags.ToArray()));
                if (failure == "slow.throw") throw new InvalidOperationException("private-slow-listener-token");
            });
            metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "oceanbase.client.protocol.io.duration")
                {
                    io.Add((value, instrument.Unit, tags.ToArray()));
                    if (failure == "io.throw") throw new InvalidOperationException("private-io-listener-token");
                    return;
                }
                if (instrument.Name != "oceanbase.client.connection.phase.duration") return;
                phases.Add((value, instrument.Unit, tags.ToArray()));
                if (failure == "phase") throw new InvalidOperationException("private-phase-listener-token");
            }); metrics.Start(); armed = true;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=secret;TLS Mode=Disabled;Slow Query Threshold={(profile ? 1 : 0)}";
                await connection.OpenAsync(budget.Token); io.Clear(); using var command = connection.CreateCommand(); command.CommandText = "secret sql";
                Assert.Equal(0, await command.ExecuteNonQueryAsync(budget.Token));
                if (failure.StartsWith("io", StringComparison.Ordinal))
                {
                    Assert.Equal(2, io.Count);
                    Assert.Equal(new object?[] { "read", "write" }, io.Select(sample => sample.Tags.Single(tag => tag.Key == "oceanbase.protocol.direction").Value));
                    Assert.All(io, sample =>
                    {
                        Assert.Equal("s", sample.Unit); Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0);
                        Assert.Equal(3, sample.Tags.Length);
                        Assert.Contains(sample.Tags, tag => tag.Key == "oceanbase.compatibility_mode" && Equals(tag.Value, "oracle"));
                        Assert.Contains(sample.Tags, tag => tag.Key == "oceanbase.outcome" && Equals(tag.Value, "success"));
                        Assert.DoesNotContain("secret", string.Join(";", sample.Tags));
                    });
                }
                if (failure == "normal" || failure == "phase" || profile)
                {
                    Assert.Equal(new[] { "oceanbase.open", "oceanbase.execute" }, spans.Select(x => x.OperationName)); Assert.Equal(2, counts.Count);
                    Assert.All(counts, tags => { Assert.Equal(4, tags.Length); Assert.Contains(tags, tag => tag.Key == "oceanbase.compatibility_mode" && Equals(tag.Value, "oracle")); Assert.DoesNotContain("secret", string.Join(";", tags)); });
                    Assert.All(spans, span => { Assert.Equal(ActivityStatusCode.Ok, span.Status); Assert.Equal("0.1.0.0", span.Source.Version); });
                    var execute = Assert.Single(spans, x => x.OperationName == "oceanbase.execute");
                    Assert.Equal(profile ? true : null, execute.GetTagItem("oceanbase.slow"));
                    Assert.Equal(profile ? 1 : 0, slow.Count);
                    if (profile)
                    {
                        var sample = Assert.Single(slow); Assert.Equal(1, sample.Value); Assert.Equal("{operation}", sample.Unit);
                        Assert.Equal(4, sample.Tags.Length);
                        Assert.Contains(sample.Tags, x => x.Key == "db.operation.name" && Equals(x.Value, "execute"));
                        Assert.Contains(sample.Tags, x => x.Key == "oceanbase.outcome" && Equals(x.Value, "success"));
                        Assert.DoesNotContain("secret", string.Join(";", sample.Tags));
                    }
                    Assert.Equal(new[] { "tcp", "greeting", "authentication", "identity" }, phases.Select(sample => sample.Tags.Single(tag => tag.Key == "oceanbase.connection.phase").Value));
                    Assert.All(phases, sample =>
                    {
                        Assert.Equal("s", sample.Unit); Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0);
                        Assert.Equal(2, sample.Tags.Length);
                        Assert.Contains(sample.Tags, tag => tag.Key == "oceanbase.outcome" && Equals(tag.Value, "success"));
                    });
                    var reference = Assert.Single(assembly.GetReferencedAssemblies(), x => x.Name == "System.Diagnostics.DiagnosticSource");
                    Assert.Equal(target.StartsWith("netstandard", StringComparison.Ordinal) || target == "net8.0" ? 8 : target == "net9.0" ? 9 : 10, reference.Version!.Major);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
