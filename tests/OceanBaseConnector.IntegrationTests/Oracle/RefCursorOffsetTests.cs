using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class RefCursorOffsetTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var empty in new[] { false, true })
                    foreach (var behavior in new[] { CommandBehavior.Default, CommandBehavior.SequentialAccess, CommandBehavior.SingleRow | CommandBehavior.SequentialAccess })
                        yield return new object[] { profile, asynchronous, empty, behavior };
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task ForwardOffsetPagesDrainBeforeConnectionReuse(string profileName, bool asynchronous, bool empty, CommandBehavior behavior)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        OceanBaseRefCursor cursor;
        using (var command = db.Command(connection, empty
            ? "BEGIN OPEN :c FOR SELECT 11 AS n FROM DUAL WHERE 1=0; END;"
            : "BEGIN OPEN :c FOR SELECT 11 AS n FROM DUAL UNION ALL SELECT 22 FROM DUAL UNION ALL SELECT 33 FROM DUAL ORDER BY 1; END;"))
        {
            var output = DatabaseContext.Parameter(command, "c", DBNull.Value);
            output.OceanBaseDbType = OceanBaseDbType.RefCursor; output.Direction = ParameterDirection.Output;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
        }
        using (cursor)
        {
            await using (var reader = asynchronous ? await cursor.GetDataReaderAsync(2, true, behavior, CancellationToken.None) : cursor.GetDataReader(2, true, behavior))
            {
                Assert.Equal(!empty, reader.HasRows);
                var count = empty ? 0 : (behavior & CommandBehavior.SingleRow) != 0 ? 1 : 3;
                for (var row = 1; row <= count; row++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), row * 11, "REF CURSOR offset row mismatch.");
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader(2, true, CommandBehavior.Default));
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Offset cursor must release its exchange and consume every tail.");
    }
}
