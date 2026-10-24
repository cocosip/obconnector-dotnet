using System;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

[Trait("Category", "Configuration")]
public sealed class IntegrationConfigurationTests
{
    private const string ConnectionString = "Server=localhost;User ID=test;TLS Mode=Disabled";

    [Theory]
    [InlineData("mysql-direct", OceanBaseCompatibilityMode.MySql)]
    [InlineData("mysql-proxy", OceanBaseCompatibilityMode.MySql)]
    [InlineData("oracle-direct", OceanBaseCompatibilityMode.Oracle)]
    [InlineData("oracle-proxy", OceanBaseCompatibilityMode.Oracle)]
    public void LegacyProfilesKeepTheirMode(string name, OceanBaseCompatibilityMode mode)
    {
        var profile = IntegrationProfileConfig.Legacy(name, ConnectionString, "direct");
        Assert.Equal(mode, profile.Mode);
        Assert.Equal(mode, profile.CreateBuilder().ExpectedMode);
    }

    [Theory]
    [InlineData("MySql", "Oracle")]
    [InlineData("Oracle", "MySql")]
    public void ConflictingExpectedModeIsRejected(string mode, string expectedMode)
    {
        var json = "{\"profiles\":[{\"name\":\"direct\",\"mode\":\"" + mode +
            "\",\"connectionString\":\"" + ConnectionString + ";ExpectedMode=" + expectedMode + "\"}]}";
        var error = Assert.Throws<InvalidOperationException>(() => IntegrationProfileConfig.Parse(json));
        Assert.Contains("ExpectedMode", error.Message);
    }

    [Theory]
    [InlineData("\"name\":\"direct\",\"name\":\"other\",\"mode\":\"MySql\"")]
    [InlineData("\"name\":\"direct\",\"mode\":\"MySql\",\"Mode\":\"Oracle\"")]
    [InlineData("\"name\":\"direct\",\"mode\":\"MySql\",\"options\":{\"tlsMode\":\"Disabled\",\"tlsMode\":\"Required\"}")]
    public void DuplicateProfilePropertiesAreRejected(string members)
    {
        var json = "{\"profiles\":[{" + members + ",\"connectionString\":\"" + ConnectionString + "\"}]}";
        var error = Assert.Throws<InvalidOperationException>(() => IntegrationProfileConfig.Parse(json));
        Assert.Contains("Duplicate configuration property", error.Message);
    }
}
