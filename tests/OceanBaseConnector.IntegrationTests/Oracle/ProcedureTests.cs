using System;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ProcedureTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithPrepareExecute), MemberType = typeof(ProfileTheoryData))]
    public async Task ScalarOutAndInputOutputParameters(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, "BEGIN :result := :input + 1; :inout := 10; END;");
        DatabaseContext.Parameter(command, "input", 41, DbType.Int32);
        var output = DatabaseContext.Parameter(command, "result", DBNull.Value, DbType.Int32);
        output.Direction = ParameterDirection.Output;
        var inout = DatabaseContext.Parameter(command, "inout", 8, DbType.Int32);
        inout.Direction = ParameterDirection.InputOutput;
        await command.ExecuteNonQueryAsync();
        DatabaseContext.EqualNumber(output.Value, 42, "Scalar OUT mismatch.");
        DatabaseContext.EqualNumber(inout.Value, 10, "Scalar InputOutput mismatch.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task ReturningIntoRoundTrips(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id) RETURNING id INTO :result");
        DatabaseContext.Parameter(command, "id", 7, DbType.Int32);
        var output = DatabaseContext.Parameter(command, "result", DBNull.Value, DbType.Int32);
        output.Direction = ParameterDirection.Output;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        DatabaseContext.EqualNumber(output.Value, 7, "RETURNING value mismatch.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task StoredProcedureOutputAndSchemaDiscovery(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection,
            "CREATE PROCEDURE " + name + " (p_input IN NUMBER, p_output OUT NUMBER) AS BEGIN p_output := p_input + 1; END;",
            CancellationToken.None);
        await using var command = db.Command(connection, name);
        command.CommandType = CommandType.StoredProcedure;
        DatabaseContext.Parameter(command, "p_input", 6, DbType.Int32);
        var output = DatabaseContext.Parameter(command, "p_output", DBNull.Value, DbType.Int32);
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync();
        DatabaseContext.EqualNumber(output.Value, 7, "Stored procedure OUT mismatch.");
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        using var procedures = await connection.GetSchemaAsync("Procedures", new string?[] { null, owner, name }, CancellationToken.None);
        using var parameters = await connection.GetSchemaAsync("ProcedureParameters", new string?[] { null, owner, name }, CancellationToken.None);
        Assert.Equal(1, procedures.Rows.Count);
        Assert.Equal(2, parameters.Rows.Count);
    }
}
