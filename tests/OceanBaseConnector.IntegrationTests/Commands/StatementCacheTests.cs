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

    // Inspect identity to distinguish a real cache hit from another successful server prepare.
    private static object Prepared(OceanBaseCommand command)
    {
        var prepared = typeof(OceanBaseCommand).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(command);
        Assert.NotNull(prepared);
        return prepared;
    }
}
