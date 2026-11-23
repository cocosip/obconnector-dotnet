using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class CompatibilityModeConfigurationTests
{
    private const string ConnectionString = "Server=localhost;User ID=test";

    [Fact]
    public void ConfigurationAndAuthenticatedModeUseTheSameEnum()
    {
        var configurationType = typeof(OceanBaseConnectionStringBuilder).GetProperty("CompatibilityMode")!.PropertyType;
        var sessionType = Nullable.GetUnderlyingType(typeof(OceanBaseConnection).GetProperty("CompatibilityMode")!.PropertyType)!;
        var parameterType = Nullable.GetUnderlyingType(typeof(OceanBaseParameter).GetProperty("CompatibilityMode")!.PropertyType)!;

        Assert.Equal(typeof(OceanBaseCompatibilityMode), configurationType);
        Assert.Equal(typeof(OceanBaseCompatibilityMode), sessionType);
        Assert.Equal(sessionType, parameterType);
        Assert.Contains("Auto", Enum.GetNames(configurationType));
        Assert.Equal(0, (int)OceanBaseCompatibilityMode.Auto);
        Assert.Equal(1, (int)OceanBaseCompatibilityMode.MySql);
        Assert.Equal(2, (int)OceanBaseCompatibilityMode.Oracle);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(99)]
    public void AuthenticatedSessionRejectsAutoAndUnknownModes(int mode)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectResult(
            "4.4.2.2", 1, (OceanBaseCompatibilityMode)mode, new NegotiatedCapabilities(default, default),
            default, default, new Dictionary<string, string>()));
        Assert.Equal("mode", error.ParamName);
    }

    [Theory]
    [InlineData("", "Auto")]
    [InlineData(";CompatibilityMode=Auto", "Auto")]
    [InlineData(";compatibilitymode=auto", "Auto")]
    [InlineData(";CompatibilityMode=MySQL", "MySql")]
    [InlineData(";CompatibilityMode=oracle", "Oracle")]
    public void PoolConfigurationUsesTheEffectiveCompatibilityMode(string configuration, string mode)
    {
        var builder = new OceanBaseConnectionStringBuilder(ConnectionString + configuration);
        var canonical = builder.ToPoolConfiguration();

        Assert.Contains("CompatibilityMode=" + mode, canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, new OceanBaseConnectionStringBuilder(canonical).ToPoolConfiguration());
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("auto")]
    public void OmittedAndExplicitAutoSharePoolConfiguration(string mode)
    {
        Assert.Equal(new OceanBaseConnectionStringBuilder(ConnectionString).ToPoolConfiguration(),
            new OceanBaseConnectionStringBuilder(ConnectionString + ";CompatibilityMode=" + mode).ToPoolConfiguration());
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("mysql,oracle")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2")]
    public void InvalidCompatibilityModesAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder(
            ConnectionString + ";CompatibilityMode=" + value));
    }

    [Theory]
    [InlineData(OceanBaseCompatibilityMode.Auto)]
    [InlineData(OceanBaseCompatibilityMode.MySql)]
    [InlineData(OceanBaseCompatibilityMode.Oracle)]
    public void TypedModeRoundTripsAndFreezesForEveryEndpoint(OceanBaseCompatibilityMode mode)
    {
        var builder = new OceanBaseConnectionStringBuilder("Server=one,two;User ID=test") { CompatibilityMode = mode };
        var options = builder.Build();
        Assert.Equal(mode, new OceanBaseConnectionStringBuilder(builder.ConnectionString).CompatibilityMode);

        builder.CompatibilityMode = mode == OceanBaseCompatibilityMode.Oracle ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
        Assert.Equal(mode, options.CompatibilityMode);
        Assert.All(options.Endpoints, endpoint => Assert.Equal(mode, options.ForEndpoint(endpoint).CompatibilityMode));
        Assert.Equal(mode, options.WithCredential(new OceanBaseCredential("other", "password", 1)).CompatibilityMode);

        builder.Remove(OceanBaseConnectionStringBuilder.CompatibilityModeKey);
        Assert.Equal(OceanBaseCompatibilityMode.Auto, builder.CompatibilityMode);
        Assert.Equal(OceanBaseCompatibilityMode.Auto, builder.Build().CompatibilityMode);
    }

    [Fact]
    public void UndefinedTypedModeIsRejected()
    {
        var builder = new OceanBaseConnectionStringBuilder(ConnectionString);
        Assert.Throws<ArgumentException>(() => builder.CompatibilityMode = (OceanBaseCompatibilityMode)99);
    }
}
