using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class DataSourceShutdownTests
{
    public static TheoryData<string, bool, bool> Profiles()
    {
        var rows = new TheoryData<string, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            foreach (var asynchronous in new[] { false, true })
                foreach (var pooling in new[] { false, true })
                    rows.Add(profile.Name, asynchronous, pooling);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task DisposedSourcePreservesReaderAndConnectionUntilClose(string profileName, bool asynchronous, bool pooling)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = pooling; builder.MinPoolSize = 0; builder.MaxPoolSize = 1;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var session = connection.GetOpenSession();
        await using var command = db.Command(connection, db.SelectOne);
        await using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
        {
            Assert.Equal(SessionState.Busy, session.State);
            if (asynchronous) await source.DisposeAsync(); else source.Dispose();
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), 1, "An existing reader must survive source disposal.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        DatabaseContext.EqualNumber(asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar(), 1,
            "An established connection must still execute after source disposal.");
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        Assert.Equal(SessionState.Closed, session.State);
        if (asynchronous) await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.OpenAsync());
        else Assert.Throws<ObjectDisposedException>(connection.Open);
    }
}
