using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class CredentialProviderTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CredentialsAndProviderExceptionsAreAbsentFromDiagnostics(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(new OceanBaseCredential("private-user", "private-password", 1));
            var captured = new ConcurrentQueue<string>();
            using var activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "OceanBaseConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => { foreach (var tag in activity.TagObjects) captured.Enqueue(tag.Key + "=" + tag.Value); captured.Enqueue(activity.StatusDescription ?? ""); },
            };
            ActivitySource.AddActivityListener(activities);
            using var metrics = new MeterListener { MeasurementsCompleted = (_, _) => { }, InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == "OceanBaseConnector") listener.EnableMeasurementEvents(instrument); } };
            metrics.SetMeasurementEventCallback<long>((instrument, _, tags, _) => { captured.Enqueue(instrument.Name); foreach (var tag in tags) captured.Enqueue(tag.Key + "=" + tag.Value); });
            metrics.SetMeasurementEventCallback<double>((instrument, _, tags, _) => { captured.Enqueue(instrument.Name); foreach (var tag in tags) captured.Enqueue(tag.Key + "=" + tag.Value); });
            metrics.Start(); using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger) { CredentialProvider = provider };
            try
            {
                await Open(connection, asynchronous); await Close(connection, asynchronous); provider.Fail = true;
                var failure = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous)); captured.Enqueue(failure.ToString());
                foreach (var entry in logger.Entries) { captured.Enqueue(entry.Message); captured.Enqueue(entry.Error?.ToString() ?? ""); }
                Assert.Contains(captured, value => value.StartsWith("oceanbase.client.pool.", StringComparison.Ordinal));
                Assert.Contains(captured, value => value == "oceanbase.outcome=error");
                var text = string.Join("\n", captured);
                foreach (var secret in new[] { "private-user", "private-password", "secret-provider-token" }) Assert.DoesNotContain(secret, text);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AcquisitionAndDiscoveryShareOneConnectionBudget(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            provider.Sync = token => { Task.Delay(650, token).GetAwaiter().GetResult(); return provider.Current; };
            provider.Async = async token => { await Task.Delay(650, token); return provider.Current; };
            using var connection = new OceanBaseConnection(new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { ConnectionTimeout = 1 }.ConnectionString)
            { CredentialProvider = provider, AddressResolver = new DelayedResolver(server.Port) };
            try { await Assert.ThrowsAsync<TimeoutException>(() => Open(connection, asynchronous)); Assert.Equal(0, server.Accepted); Assert.Equal(1, provider.Calls); }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        private sealed class DelayedResolver(int port) : IOceanBaseAddressResolver
        {
            public string Resolve(CancellationToken token) { Task.Delay(650, token).GetAwaiter().GetResult(); return "127.0.0.1:" + port; }
            public async Task<string> ResolveAsync(CancellationToken token) { await Task.Delay(650, token); return "127.0.0.1:" + port; }
        }
    }
}
