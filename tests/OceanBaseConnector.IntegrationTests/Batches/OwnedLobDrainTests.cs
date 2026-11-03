using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class OwnedLobDrainTests
{
    public static IEnumerable<object[]> Rows() => LobMultiSendTests.Rows();

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task OwnedNonQueryDrainsExternalLobsAndReusesPooledSession(string profileName, bool asynchronous, bool character)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY,value " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
        var bytes = Enumerable.Range(0, 100000).Select(i => (byte)(i % 251)).ToArray();
        var text = string.Concat(Enumerable.Repeat("患者😀z", 20000));
        await using (var seed = await setup.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob))
        {
            try
            {
                if (character) await seed.WriteTextAsync(0, text); else await seed.WriteBytesAsync(0, bytes, 0, bytes.Length);
                using var insert = db.Command(setup, "INSERT INTO " + table + " VALUES(1,:p)");
                DatabaseContext.Parameter(insert, "p", seed); Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            finally { await seed.FreeTemporaryAsync(CancellationToken.None); }
        }
        using (var query = db.Command(setup, "SELECT value FROM " + table + " WHERE id=1"))
        using (var stored = Assert.IsType<OceanBaseLob>(await query.ExecuteScalarAsync()))
            Assert.False(stored.Locator.HasInlineData, "Acceptance requires a real remote locator.");

        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 5;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = Assert.IsType<OceanBaseConnection>(await source.OpenConnectionAsync());
        var session = connection.GetOpenSession(); await connection.CloseAsync();
        using (var batch = source.CreateBatch())
        {
            foreach (var sql in new[]
            {
                "INSERT INTO " + table + " VALUES(2,NULL)",
                "SELECT value FROM " + table + " WHERE id=1",
                "SELECT value FROM " + table + " WHERE id=2",
                "SELECT value FROM " + table + " WHERE id=1",
                "UPDATE " + table + " SET value=NULL WHERE id=2",
            })
            {
                var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item);
            }
            Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            Assert.Equal(new[] { 1, -1, -1, -1, 1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
        }
        await using var reused = Assert.IsType<OceanBaseConnection>(await source.OpenConnectionAsync());
        Assert.Same(session, reused.GetOpenSession()); Assert.False(session.RequiresDiscardOnReturn);
        Assert.Equal(2, await db.CountAsync(setup, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(setup, "SELECT COUNT(*) FROM " + table + " WHERE id=2 AND value IS NULL", CancellationToken.None),
            1, "The batch must finish the DML after the LOB result groups.");
    }
}
