using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed class PoolLeaseMetricsConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        foreach (var throwing in new[] { false, true })
                            yield return new object[] { target, asynchronous, oracle, throwing };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReportsCurrentPoolLeasesAndIsolatesObservers(string target, bool asynchronous, bool oracle, bool throwing)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new CredentialServer(Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!), oracle);
            var context = new AssemblyLoadContext("pool-leases-" + target, isCollectible: true);
            long current = 0, positive = 0; var invalid = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.pool.leased") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Unit != "{session}" || tags.Length != 0) Interlocked.Increment(ref invalid);
                Interlocked.Add(ref current, value); if (value > 0) Interlocked.Increment(ref positive);
                Activity.Current = new Activity("observer");
                if (throwing) throw new InvalidOperationException("private observer detail");
            });
            listener.Start();
            DbConnection? connection = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Connection Timeout=5;Command Timeout=3";
                using var parent = new Activity("application").Start();
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    Assert.Equal(1, Interlocked.Read(ref current)); Assert.Same(parent, Activity.Current);
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                    Assert.Equal(0, Interlocked.Read(ref current)); Assert.Same(parent, Activity.Current);
                }
                Assert.Equal(2, Interlocked.Read(ref positive)); Assert.Equal(0, invalid);
                Assert.Equal(1, server.Accepted);
            }
            finally
            {
                connection?.Close(); connection?.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                connection?.Dispose(); context.Unload();
            }
        }
    }
}
