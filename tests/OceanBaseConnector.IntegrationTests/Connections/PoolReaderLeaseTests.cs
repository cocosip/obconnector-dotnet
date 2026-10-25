using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class PoolReaderLeaseTests
{
    public static TheoryData<string, bool> Profiles()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        { rows.Add(profile.Name, false); rows.Add(profile.Name, true); }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task OldCloseConnectionReaderDoesNotCloseReopenedPooledLease(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 5;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var session = connection.GetOpenSession(); var generation = session.Generation;
        await using var command = db.Command(connection, db.SelectOne);
        await using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.CloseConnection) : command.ExecuteReader(CommandBehavior.CloseConnection);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        DatabaseContext.EqualNumber(reader.GetValue(0), 1, "The original reader must consume real SQL.");
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        Assert.Same(session, connection.GetOpenSession()); Assert.True(session.Generation > generation);
        if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
        Assert.Equal(ConnectionState.Open, connection.State);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, default), 1, "Disposing the old reader must preserve the new lease.");
    }
}
