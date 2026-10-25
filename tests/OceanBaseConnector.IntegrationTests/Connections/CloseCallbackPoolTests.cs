using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class CloseCallbackPoolTests
{
    public static TheoryData<string, bool, bool, bool> Profiles()
    {
        var rows = new TheoryData<string, bool, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            foreach (var asynchronous in new[] { false, true })
                foreach (var transaction in new[] { false, true })
                    foreach (var other in new[] { false, true })
                        rows.Add(profile.Name, asynchronous, transaction, other);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task ClosedCallbackReturnsOldTransactionAndReusesTheSamePhysicalSession(string profileName, bool asynchronous, bool transaction, bool other)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 5;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        await using var next = (OceanBaseConnection)source.CreateConnection();
        await connection.OpenAsync();
        var table = await db.TableAsync(connection, "id INT", default);
        await connection.CloseAsync(); await connection.OpenAsync();
        var original = connection.GetOpenSession(); var generation = original.Generation;
        await using var tx = transaction ? (OceanBaseTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted) : null;
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", default, tx);
        var target = other ? next : connection;
        var reopen = true;
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState != ConnectionState.Closed || !reopen) return;
            reopen = false; target.Open();
            using var command = db.Command(target, "SELECT COUNT(*) FROM " + table);
            DatabaseContext.EqualNumber(command.ExecuteScalar(), transaction ? 0 : 1, "Callback reads must follow the captured transaction cleanup.");
        };
        try
        {
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            Assert.Same(original, target.GetOpenSession()); Assert.True(original.Generation > generation);
            Assert.Equal(ConnectionState.Open, target.State); if (tx != null) Assert.Null(tx.Connection);
            Assert.Equal(transaction ? 0 : 1, await db.CountAsync(target, table, default));
        }
        finally { reopen = false; }
    }
}
