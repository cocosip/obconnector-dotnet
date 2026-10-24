using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class ConnectionTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public Task OpenCloseReopenIdentifiesServerSync(string profileName) => Core(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public Task OpenCloseReopenIdentifiesServerAsync(string profileName) => Core(profileName, asynchronous: true);

    private static async Task Core(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = db.CreateConnection();
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        Assert.Equal(db.Profile.Mode, connection.CompatibilityMode);
        Assert.False(string.IsNullOrWhiteSpace(connection.ServerVersion), "ServerVersion must be available after Open.");
        var actualVersion = await db.ScalarAsync(connection, db.ScalarSql("ob_version()"), CancellationToken.None);
        Assert.Equal(connection.ServerVersion, actualVersion);
        using (var source = connection.GetSchema("DataSourceInformation"))
            Assert.Equal(actualVersion, source.Rows[0]["DataSourceProductVersion"]);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A fresh connection must execute scalar SQL.");
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A reopened connection must execute scalar SQL.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task FactoryAndDataSourceConnectionsExecute(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var factoryConnection = OceanBaseProviderFactory.Instance.CreateConnection();
        Assert.NotNull(factoryConnection);
        factoryConnection.ConnectionString = db.Profile.CreateBuilder().ConnectionString;
        await factoryConnection.OpenAsync();
        using (var command = factoryConnection.CreateCommand())
        {
            command.CommandText = db.SelectOne;
            DatabaseContext.EqualNumber(await command.ExecuteScalarAsync(), 1, "Factory connection must execute SQL.");
        }
        await using var source = new OceanBaseDataSourceBuilder(db.Profile.CreateBuilder().ConnectionString).Build();
        await using var dataSourceConnection = await source.OpenConnectionAsync();
        using var dataCommand = dataSourceConnection.CreateCommand();
        dataCommand.CommandText = db.SelectOne;
        DatabaseContext.EqualNumber(await dataCommand.ExecuteScalarAsync(), 1, "Data source connection must execute SQL.");
    }

    [IntegrationTheory("alternateDatabase")]
    [MemberData(nameof(ProfileTheoryData.WithAlternateDatabase), MemberType = typeof(ProfileTheoryData))]
    public async Task ChangeDatabaseSwitchesAndRestores(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.False(db.Oracle, "ChangeDatabase is a MySQL-mode surface.");
        await using var connection = await db.OpenAsync();
        var original = connection.Database;
        try
        {
            await connection.ChangeDatabaseAsync(db.Profile.AlternateDatabase!, CancellationToken.None);
            Assert.Equal(db.Profile.AlternateDatabase, connection.Database);
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
                "A changed database must execute SQL.");
        }
        finally
        {
            await connection.ChangeDatabaseAsync(original, CancellationToken.None);
            Assert.Equal(original, connection.Database);
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task OpenConnectionHidesPasswordByDefault(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        await using var connection = db.CreateConnection();
        await connection.OpenAsync();
        if (builder.Password.Length > 0)
            Assert.DoesNotContain(builder.Password, connection.ConnectionString, StringComparison.Ordinal);
        var sanitized = new OceanBaseConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(0, sanitized.Password.Length);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task AddressResolverSuppliesWorkingEndpoints(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var server = db.Profile.CreateBuilder().Server;
        var connection = db.CreateConnection();
        connection.AddressResolver = new FixedAddressResolver(server);
        await using (connection)
        {
            await connection.OpenAsync();
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
                "A resolver-supplied address must open a working connection.");
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task UnreachableEndpointFailsWithinTimeout(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.Server = "127.0.0.1";
        builder.Port = 1;
        builder.ConnectionTimeout = 3;
        var started = DateTimeOffset.UtcNow;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        var error = await Record.ExceptionAsync(() => connection.OpenAsync());
        Assert.True(error is OceanBaseException or TimeoutException,
            "An unreachable endpoint must fail with a connection error or timeout.");
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30),
            "A refused endpoint must fail within the connection budget, not hang.");
    }

    private sealed class FixedAddressResolver(string server) : IOceanBaseAddressResolver
    {
        public string Resolve(CancellationToken cancellationToken) => server;
        public Task<string> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(server);
    }
}
