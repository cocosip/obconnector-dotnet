using System.Diagnostics.Metrics;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

[Trait("Category", "Configuration")]
public sealed class PoolSoakMetricsTests
{
    [Fact]
    public void CapturesLiveResourcesAndBalancesTheirRelease()
    {
        using var capture = new PoolSoakMetrics();
        using var meter = new Meter("OceanBaseConnector");
        var sessions = meter.CreateUpDownCounter<long>("oceanbase.client.pool.sessions");
        var leased = meter.CreateUpDownCounter<long>("oceanbase.client.pool.leased");
        var waiters = meter.CreateUpDownCounter<long>("oceanbase.client.pool.waiters");
        var borrows = meter.CreateCounter<long>("oceanbase.client.pool.borrowed");
        sessions.Add(2); leased.Add(1); waiters.Add(3); borrows.Add(7);
        Assert.Equal((2L, 1L, 3L, 7L), capture.Snapshot());
        sessions.Add(-2); leased.Add(-1); waiters.Add(-3);
        Assert.Equal((0L, 0L, 0L, 7L), capture.Snapshot());
    }

    [Fact]
    public void IgnoresUnrelatedMeters()
    {
        using var capture = new PoolSoakMetrics();
        using var meter = new Meter("AnotherDriver");
        meter.CreateUpDownCounter<long>("oceanbase.client.pool.sessions").Add(1);
        Assert.Equal((0L, 0L, 0L, 0L), capture.Snapshot());
    }
}
