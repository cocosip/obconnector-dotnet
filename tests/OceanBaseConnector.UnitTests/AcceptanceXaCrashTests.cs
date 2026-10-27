using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceXaCrashTests
{
    [Fact]
    public async Task CoordinatorCrashRequiresSeparateOptInBeforeConnectionOrDdl()
    {
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.xa-crash-recovery.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, cases.Length);
        Assert.All(cases, c => Assert.Null(c.DeferredReason));
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"Server=unused;User ID=synthetic;TLS Mode=Disabled\",\"allowCreateObjects\":true}");
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.All(report.Results, r => { Assert.Equal("skipped", r.Status); Assert.Contains("xaCrashProfile", r.Detail); });
        Assert.Empty(report.Objects); Assert.Empty(report.Servers);
    }

    [Theory]
    [InlineData("Oracle", false, true, "Prepare Execute")]
    [InlineData("MySql", false, false, "allowCreateObjects")]
    public async Task CrashProfileCannotBypassExistingPrerequisites(string mode, bool prepare, bool objects, string reason)
    {
        var config = AcceptanceConfig.Parse("{\"mode\":\"" + mode + "\",\"connectionString\":\"Server=unused;User ID=synthetic;TLS Mode=Disabled;Use Oracle Prepare Execute=" + prepare.ToString().ToLowerInvariant() + "\",\"xaCrashProfile\":true,\"allowCreateObjects\":" + objects.ToString().ToLowerInvariant() + "}");
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.xa-crash-recovery.", StringComparison.Ordinal));
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.Equal(4, report.Skipped); Assert.All(report.Results, r => Assert.Contains(reason, r.Detail));
        Assert.Empty(report.Objects); Assert.Empty(report.Servers);
    }
}
