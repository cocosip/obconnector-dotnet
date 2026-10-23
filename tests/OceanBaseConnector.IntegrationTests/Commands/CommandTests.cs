using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Commands;

public sealed class CommandTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task CrudTextAndPreparedCommandsSync(string profileName) => Crud(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task CrudTextAndPreparedCommandsAsync(string profileName) => Crud(profileName, asynchronous: true);

    private static async Task Crud(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "VARCHAR2(100)" : "VARCHAR(100)"), CancellationToken.None);
        Assert.Equal(1, await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1, 'text')", CancellationToken.None));
        await using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (" + db.ParameterSql(":id, :value") + ")");
        var id = DatabaseContext.Parameter(command, "id", 2, DbType.Int32);
        var value = DatabaseContext.Parameter(command, "value", "中文'参数", DbType.String);
        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        id.Value = 3;
        value.Value = DBNull.Value;
        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal(3, await db.CountAsync(connection, table, CancellationToken.None));
        await using (var select = db.Command(connection, "SELECT id, value FROM " + table + " ORDER BY id"))
        await using (var reader = asynchronous ? await select.ExecuteReaderAsync() : select.ExecuteReader())
        {
            Assert.Equal(2, reader.FieldCount);
            var rows = 0;
            while (asynchronous ? await reader.ReadAsync() : reader.Read())
            {
                rows++;
                DatabaseContext.EqualNumber(reader.GetValue(0), rows, "Ordered row identity must match.");
                if (rows == 2) Assert.Equal("中文'参数", reader.GetString(1));
                if (rows == 3) Assert.True(reader.IsDBNull(1), "SQL NULL must round-trip.");
            }
            Assert.Equal(3, rows);
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Equal(1, await db.ExecuteAsync(connection, "UPDATE " + table + " SET value='changed' WHERE id=2", CancellationToken.None));
        Assert.Equal(1, await db.ExecuteAsync(connection, "DELETE FROM " + table + " WHERE id=3", CancellationToken.None));
        Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task UnpreparedParameterizedTextUsesClientPrepare(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.False(db.Oracle, "Client-side interpolation is the MySQL text path.");
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1),(2),(3)", CancellationToken.None);
        await using var command = db.Command(connection, "SELECT COUNT(*) FROM " + table + " WHERE id >= ?");
        command.Parameters.Add(new OceanBaseParameter { DbType = DbType.Int32, Value = 2 });
        DatabaseContext.EqualNumber(await command.ExecuteScalarAsync(), 2,
            "An unprepared parameterized text command must interpolate parameters client-side.");
        command.Parameters[0].Value = 4;
        DatabaseContext.EqualNumber(await command.ExecuteScalarAsync(), 0,
            "The same command must honor changed parameter values.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task UnsupportedCommandTypeIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, db.SelectOne);
        command.CommandType = CommandType.TableDirect;
        Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySql), MemberType = typeof(ProfileTheoryData))]
    public async Task StoredProcedureRequiresOraclePrepareExecute(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, "nonexistent_procedure");
        command.CommandType = CommandType.StoredProcedure;
        Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task UnsupportedCommandBehaviorIsRejected(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, db.SelectOne);
        Assert.Throws<NotSupportedException>(() => command.ExecuteReader(CommandBehavior.SchemaOnly));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task CancelWithoutExecutionIsHarmless(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, db.SelectOne);
        command.Cancel();
        DatabaseContext.EqualNumber(await command.ExecuteScalarAsync(), 1,
            "A cancelled idle command must remain executable.");
    }
}
