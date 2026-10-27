using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceLocalInfileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalInfileCasesAreSkippedWhenProfileIsDisabled(bool explicitFalse)
    {
        var json = "{\"mode\":\"MySql\",\"connectionString\":\"Server=unused;User ID=u;TLS Mode=Disabled;Allow Local Infile=true\"" +
            (explicitFalse ? ",\"localInfileProfile\":false" : "") + "}";
        var config = AcceptanceConfig.Parse(json);
        var builder = new OceanBaseConnectionStringBuilder(config.ResolveConnectionString(_ => null));
        var cases = DatabaseCases.All().Where(c => c.RequiresLocalInfileProfile).ToArray();

        Assert.Equal(2, cases.Length);
        Assert.All(cases, test => Assert.Equal("Local Infile profile is disabled.", AcceptanceSuite.SkipReason(config, builder, test)));
    }

    [Fact]
    public void OracleLocalInfileProfileIsRejectedBeforeNetworking()
    {
        var config = AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionString\":\"Server=unused;User ID=u;TLS Mode=Disabled;Allow Local Infile=true\",\"localInfileProfile\":true}");

        var error = Assert.Throws<ArgumentException>(() => config.ResolveConnectionString(_ => null));
        Assert.Contains("MySql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalInfileProfileRequiresExplicitCapability()
    {
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"Server=unused;User ID=u;TLS Mode=Disabled\",\"localInfileProfile\":true}");

        var error = Assert.Throws<ArgumentException>(() => config.ResolveConnectionString(_ => null));
        Assert.Contains("Allow Local Infile=true", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidMySqlLocalInfileProfileSelectsBothCases()
    {
        var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"Server=unused;User ID=u;TLS Mode=Disabled;Allow Local Infile=true\",\"localInfileProfile\":true,\"allowCreateObjects\":true}");
        var builder = new OceanBaseConnectionStringBuilder(config.ResolveConnectionString(_ => null));
        var cases = DatabaseCases.All().Where(c => c.RequiresLocalInfileProfile).ToArray();

        Assert.Equal(new[] { "streams.local-infile.sync", "streams.local-infile.async" }, cases.Select(c => c.Name));
        Assert.All(cases, test => Assert.Null(AcceptanceSuite.SkipReason(config, builder, test)));
    }
}
