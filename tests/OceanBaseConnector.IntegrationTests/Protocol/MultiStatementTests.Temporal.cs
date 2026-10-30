using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> MySqlTemporalScriptRows() =>
        from row in MySqlRows()
        from sequential in new[] { false, true }
        from scale in new byte[] { 0, 3, 6 }
        from providerValues in new[] { false, true }
        select row.Concat(new object[] { sequential, scale, providerValues }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(MySqlTemporalScriptRows))]
    public async Task MySqlTemporalScriptsPreserveIndependentStorageAcrossSessionChanges(
        string profileName, bool asynchronous, bool sequential, byte scale, bool providerValues)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,d DATE,dt DATETIME(6),ts TIMESTAMP(6) NULL,t TIME(6)", CancellationToken.None);
        var fraction = scale == 0 ? 0 : scale == 3 ? 123000 : 123456;
        var wall = new DateTime(2026, 10, 30, 12, 34, 56).AddTicks(fraction * 10L);
        var duration = new TimeSpan(2, 1, 2, 3).Add(TimeSpan.FromTicks(fraction * 10L));
        if (providerValues) duration = -duration;
        var zero = new OceanBaseDate(0, 0, 0);
        using (var command = db.Command(connection,
            "SET sql_mode='STRICT_ALL_TABLES'; SET time_zone='+05:45'; INSERT INTO " + table + " VALUES (1,?,?,?,?); " +
            "SET sql_mode='STRICT_ALL_TABLES,NO_BACKSLASH_ESCAPES'; SET time_zone='-03:30'; INSERT INTO " + table + " VALUES (2,?,?,?,?); " +
            "INSERT INTO " + table + " VALUES (3,?,?,?,?); SET sql_mode=''; INSERT INTO " + table + " VALUES (4,?,?,?,?)"))
        {
            for (var row = 1; row <= 4; row++)
            {
                object date = row == 3 ? DBNull.Value : row == 4 ? zero : providerValues ? new OceanBaseDate(wall.Date) : wall.Date;
                object datetime = row == 3 ? DBNull.Value : row == 4 ? new OceanBaseDateTime(zero) : providerValues ? new OceanBaseDateTime(wall) : wall;
                var d = DatabaseContext.Parameter(command, "", date); d.OceanBaseDbType = OceanBaseDbType.Date;
                var dt = DatabaseContext.Parameter(command, "", datetime); dt.OceanBaseDbType = OceanBaseDbType.DateTime; dt.Scale = scale;
                var ts = DatabaseContext.Parameter(command, "", row >= 3 ? DBNull.Value : wall, DbType.DateTime2); ts.Scale = scale;
                var t = DatabaseContext.Parameter(command, "", row == 3 ? DBNull.Value : row == 4 ? TimeSpan.Zero : duration); t.OceanBaseDbType = OceanBaseDbType.Time; t.Scale = scale;
            }
            Assert.Equal(4, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(providerValues ? new OceanBaseDate(wall.Date) : wall.Date, command.Parameters[0].Value);
            Assert.Equal(duration, command.Parameters[3].Value);
        }
        Assert.Equal("-03:30", await db.ScalarAsync(connection, "SELECT @@time_zone", CancellationToken.None));
        await using var verification = await db.OpenAsync();
        await db.ExecuteAsync(verification, "SET time_zone='+00:00'", CancellationToken.None);
        foreach (var prepared in new[] { false, true })
        {
            using var query = db.Command(verification, "SELECT d,dt,ts,t FROM " + table + " ORDER BY id");
            if (prepared) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
            using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
            Assert.Equal("DATE", reader.GetDataTypeName(0)); Assert.Equal("DATETIME", reader.GetDataTypeName(1));
            Assert.Equal("TIMESTAMP", reader.GetDataTypeName(2)); Assert.Equal("TIME", reader.GetDataTypeName(3));
            for (var row = 1; row <= 4; row++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (row == 3) { for (var column = 0; column < 4; column++) Assert.True(reader.IsDBNull(column)); continue; }
                Assert.Equal(row == 4 ? zero : new OceanBaseDate(wall.Date), reader.GetFieldValue<OceanBaseDate>(0));
                if (row == 4)
                {
                    Assert.Equal(new OceanBaseDateTime(zero), reader.GetFieldValue<OceanBaseDateTime>(1));
                    Assert.True(reader.IsDBNull(2)); Assert.Equal(TimeSpan.Zero, reader.GetFieldValue<TimeSpan>(3));
                }
                else
                {
                    Assert.Equal(wall, reader.GetDateTime(1));
                    Assert.Equal(row == 1 ? wall.AddMinutes(-345) : wall.AddMinutes(210), reader.GetDateTime(2));
                    Assert.Equal(duration, reader.GetFieldValue<TimeSpan>(3));
                }
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Equal(4, await db.CountAsync(verification, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Temporal script writer remains reusable.");
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlTemporalScriptsRejectInvalidInputsBeforeAnyPrefixSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,dt DATETIME(6),t TIME(6)", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,NULL,NULL); INSERT INTO " + table + " VALUES (2,?,?)");
        var datetime = DatabaseContext.Parameter(command, "", new DateTime(2026, 10, 30), DbType.DateTime2);
        var time = DatabaseContext.Parameter(command, "", DBNull.Value, DbType.Time); time.Scale = 7;
        Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
        await Assert.ThrowsAsync<NotSupportedException>(execute);
        time.Scale = 6; time.Value = TimeSpan.FromTicks(1);
        await Assert.ThrowsAsync<ArgumentException>(execute);
        time.Value = TimeSpan.Zero; datetime.Value = DateTime.SpecifyKind(new DateTime(2026, 10, 30), DateTimeKind.Utc);
        await Assert.ThrowsAsync<ArgumentException>(execute);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }
}
