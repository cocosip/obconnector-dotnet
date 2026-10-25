using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class StandalonePrepareTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var shape in new[] { "block", "returning", "procedure", "function", "cursor" })
                    yield return new object[] { profile, asynchronous, shape };
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task PreparationDefersExecutionAndSupportsOutputs(string profileName, bool asynchronous, string shape)
        => await PreparationDefersExecutionAndSupportsOutputsCore(profileName, asynchronous, shape);

    public static IEnumerable<object[]> FetchHintProfiles()
    {
        foreach (var data in Profiles())
            foreach (var prepared in new[] { false, true })
                yield return new object[] { data[0], data[1], data[2], prepared };
    }

    [IntegrationTheory]
    [MemberData(nameof(FetchHintProfiles))]
    public async Task PositiveFetchHintSupportsImplicitAndPreparedOutputs(string profileName, bool asynchronous, string shape, bool prepared)
        => await PreparationDefersExecutionAndSupportsOutputsCore(profileName, asynchronous, shape, prepared, fetchSize: 2);

    private static async Task PreparationDefersExecutionAndSupportsOutputsCore(string profileName, bool asynchronous, string shape, bool prepared = true, int fetchSize = 0)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var sql = shape == "returning" ? "INSERT INTO " + table + " VALUES (:input) RETURNING id INTO :result"
            : shape == "cursor" ? "BEGIN OPEN :result FOR SELECT :input AS value FROM DUAL" + (fetchSize > 0 ? " UNION ALL SELECT 8 FROM DUAL UNION ALL SELECT 9 FROM DUAL ORDER BY value" : "") + "; END;"
            : "BEGIN INSERT INTO " + table + " VALUES (:input); :result := 42; END;";
        if (shape == "procedure")
        {
            var name = db.NewObject("PROCEDURE");
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + name + " (p_input IN NUMBER, p_output OUT NUMBER) AS BEGIN INSERT INTO " + table + " VALUES (p_input); p_output := 42; END;", CancellationToken.None);
            sql = name;
        }
        if (shape == "function")
        {
            var name = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
            db.RegisterCleanup("FUNCTION", name, async () =>
            {
                await using var cleanup = await db.OpenAsync();
                await db.ExecuteAsync(cleanup, "DROP FUNCTION " + name, CancellationToken.None);
            });
            await db.ExecuteAsync(connection, "CREATE FUNCTION " + name + " (p_input IN NUMBER) RETURN NUMBER AS BEGIN INSERT INTO " + table + " VALUES (p_input); RETURN 42; END;", CancellationToken.None);
            sql = name;
        }
        OceanBaseRefCursor? cursor = null;
        await using (var command = db.Command(connection, sql))
        {
            command.FetchSize = fetchSize;
            if (shape is "procedure" or "function") command.CommandType = CommandType.StoredProcedure;
            var input = DatabaseContext.Parameter(command, "input", "not encoded during Prepare", DbType.Int32);
            var output = DatabaseContext.Parameter(command, "result", -1, shape == "cursor" ? null : DbType.Int32);
            output.Direction = shape == "function" ? ParameterDirection.ReturnValue : ParameterDirection.Output;
            if (shape == "cursor") output.OceanBaseDbType = OceanBaseDbType.RefCursor;
            for (var i = 0; prepared && i < 2; i++)
            {
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(-1, output.Value);
                Assert.Equal(0L, await db.CountAsync(connection, table, CancellationToken.None));
            }
            input.Value = shape == "returning" ? 42 : 7;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Equal(fetchSize, command.FetchSize);
            if (shape == "cursor") cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
            else DatabaseContext.EqualNumber(output.Value, 42, "Prepared execution output mismatch.");
        }
        if (cursor != null)
        {
            using (cursor)
            await using (var reader = asynchronous ? await cursor.GetDataReaderAsync(1) : cursor.GetDataReader(1))
            {
                for (var row = 7; row <= (fetchSize > 0 ? 9 : 7); row++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), row, "REF CURSOR must retain its own page size after command disposal.");
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
        }
        Assert.Equal(shape == "cursor" ? 0L : 1L, await db.CountAsync(connection, table, CancellationToken.None));
    }
}
