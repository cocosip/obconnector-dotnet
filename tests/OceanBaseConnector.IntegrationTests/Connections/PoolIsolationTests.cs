using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class PoolIsolationTests
{
    public static TheoryData<string, bool> Profiles() => Rows(_ => true);
    public static TheoryData<string, bool> ObjectProfiles() => Rows(p => p.CreateObjects);
    public static TheoryData<string, bool> LobProfiles() => Rows(p => p.Oracle && p.CreateBuilder().UseOraclePrepareExecute && p.CreateBuilder().UseLobLocators);
    public static TheoryData<string, bool> DatabaseProfiles() => Rows(p => !p.Oracle && p.AlternateDatabase is not null);

    private static TheoryData<string, bool> Rows(Func<IntegrationProfile, bool> applies)
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (applies(profile)) { rows.Add(profile.Name, false); rows.Add(profile.Name, true); }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task PreparedCommandRepreparesAfterResetAndReadOnlyBorrowersReuse(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        var generation = session.Generation;
        await using var command = db.Command(first, db.ParameterSql(db.ScalarSql(":value")));
        var value = DatabaseContext.Parameter(command, "value", 7, DbType.Int32);
        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
        for (var i = 0; i < 3; i++)
        {
            value.Value = 7 + i;
            await using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 7 + i, "Prepared execution must survive pool reset.");
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
            await Close(first, asynchronous);
            await Open(second, asynchronous);
            Assert.Same(session, second.GetOpenSession());
            Assert.True(session.Generation > generation, "Every successful reset must invalidate prepared state.");
            generation = session.Generation;
            DatabaseContext.EqualNumber(await Scalar(db, second, db.SelectOne, asynchronous), 1, "The next logical borrower must execute real SQL.");
            await Close(second, asynchronous);
            await Open(first, asynchronous);
            Assert.Same(session, first.GetOpenSession());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task ExplicitSessionStateIsDiscardedAndNextBorrowerGetsBaseline(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        var query = db.ScalarSql(db.Oracle ? "SESSIONTIMEZONE" : "@@session.time_zone");
        var baseline = Assert.IsType<string>(await Scalar(db, first, query, asynchronous));
        var changed = baseline == "+05:45" ? "+06:45" : "+05:45";
        await Execute(db, first, (db.Oracle ? "ALTER SESSION SET TIME_ZONE" : "SET SESSION time_zone") + " = '" + changed + "'", asynchronous);
        Assert.Equal(changed, await Scalar(db, first, query, asynchronous));
        await Close(first, asynchronous);
        await Open(second, asynchronous);
        Assert.NotSame(session, second.GetOpenSession());
        Assert.Equal(baseline, await Scalar(db, second, query, asynchronous));
    }

    [IntegrationTheory]
    [MemberData(nameof(ObjectProfiles))]
    public async Task FreshSessionsCommitImplicitWritesBeforeAndAfterClose(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var observer = await db.OpenAsync();
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var fresh = new OceanBaseConnection(builder.ConnectionString);
        await Open(fresh, asynchronous);
        await Execute(db, fresh, "INSERT INTO " + table + " VALUES (1)", asynchronous);
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        await Close(fresh, asynchronous);
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ObjectProfiles))]
    public async Task CloseRollsBackActiveTransactionAndRestoresAutocommitForNextBorrower(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        await using var transaction = asynchronous ? (OceanBaseTransaction)await first.BeginTransactionAsync() : (OceanBaseTransaction)first.BeginTransaction();
        await Execute(db, first, "INSERT INTO " + table + " VALUES (1)", asynchronous, transaction);
        DatabaseContext.EqualNumber(await Scalar(db, first, "SELECT COUNT(*) FROM " + table, asynchronous, transaction), 1, "The first borrower must have a pending write.");
        await Close(first, asynchronous);
        await Open(second, asynchronous);
        Assert.Same(session, second.GetOpenSession());
        DatabaseContext.EqualNumber(await Scalar(db, second, "SELECT COUNT(*) FROM " + table, asynchronous), 0, "The next borrower must not inherit the transaction or pending rows.");
        Assert.Equal(0, await db.CountAsync(setup, table, CancellationToken.None));
        await Execute(db, second, "INSERT INTO " + table + " VALUES (2)", asynchronous);
        Assert.Equal(1, await db.CountAsync(setup, table, CancellationToken.None));
        await Close(second, asynchronous);
        await Open(first, asynchronous);
        Assert.Same(session, first.GetOpenSession());
        DatabaseContext.EqualNumber(await Scalar(db, first, "SELECT COUNT(*) FROM " + table, asynchronous), 1, "The next lease must observe only the committed write.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task UnreadReaderDiscardsItsSessionAndCannotDisruptTheNextBorrower(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        await using var command = db.Command(first, db.SelectOne + " UNION ALL " + db.SelectOne);
        await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        await Close(first, asynchronous);
        await Open(second, asynchronous);
        Assert.NotSame(session, second.GetOpenSession());
        DatabaseContext.EqualNumber(await Scalar(db, second, db.SelectOne, asynchronous), 1, "Unread protocol data must not enter the next lease.");
        if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
        DatabaseContext.EqualNumber(await Scalar(db, second, db.SelectOne, asynchronous), 1, "Disposing an old reader must not affect a new borrower.");
    }

    [IntegrationTheory]
    [MemberData(nameof(LobProfiles))]
    public async Task ActiveTemporaryLobDiscardsItsSessionOnReturn(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        await using var lob = asynchronous ? await first.CreateTemporaryLobAsync(OceanBaseDbType.Blob) : first.CreateTemporaryLob(OceanBaseDbType.Blob);
        await Close(first, asynchronous);
        await Open(second, asynchronous);
        Assert.NotSame(session, second.GetOpenSession());
        if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
        DatabaseContext.EqualNumber(await Scalar(db, second, db.SelectOne, asynchronous), 1, "An old LOB handle must never enter the next borrower's session.");
    }

    [IntegrationTheory("alternateDatabase")]
    [MemberData(nameof(DatabaseProfiles))]
    public async Task ChangeDatabaseIsResetToTheConfiguredCatalog(string profileName, bool asynchronous)
    {
        await using var db = Context(profileName);
        await using var source = Source(db);
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        await Open(first, asynchronous);
        var session = first.GetOpenSession();
        var original = await Scalar(db, first, "SELECT DATABASE()", asynchronous);
        Assert.NotEqual(original, db.Profile.AlternateDatabase);
        if (asynchronous) await first.ChangeDatabaseAsync(db.Profile.AlternateDatabase!); else first.ChangeDatabase(db.Profile.AlternateDatabase!);
        Assert.Equal(db.Profile.AlternateDatabase, await Scalar(db, first, "SELECT DATABASE()", asynchronous));
        await Close(first, asynchronous);
        await Open(second, asynchronous);
        Assert.Same(session, second.GetOpenSession());
        Assert.Equal(original, await Scalar(db, second, "SELECT DATABASE()", asynchronous));
        Assert.Equal(db.Profile.CreateBuilder().Database, second.Database);
    }

    private static DatabaseContext Context(string name) => new(IntegrationEnvironment.GetProfile(name));

    private static OceanBaseDataSource Source(DatabaseContext db)
    {
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 5;
        return new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
    }

    private static async Task Open(OceanBaseConnection connection, bool asynchronous)
    { if (asynchronous) await connection.OpenAsync(); else connection.Open(); }

    private static async Task Close(OceanBaseConnection connection, bool asynchronous)
    { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }

    private static async Task<object?> Scalar(DatabaseContext db, OceanBaseConnection connection, string sql, bool asynchronous, OceanBaseTransaction? transaction = null)
    {
        await using var command = db.Command(connection, sql, transaction);
        return asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar();
    }

    private static async Task Execute(DatabaseContext db, OceanBaseConnection connection, string sql, bool asynchronous, OceanBaseTransaction? transaction = null)
    {
        await using var command = db.Command(connection, sql, transaction);
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
    }
}
