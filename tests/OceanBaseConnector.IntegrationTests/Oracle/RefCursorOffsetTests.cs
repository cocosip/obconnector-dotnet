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
    public Task ForwardOffsetPagesDrainBeforeConnectionReuse(string profileName, bool asynchronous, bool empty, CommandBehavior behavior)
        => VerifyPages(profileName, asynchronous, empty, behavior, parentOffset: false, disable: false);

    public static IEnumerable<object[]> ParentProfiles()
    {
        foreach (var row in Profiles())
            foreach (var disable in new[] { false, true }) yield return new object[] { row[0], row[1], row[2], row[3], disable };
    }

    [IntegrationTheory]
    [MemberData(nameof(ParentProfiles))]
    public Task ParentOffsetDefaultsCanBeOverriddenAfterExplicitPreparation(string profileName, bool asynchronous, bool empty, CommandBehavior behavior, bool disable)
        => VerifyPages(profileName, asynchronous, empty, behavior, parentOffset: true, disable);

    public static IEnumerable<object[]> FunctionProfiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true }) yield return new object[] { profile, asynchronous };
    }

    [IntegrationTheory]
    [MemberData(nameof(FunctionProfiles))]
    public Task FunctionReturnInheritsParentOffset(string profileName, bool asynchronous)
        => VerifyPages(profileName, asynchronous, false, CommandBehavior.SequentialAccess, parentOffset: true, disable: false, function: true);

    private static async Task VerifyPages(string profileName, bool asynchronous, bool empty, CommandBehavior behavior, bool parentOffset, bool disable, bool function = false)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var query = empty ? "SELECT 11 AS n FROM DUAL WHERE 1=0"
            : "SELECT 11 AS n FROM DUAL UNION ALL SELECT 22 FROM DUAL UNION ALL SELECT 33 FROM DUAL ORDER BY 1";
        var sql = "BEGIN OPEN :c FOR " + query + "; END;";
        if (function)
        {
            sql = db.NewObject("PROCEDURE", registerCleanup: false); db.RegisterDropCleanup("FUNCTION", sql);
            await db.ExecuteAsync(connection, "CREATE FUNCTION " + sql + " RETURN SYS_REFCURSOR IS c SYS_REFCURSOR; BEGIN OPEN c FOR " + query + "; RETURN c; END;", CancellationToken.None);
        }
        OceanBaseRefCursor cursor;
        using (var command = db.Command(connection, sql))
        {
            if (function) command.CommandType = CommandType.StoredProcedure;
            command.UseCursorOffset = parentOffset; command.FetchSize = parentOffset ? 1 : 0;
            var output = DatabaseContext.Parameter(command, "c", DBNull.Value);
            output.OceanBaseDbType = OceanBaseDbType.RefCursor; output.Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.Output;
            if (parentOffset) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
            command.UseCursorOffset = false;
        }
        using (cursor)
        {
            await using (var reader = parentOffset && !disable
                ? asynchronous ? await cursor.GetDataReaderAsync(2, behavior) : cursor.GetDataReader(2, behavior)
                : asynchronous ? await cursor.GetDataReaderAsync(2, !disable, behavior, CancellationToken.None) : cursor.GetDataReader(2, !disable, behavior))
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
