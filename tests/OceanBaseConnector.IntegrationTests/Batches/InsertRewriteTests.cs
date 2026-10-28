using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class InsertRewriteTests
{
    public static TheoryData<string, bool> Rows()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && !p.Oracle))
            foreach (var asynchronous in new[] { false, true }) rows.Add(profile.Name, asynchronous);
        return rows;
    }

    public static TheoryData<string, bool, bool> TransactionRows()
    {
        var rows = new TheoryData<string, bool, bool>();
        foreach (var row in Rows())
            foreach (var commit in new[] { false, true }) rows.Add((string)row[0], (bool)row[1], commit);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(TransactionRows))]
    public async Task RewrittenInsertsKeepDefaultsTriggersAndTransactionOutcome(string profileName, bool asynchronous, bool commit)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var observer = await db.OpenAsync();
        var (table, audit) = await CreateFixture(db, connection);
        await using var transaction = asynchronous
            ? (OceanBaseTransaction)await connection.BeginTransactionAsync()
            : (OceanBaseTransaction)connection.BeginTransaction();
        using var batch = (OceanBaseBatch)connection.CreateBatch();
        batch.RewriteBatchedInserts = true; batch.Transaction = transaction;
        foreach (var id in new[] { 1, 2, 3 }) Add(batch, table, id);
        Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
        Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), command => Assert.Equal(1, command.RecordsAffected));
        Assert.Equal(3, await db.CountAsync(connection, audit, CancellationToken.None, transaction));
        Assert.Equal("默认", Assert.IsType<string>(await db.ScalarAsync(connection,
            "SELECT value FROM " + table + " WHERE id = 1", CancellationToken.None, transaction)));
        Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
        Assert.Equal(0, await db.CountAsync(observer, audit, CancellationToken.None));
        if (asynchronous)
        {
            if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        }
        else { if (commit) transaction.Commit(); else transaction.Rollback(); }
        Assert.Equal(commit ? 3 : 0, await db.CountAsync(observer, table, CancellationToken.None));
        Assert.Equal(commit ? 3 : 0, await db.CountAsync(observer, audit, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE value = '默认'", CancellationToken.None),
            commit ? 3 : 0, "Every rewritten row must receive its server default.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task DuplicateGroupKeepsConfirmedPrefixAndDoesNotExecuteSuffix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var (table, audit) = await CreateFixture(db, connection);
        using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
        batch.BatchCommands.Add(new OceanBaseBatchCommand { CommandText = "INSERT INTO " + table + " (id) VALUES (10)" });
        Add(batch, table, 20); Add(batch, table, 20);
        batch.BatchCommands.Add(new OceanBaseBatchCommand { CommandText = "INSERT INTO " + table + " (id) VALUES (30)" });
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery();
        });
        Assert.Equal(1062, error.ServerErrorCode);
        Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Null(error.BatchCommand);
        Assert.Equal(new[] { batch.BatchCommands[1], batch.BatchCommands[2] }, error.BatchCommandGroup);
        Assert.Equal(new[] { 1, -1, -1, -1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(command => command.RecordsAffected));
        await using var observer = await db.OpenAsync();
        foreach (var name in new[] { table, audit })
        {
            Assert.Equal(1, await db.CountAsync(observer, name, CancellationToken.None));
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + name, CancellationToken.None), 10,
                "Only the confirmed prefix and its trigger may be committed.");
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A confirmed duplicate error must keep the connection usable.");
    }

    private static void Add(OceanBaseBatch batch, string table, int id)
    {
        var command = new OceanBaseBatchCommand { CommandText = "INSERT INTO " + table + " (id) VALUES (?)" };
        command.Parameters.Add(new OceanBaseParameter { DbType = DbType.Int32, Value = id });
        batch.BatchCommands.Add(command);
    }

    private static async Task<(string Table, string Audit)> CreateFixture(DatabaseContext db, OceanBaseConnection connection)
    {
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value VARCHAR(100) DEFAULT '默认'", CancellationToken.None);
        var audit = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var trigger = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        db.RegisterCleanup("TRIGGER", trigger, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP TRIGGER IF EXISTS " + trigger, CancellationToken.None);
        });
        await db.ExecuteAsync(connection, "CREATE TRIGGER " + trigger + " AFTER INSERT ON " + table +
            " FOR EACH ROW INSERT INTO " + audit + " (id) VALUES (NEW.id)", CancellationToken.None);
        return (table, audit);
    }
}
