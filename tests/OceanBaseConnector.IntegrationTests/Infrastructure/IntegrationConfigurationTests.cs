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
        Assert.Equal(mode, profile.CreateBuilder().CompatibilityMode);
    }

    [Theory]
    [InlineData("MySql", "Oracle")]
    [InlineData("Oracle", "MySql")]
    public void ConflictingCompatibilityModeIsRejected(string mode, string compatibilityMode)
    {
        var json = "{\"profiles\":[{\"name\":\"direct\",\"mode\":\"" + mode +
            "\",\"connectionString\":\"" + ConnectionString + ";CompatibilityMode=" + compatibilityMode + "\"}]}";
        var error = Assert.Throws<InvalidOperationException>(() => IntegrationProfileConfig.Parse(json));
        Assert.Contains("CompatibilityMode", error.Message);
    }

    [Theory]
    [InlineData("MySql", OceanBaseCompatibilityMode.MySql)]
    [InlineData("Oracle", OceanBaseCompatibilityMode.Oracle)]
    public void AutoConnectionModeUsesTheConcreteProfileMode(string mode, OceanBaseCompatibilityMode expected)
    {
        var json = "{\"profiles\":[{\"name\":\"direct\",\"mode\":\"" + mode +
            "\",\"connectionString\":\"" + ConnectionString + ";CompatibilityMode=Auto\"}]}";
        Assert.Equal(expected, Assert.Single(IntegrationProfileConfig.Parse(json)).CreateBuilder().CompatibilityMode);
    }

    [Fact]
    public void ProfileRequiresAConcreteTenantMode()
    {
        var profile = new IntegrationProfile { Name = "direct", Mode = OceanBaseCompatibilityMode.Auto, ConnectionString = ConnectionString };
        Assert.Throws<InvalidOperationException>(() => profile.CreateBuilder());
        var json = "{\"profiles\":[{\"name\":\"direct\",\"mode\":\"Auto\",\"connectionString\":\"" + ConnectionString + "\"}]}";
        Assert.Throws<InvalidOperationException>(() => IntegrationProfileConfig.Parse(json));
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

    [Fact]
    public void ServerLimitationsArePreservedAsProfileMetadata()
    {
        var json = "{\"profiles\":[{\"name\":\"direct\",\"mode\":\"Oracle\",\"connectionString\":\"" + ConnectionString +
            "\",\"capabilities\":{\"serverLimitations\":[\"oracle-euckr\"]}}]}";

        var profile = Assert.Single(IntegrationProfileConfig.Parse(json));
        Assert.True(profile.HasServerLimitation("oracle-euckr"));
        Assert.False(profile.HasServerLimitation("oracle-sjis-big5"));
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
