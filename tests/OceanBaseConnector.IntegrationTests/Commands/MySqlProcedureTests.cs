using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Commands;

public sealed class MySqlProcedureTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task MySqlProcedureSync(string profileName) => Execute(profileName, asynchronous: false, earlyClose: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task MySqlProcedureAsync(string profileName) => Execute(profileName, asynchronous: true, earlyClose: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task MySqlProcedureCloseSync(string profileName) => Execute(profileName, asynchronous: false, earlyClose: true);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task MySqlProcedureCloseAsync(string profileName) => Execute(profileName, asynchronous: true, earlyClose: true);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task MySqlFunctionReturnValue(string profileName) => ExecuteFunction(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task MySqlFunctionReturnValueAsync(string profileName) => ExecuteFunction(profileName, asynchronous: true);

    public static IEnumerable<object[]> FetchHintProfiles()
    {
        foreach (var profile in ProfileTheoryData.MySqlWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var fetchSize in new[] { 1, int.MaxValue })
                        yield return new object[] { profile, asynchronous, prepared, fetchSize };
    }

    [IntegrationTheory]
    [MemberData(nameof(FetchHintProfiles))]
    public Task ProcedureFetchHintPreservesResultsAndOutputs(string profileName, bool asynchronous, bool prepared, int fetchSize)
        => Execute(profileName, asynchronous, earlyClose: false, fetchSize, prepared);

    [IntegrationTheory]
    [MemberData(nameof(FetchHintProfiles))]
    public Task ProcedureFetchHintPreservesOutputsOnEarlyClose(string profileName, bool asynchronous, bool prepared, int fetchSize)
        => Execute(profileName, asynchronous, earlyClose: true, fetchSize, prepared);

    [IntegrationTheory]
    [MemberData(nameof(FetchHintProfiles))]
    public Task FunctionFetchHintPreservesReturnValue(string profileName, bool asynchronous, bool prepared, int fetchSize)
        => ExecuteFunction(profileName, asynchronous, fetchSize, prepared);

    private static async Task Execute(string profileName, bool asynchronous, bool earlyClose, int fetchSize = 0, bool prepared = true)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var name = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        try
        {
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + name + "(IN p BIGINT, OUT o BIGINT, INOUT s VARCHAR(100)) " +
                "BEGIN SELECT p AS first_result; SET o=p+1; SET s=CONCAT(s, '-done'); SELECT p+2 AS second_result; END", CancellationToken.None);
        }
        catch (OceanBaseException error)
        { throw new InvalidOperationException($"Routine creation failed: code={error.ServerErrorCode}, state={error.SqlState}, unknown={error.IsExecutionOutcomeUnknown}.", error); }
        db.RegisterCleanup("PROCEDURE", name, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP PROCEDURE " + name, CancellationToken.None);
        });
        await using var command = db.Command(connection, name);
        command.CommandType = CommandType.StoredProcedure;
        command.FetchSize = fetchSize;
        DatabaseContext.Parameter(command, "ignored", 7L, DbType.Int64);
        var output = DatabaseContext.Parameter(command, "o", -1L, DbType.Int64); output.Direction = ParameterDirection.Output;
        var inout = DatabaseContext.Parameter(command, "s", "中文'input", DbType.String); inout.Direction = ParameterDirection.InputOutput;
        if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            output.Value = -1L; inout.Value = "中文'input";
            using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), 7, "First result mismatch.");
            DatabaseContext.EqualNumber(output.Value, -1, "Output published before completion.");
            if (!earlyClose)
            {
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 9, "Second result mismatch.");
                while (asynchronous ? await reader.NextResultAsync() : reader.NextResult())
                    while (asynchronous ? await reader.ReadAsync() : reader.Read()) { }
            }
            if (asynchronous) await reader.CloseAsync(); else reader.Close();
            DatabaseContext.EqualNumber(output.Value, 8, "Stored procedure OUT mismatch.");
            Assert.Equal("中文'input-done", inout.Value);
            Assert.Equal(fetchSize, command.FetchSize);
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1", CancellationToken.None), 1, "Connection is unusable after procedure completion.");
    }

    private static async Task ExecuteFunction(string profileName, bool asynchronous, int fetchSize = 0, bool prepared = false)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var name = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        await db.ExecuteAsync(connection, "CREATE FUNCTION " + name + "(p BIGINT) RETURNS BIGINT DETERMINISTIC RETURN p + 35", CancellationToken.None);
        db.RegisterCleanup("FUNCTION", name, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP FUNCTION " + name, CancellationToken.None);
        });
        await using var command = db.Command(connection, name);
        command.CommandType = CommandType.StoredProcedure;
        command.FetchSize = fetchSize;
        var returned = DatabaseContext.Parameter(command, "RETURN_VALUE", -1L, DbType.Int64);
        returned.Direction = ParameterDirection.ReturnValue;
        DatabaseContext.Parameter(command, "p", 7L, DbType.Int64);
        if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        DatabaseContext.EqualNumber(returned.Value, 42, "MySQL function ReturnValue mismatch.");
        Assert.Equal(fetchSize, command.FetchSize);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1", CancellationToken.None), 1, "Connection is unusable after function completion.");
    }
}
