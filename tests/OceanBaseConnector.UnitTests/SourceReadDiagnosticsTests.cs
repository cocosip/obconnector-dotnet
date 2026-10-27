using System.Collections.Generic;
using System;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class DiagnosticsTests
    {
        public static IEnumerable<object[]> SourceReadCases()
        {
            foreach (var (oracle, local) in new[] { (false, false), (true, false), (false, true) })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var outcome in new[] { "success", "error", "source-error", "canceled", "timeout" })
                        if (outcome != "timeout" || asynchronous)
                            yield return new object[] { oracle, local, asynchronous, outcome };
        }
        [Theory]
        [MemberData(nameof(SourceReadCases))]
        public Task SourceReadsPublishOneSafePostReleaseSummary(bool oracle, bool local, bool asynchronous, string outcome) =>
            SourceReadFixture.Verify(() => new OceanBaseConnection(), oracle, local, asynchronous, outcome);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public Task SourceReadsIncludeEmptyEofAndSurviveListenerFailure(bool oracle, bool local) =>
            SourceReadFixture.Verify(() => new OceanBaseConnection(), oracle, local, true, size: 0, throwListener: true);

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        public Task SourceReadsIncludeSourceWorkAndExcludeServerWait(bool oracle, bool local, bool asynchronous) =>
            SourceReadFixture.Verify(() => new OceanBaseConnection(), oracle, local, asynchronous, delayed: true);

        [Fact]
        public void SourceReadsEnabledWithoutAnyReadEmitsNoSample()
        {
            using var capture = new SourceReadFixture.Capture();
            DriverDiagnostics.StartSessionIo(OceanBaseCompatibilityMode.MySql)!.Finish(null);
            Assert.Empty(capture.Samples);
        }

        [Fact]
        public async Task SourceReadsPendingCompletionPublishesEachKindOnceAndExcludesProtocolWhenDisabled()
        {
            using var capture = new SourceReadFixture.Capture();
            var measurement = DriverDiagnostics.StartSessionIo(OceanBaseCompatibilityMode.Oracle)!;
            Assert.False(measurement.ProtocolEnabled); Assert.True(measurement.SourceReadsEnabled);
            Assert.Equal(0, measurement.Start());
            var parameter = measurement.StartSourceRead(); var infile = measurement.StartSourceRead();
            measurement.Finish(new TimeoutException("private")); measurement.Finish(null);
            Assert.Empty(capture.Samples); Assert.Equal(0, measurement.StartSourceRead());
            await Task.WhenAll(Task.Run(() => measurement.StopSourceRead(parameter, false)), Task.Run(() => measurement.StopSourceRead(infile, true)));
            Assert.Equal(2, capture.Samples.Length);
            Assert.Equal(new[] { "local_infile", "parameter" }, capture.Samples.Select(s => (string)s.Tags["oceanbase.source.kind"]!).OrderBy(s => s));
            Assert.All(capture.Samples, s => Assert.Equal("timeout", s.Tags["oceanbase.outcome"]));
            measurement.Finish(null); Assert.Equal(2, capture.Samples.Length);
        }

        [Fact]
        public void SourceReadsDisabledDoesNotStartTimingWithOnlyProtocolEnabled()
        {
            Assert.Null(DriverDiagnostics.StartSessionIo(OceanBaseCompatibilityMode.MySql));
            using var listener = new MeterListener(); var count = 0;
            listener.InstrumentPublished = (instrument, owner) =>
            { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.protocol.io.duration") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => count++); listener.Start();
            var measurement = DriverDiagnostics.StartSessionIo(OceanBaseCompatibilityMode.MySql)!;
            Assert.False(measurement.SourceReadsEnabled); Assert.Equal(0, measurement.StartSourceRead());
            var protocol = measurement.Start(); Assert.NotEqual(0, protocol);
            measurement.Stop(protocol, false); measurement.Finish(null); Assert.Equal(1, count);
        }
    }
}
