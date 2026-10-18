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
        public async Task EveryAssetPublishesSafeDiagnosticsWithoutInitializationPoisoning(string target, string failure)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token); await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                channel.BeginCommand(); var sql = await channel.ReadPacketAsync(budget.Token); Assert.Equal(3, sql[0]);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
            });
            var context = new AssemblyLoadContext("diagnostics-" + target, true);
            var armed = false; var spans = new List<Activity>(); var counts = new List<KeyValuePair<string, object?>[]>();
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
            metrics.SetMeasurementEventCallback<long>((_, _, tags, _) => counts.Add(tags.ToArray()));
            metrics.SetMeasurementEventCallback<double>((_, _, _, _) => { }); metrics.Start(); armed = true;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=secret;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token); using var command = connection.CreateCommand(); command.CommandText = "secret sql";
                Assert.Equal(0, await command.ExecuteNonQueryAsync(budget.Token));
                if (failure == "normal")
                {
                    Assert.Equal(new[] { "oceanbase.open", "oceanbase.execute" }, spans.Select(x => x.OperationName)); Assert.Equal(2, counts.Count);
                    Assert.All(counts, tags => { Assert.Equal(4, tags.Length); Assert.Contains(tags, tag => tag.Key == "oceanbase.compatibility_mode" && Equals(tag.Value, "oracle")); Assert.DoesNotContain("secret", string.Join(";", tags)); });
                    Assert.All(spans, span => { Assert.Equal(ActivityStatusCode.Ok, span.Status); Assert.Equal("0.1.0.0", span.Source.Version); });
                    var reference = Assert.Single(assembly.GetReferencedAssemblies(), x => x.Name == "System.Diagnostics.DiagnosticSource");
                    Assert.Equal(target.StartsWith("netstandard", StringComparison.Ordinal) || target == "net8.0" ? 8 : target == "net9.0" ? 9 : 10, reference.Version!.Major);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
