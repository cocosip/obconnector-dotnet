using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Commands;

public sealed class StatementCacheTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task CachedAndHeldStatementsReprepareAfterDdlAndPoolReset(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(System.TimeSpan.FromSeconds(30));
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.StatementCacheSize = 2;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        await Open();
        var session = connection.GetOpenSession();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "VARCHAR2(100)" : "VARCHAR(100)"), deadline.Token);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1, 'cached')", deadline.Token);
        var sql = "SELECT * FROM " + table + " WHERE id = " + db.ParameterSql(":id");
        object initial;
        using (var first = Create(sql))
        {
            await Prepare(first);
            initial = Prepared(first);
            await Read(first, columns: 2);
        }
        using var held = Create(sql);
        await Prepare(held);
        Assert.Same(initial, Prepared(held));
        await Read(held, columns: 2);
        var idleSql = "SELECT id FROM " + table + " WHERE id = " + db.ParameterSql(":id");
        object idleBeforeDdl;
        using (var idle = Create(idleSql))
        {
            await Prepare(idle);
            idleBeforeDdl = Prepared(idle);
            await Read(idle, columns: 1);
        }
        var ddl = "ALTER TABLE " + table + (db.Oracle ? " ADD (extra INTEGER DEFAULT 7)" : " ADD extra INTEGER DEFAULT 7");
        await db.ExecuteAsync(connection, ddl, deadline.Token);
        await Read(held, columns: 3);
        Assert.NotSame(initial, Prepared(held));
        using (var idle = Create(idleSql))
        {
            await Prepare(idle);
            Assert.NotSame(idleBeforeDdl, Prepared(idle));
            await Read(idle, columns: 1);
        }
        var beforeReset = Prepared(held);
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        await Open();
        Assert.Same(session, connection.GetOpenSession());
        await Read(held, columns: 3);
        Assert.NotSame(beforeReset, Prepared(held));

        OceanBaseCommand Create(string text)
        {
            var command = db.Command(connection, text);
            DatabaseContext.Parameter(command, "id", 1, DbType.Int32);
            return command;
        }
        async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
        async Task Prepare(OceanBaseCommand command) { if (asynchronous) await command.PrepareAsync(deadline.Token); else command.Prepare(); }
        async Task Read(OceanBaseCommand command, int columns)
        {
            var reader = asynchronous ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader();
            try
            {
                Assert.Equal(columns, reader.FieldCount);
                Assert.True(asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 1, "The prepared predicate must still bind.");
                if (columns >= 2) Assert.Equal("cached", reader.GetString(1));
                if (columns == 3) DatabaseContext.EqualNumber(reader.GetValue(2), 7, "DDL must refresh result metadata.");
                Assert.False(asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync(deadline.Token) : reader.NextResult());
            }
            finally { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); }
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ExternalColumnDropConfirmedErrorEvictsFailedCachedStatement(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(System.TimeSpan.FromSeconds(30));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.StatementCacheSize = 0;
        await using var external = new OceanBaseConnection(builder.ConnectionString);
        await external.OpenAsync(deadline.Token);
        var textType = db.Oracle ? "VARCHAR2(100)" : "VARCHAR(100)";
        var table = await db.TableAsync(external, "id INTEGER PRIMARY KEY, value " + textType, deadline.Token);
        await db.ExecuteAsync(external, "INSERT INTO " + table + " VALUES (1, 'before')", deadline.Token);
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.StatementCacheSize = 2;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open();
        var session = connection.GetOpenSession();
        var sql = "SELECT * FROM " + table + " WHERE id=" + db.ParameterSql(":id") + " AND value IS NOT NULL";
        object original;
        using (var first = Create())
        {
            await Prepare(first); original = Prepared(first);
            await Read(first, false);
        }
        using (var cached = Create())
        {
            await Prepare(cached); Assert.Same(original, Prepared(cached));
            await db.ExecuteAsync(external, "ALTER TABLE " + table + " DROP COLUMN value", deadline.Token);
            var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
            {
                if (asynchronous) await cached.ExecuteScalarAsync(deadline.Token); else cached.ExecuteScalar();
            });
            Assert.False(error.IsExecutionOutcomeUnknown); Assert.NotEqual(0, error.ServerErrorCode);
            Assert.Equal(ConnectionState.Open, connection.State); Assert.Same(session, connection.GetOpenSession());
        }
        // External DDL is deliberately not tracked by the client's schema epoch.
        await db.ExecuteAsync(external, "ALTER TABLE " + table + (db.Oracle ? " ADD (value " + textType + ", extra INTEGER)" : " ADD value " + textType + ", ADD extra INTEGER"), deadline.Token);
        await db.ExecuteAsync(external, "UPDATE " + table + " SET value='after', extra=9 WHERE id=1", deadline.Token);
        using (var replacement = Create())
        {
            await Prepare(replacement); Assert.NotSame(original, Prepared(replacement));
            await Read(replacement, true);
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, deadline.Token), 1, "A confirmed missing-column error must preserve the session.");
        Assert.Same(session, connection.GetOpenSession());

        OceanBaseCommand Create()
        {
            var command = db.Command(connection, sql);
            DatabaseContext.Parameter(command, "id", 1, DbType.Int32);
            return command;
        }
        async Task Prepare(OceanBaseCommand command) { if (asynchronous) await command.PrepareAsync(deadline.Token); else command.Prepare(); }
        async Task Read(OceanBaseCommand command, bool after)
        {
            var reader = asynchronous ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader();
            try
            {
                Assert.Equal(after ? 3 : 2, reader.FieldCount);
                Assert.True(asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 1, "Prepared binding must survive explicit command recreation.");
                Assert.Equal(after ? "after" : "before", reader.GetString(1));
                if (after) DatabaseContext.EqualNumber(reader.GetValue(2), 9, "The new schema must replace cached metadata.");
                Assert.False(asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync(deadline.Token) : reader.NextResult());
            }
            finally { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); }
        }
    }

    // Inspect identity to distinguish a real cache hit from another successful server prepare.
    private static object Prepared(OceanBaseCommand command)
    {
        var prepared = typeof(OceanBaseCommand).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(command);
        Assert.NotNull(prepared);
        return prepared;
    }
}
