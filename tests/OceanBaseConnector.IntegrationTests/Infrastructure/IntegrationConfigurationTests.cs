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
    [InlineData("SslMode=Disabled", "Disabled")]
    [InlineData("SslMode=Required", "Disabled")]
    [InlineData("TLS Mode=Required", "Disabled")]
    public void TlsOptionOverridesConnectionStringAlias(string connectionTls, string expected)
    {
        var json = "{\"profiles\":[{\"name\":\"direct\",\"mode\":\"MySql\",\"connectionString\":\"Server=localhost;User ID=test;" +
            connectionTls + "\",\"options\":{\"tlsMode\":\"Disabled\"}}]}";

        var profile = Assert.Single(IntegrationProfileConfig.Parse(json));
        Assert.Equal(expected, new OceanBaseConnectionStringBuilder(profile.ConnectionString).TlsMode.ToString());
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
