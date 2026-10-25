using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Diagnostics;

public sealed class DiagnosticsTests
{
    public static TheoryData<string, bool> ConnectionPhaseRows()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        { data.Add(profile.Name, false); data.Add(profile.Name, true); }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(ConnectionPhaseRows))]
    public async Task ConnectionPhaseMetricsDescribeAnActualOpen(string profileName, bool asynchronous)
    {
        var samples = new ConcurrentQueue<(double Value, string? Unit, KeyValuePair<string, object?>[] Tags)>();
        using var meter = new MeterListener { MeasurementsCompleted = (_, _) => { } };
        meter.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.connection.phase.duration")
                owner.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) => samples.Enqueue((value, instrument.Unit, tags.ToArray())));
        meter.Start();
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var expected = new List<string> { "tcp", "greeting" };
        if (builder.TlsMode != OceanBaseTlsMode.Disabled) expected.Add("tls");
        expected.AddRange(new[] { "authentication", "identity" });
        Assert.Equal(expected, samples.Select(sample => sample.Tags.Single(tag => tag.Key == "oceanbase.connection.phase").Value));
        Assert.All(samples, sample =>
        {
            Assert.Equal("s", sample.Unit); Assert.True(double.IsFinite(sample.Value) && sample.Value >= 0);
            Assert.Equal(2, sample.Tags.Length);
            Assert.Contains(sample.Tags, tag => tag.Key == "oceanbase.outcome" && Equals(tag.Value, "success"));
        });
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The observed connection remains usable.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task OperationsPublishActivitiesAndMetrics(string profileName)
    {
        var activities = 0;
        var measurements = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "OceanBaseConnector",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _ => Interlocked.Increment(ref activities),
        };
        ActivitySource.AddActivityListener(listener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
        meter.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurements));
        meter.Start();
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "The observed operation must succeed.");
        Assert.True(activities > 0, "Public operations must publish activities.");
        Assert.True(measurements > 0, "Public operations must publish metrics.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task DriverLogsConnectionEvents(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var factory = new RecordingLoggerFactory();
        var builder = db.Profile.CreateBuilder();
        var connection = new OceanBaseConnection(builder.ConnectionString, factory);
        await using (connection)
        {
            await connection.OpenAsync();
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
                "The observed operation must succeed.");
            await connection.CloseAsync();
        }
        Assert.True(factory.Records.Count > 0, "Connection lifetime must publish log events.");
        Assert.Contains(factory.Records, record => record.Category == "OceanBaseConnector.Connection");
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<(string Category, LogLevel Level)> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Records);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class RecordingLogger(string category, ConcurrentQueue<(string, LogLevel)> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => records.Enqueue((category, logLevel));
        }
    }
}
