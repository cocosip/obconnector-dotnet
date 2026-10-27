using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceNetworkTests
{
    [Fact]
    public void InvalidRelayConfigurationIsRejectedBeforeStartingListener()
    {
        Assert.Throws<ArgumentException>(() => new TransactionResponseDropProxy("unused", 1, "not-a-transaction-end", false));
        Assert.Throws<ArgumentException>(() => new TransactionResponseDropProxy("unused", 1, "", CommandDropPoint.WholeResponse));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransactionResponseDropProxy("unused", 1, "INSERT", (CommandDropPoint)99));
    }

    [Fact]
    public async Task NetworkFaultsRequireSeparateOptInBeforeAnyConnectionOrDdl()
    {
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.network-outcome-unknown.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(16, cases.Length);
        Assert.All(cases, c => Assert.Null(c.DeferredReason));
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"Server=unused;User ID=synthetic;TLS Mode=Disabled\",\"allowCreateObjects\":true}");
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.All(report.Results, r => { Assert.Equal("skipped", r.Status); Assert.Contains("networkFaultProfile", r.Detail); });
        Assert.Empty(report.Objects);
        Assert.Empty(report.Servers);
    }

    [Theory]
    [InlineData("TLS Mode=Required", "TLS")]
    [InlineData("TLS Mode=Disabled;Use Compression=true", "compression")]
    [InlineData("TLS Mode=Disabled;Use Ob20=true", "OB20")]
    [InlineData("TLS Mode=Disabled;Server=one,two", "host")]
    [InlineData("TLS Mode=Disabled;Server=host:2881", "host")]
    public async Task IncompatibleNetworkProfilesSkipWithoutDowngradingOrConnecting(string options, string reason)
    {
        var server = options.Contains("Server=", StringComparison.Ordinal) ? "" : "Server=unused;";
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"" + server + "User ID=synthetic;" + options + "\",\"allowCreateObjects\":true,\"networkFaultProfile\":true}");
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.network-outcome-unknown.", StringComparison.Ordinal));
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.Equal(16, report.Skipped);
        Assert.All(report.Results, r => Assert.Contains(reason, r.Detail));
        Assert.Empty(report.Objects);
        Assert.Empty(report.Servers);
    }
}
