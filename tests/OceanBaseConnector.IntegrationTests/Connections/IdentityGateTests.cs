using System;
using System.Data;
using System.Security.Authentication;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

/// <summary>
/// Identity and capability gates: the driver must refuse sessions that do not
/// match its contract instead of silently degrading.
/// </summary>
public sealed class IdentityGateTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task ExpectedModeMismatchIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.ExpectedMode = db.Oracle ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        var error = await Assert.ThrowsAsync<OceanBaseException>(connection.OpenAsync);
        Assert.Contains("ExpectedMode", error.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task IncorrectTlsServerNameIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.TlsMode = OceanBaseTlsMode.VerifyFull;
        builder.TlsServerName = "invalid-name.example.invalid";
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        var error = await Assert.ThrowsAsync<OceanBaseException>(connection.OpenAsync);
        Assert.Equal(ConnectionState.Closed, connection.State);
        if (db.Profile.HasTag("tls"))
            Assert.IsAssignableFrom<AuthenticationException>(error.InnerException);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task PoolingRoundTripReusesPhysicalSession(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var first = connection.GetOpenSession();
        await connection.CloseAsync();
        await connection.OpenAsync();
        Assert.Same(first, connection.GetOpenSession());
        await connection.CloseAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task EnlistTransactionIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        Assert.Throws<NotSupportedException>(() => connection.EnlistTransaction(null));
    }

    [IntegrationTheory("unsupportedAuth")]
    [MemberData(nameof(ProfileTheoryData.WithUnsupportedAuth), MemberType = typeof(ProfileTheoryData))]
    public async Task UnsupportedAuthenticationPluginIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.NotNull(db.Profile.UnsupportedAuthConnectionString);
        await using var connection = new OceanBaseConnection(db.Profile.UnsupportedAuthConnectionString!);
        var error = await Assert.ThrowsAsync<OceanBaseException>(connection.OpenAsync);
        Assert.Contains("not supported", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, error.ServerErrorCode);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [IntegrationTheory("autocommitDisabled")]
    [MemberData(nameof(ProfileTheoryData.WithAutocommitDisabled), MemberType = typeof(ProfileTheoryData))]
    public async Task AutocommitDisabledSessionIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.NotNull(db.Profile.AutocommitDisabledConnectionString);
        await using var connection = new OceanBaseConnection(db.Profile.AutocommitDisabledConnectionString!);
        var error = await Assert.ThrowsAsync<OceanBaseException>(connection.OpenAsync);
        Assert.Contains("autocommit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
