using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceTopologyTests
{
    [Fact]
    public async Task TopologyCasesRequireSeparateOptInBeforeNetworking()
    {
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.topology-failover.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, cases.Length);
        Assert.All(cases, c => Assert.Null(c.DeferredReason));
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"Server=unused;User ID=synthetic;TLS Mode=Disabled\"}");
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.Equal(6, report.Skipped);
        Assert.All(report.Results, r => Assert.Contains("topologyFaultProfile", r.Detail));
        Assert.Empty(report.Objects); Assert.Empty(report.Servers);
    }

    [Theory]
    [InlineData("TLS Mode=Required", "TLS")]
    [InlineData("TLS Mode=Disabled;Use Compression=true", "compression")]
    [InlineData("TLS Mode=Disabled;Use Ob20=true", "OB20")]
    [InlineData("TLS Mode=Disabled;Server=one,two", "host")]
    [InlineData("TLS Mode=Disabled;Server=host:2881", "host")]
    public async Task IncompatibleTopologyProfilesSkipWithoutDowngrading(string options, string reason)
    {
        var server = options.Contains("Server=", StringComparison.Ordinal) ? "" : "Server=unused;";
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"" + server + "User ID=synthetic;" + options + "\",\"topologyFaultProfile\":true}");
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.topology-failover.", StringComparison.Ordinal));
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.Equal(6, report.Skipped);
        Assert.All(report.Results, r => Assert.Contains(reason, r.Detail));
        Assert.Empty(report.Objects); Assert.Empty(report.Servers);
    }
}
