using System;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

[Trait("Category", "Configuration")]
public sealed class PoolSoakConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AbsentDurationDoesNotOptIntoSoak(string? value) =>
        Assert.Null(PoolSoakTheoryAttribute.ParseDuration(value));

    [Theory]
    [InlineData("1", 1)]
    [InlineData("3600", 3600)]
    [InlineData("86400", 86400)]
    public void ExplicitDurationBoundsTheRun(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PoolSoakTheoryAttribute.ParseDuration(value));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    [InlineData(" ")]
    public void InvalidDurationFailsRatherThanSkipping(string value) =>
        Assert.Throws<ArgumentException>(() => PoolSoakTheoryAttribute.ParseDuration(value));
}
