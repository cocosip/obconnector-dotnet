using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ProcedureTests
{
    public static IEnumerable<object[]> PackageOverloadRows()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                yield return new object[] { profile, asynchronous };
    }

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

    [IntegrationTheory]
    [MemberData(nameof(PackageOverloadRows))]
    public async Task PackageOverloadsDeriveAndExecuteSelectedSignatures(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var package = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        await db.ExecuteAsync(connection,
            "CREATE PACKAGE " + package + " AS " +
            "PROCEDURE P(X IN NUMBER, Y OUT NUMBER); " +
            "PROCEDURE P(X IN DATE, Y OUT DATE); END;", CancellationToken.None);
        await db.ExecuteAsync(connection,
            "CREATE PACKAGE BODY " + package + " AS " +
            "PROCEDURE P(X IN NUMBER, Y OUT NUMBER) AS BEGIN Y := X + 1; END; " +
            "PROCEDURE P(X IN DATE, Y OUT DATE) AS BEGIN Y := X; END; END;", CancellationToken.None);
        db.RegisterCleanup("PACKAGE", package, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP PACKAGE " + package, CancellationToken.None);
        });

        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        using var directory = await connection.GetSchemaAsync("Procedures", new string?[] { null, owner, "P", null, package }, CancellationToken.None);
        var overloads = directory.Rows.Cast<DataRow>().Select(row => Assert.IsType<string>(row["OVERLOAD"])).OrderBy(value => value).ToArray();
        Assert.Equal(2, overloads.Length); Assert.NotEqual(overloads[0], overloads[1]);
        foreach (var overload in overloads)
        {
            await using var command = db.Command(connection, package + ".P"); command.CommandType = CommandType.StoredProcedure;
            if (asynchronous) await command.DeriveParametersAsync(overload); else command.DeriveParameters(overload);
            Assert.Equal(2, command.Parameters.Count);
            var input = (OceanBaseParameter)command.Parameters[0]; var output = (OceanBaseParameter)command.Parameters[1];
            Assert.Equal(ParameterDirection.Input, input.Direction); Assert.Equal(ParameterDirection.Output, output.Direction);
            if (input.OceanBaseDbType == OceanBaseDbType.Decimal)
            {
                input.Value = 41m; output.Value = DBNull.Value;
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                DatabaseContext.EqualNumber(output.Value, 42, "NUMBER package overload output mismatch.");
            }
            else
            {
                Assert.Equal(OceanBaseDbType.OracleDate, input.OceanBaseDbType);
                var date = new DateTime(2026, 11, 1);
                input.Value = date; output.Value = DBNull.Value;
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Equal(date, Assert.IsType<DateTime>(output.Value));
            }
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(PackageOverloadRows))]
    public async Task QuotedPackageIdentifierDerivesAndExecutes(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = "ObiPkg_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        await db.ExecuteAsync(connection,
            "CREATE PACKAGE \"" + name + "\" AS PROCEDURE \"DoIt\" (P_INPUT IN NUMBER, P_OUTPUT OUT NUMBER); END;",
            CancellationToken.None);
        db.RegisterCleanup("PACKAGE", name, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP PACKAGE \"" + name + "\"", CancellationToken.None);
        });
        await db.ExecuteAsync(connection,
            "CREATE PACKAGE BODY \"" + name + "\" AS PROCEDURE \"DoIt\" (P_INPUT IN NUMBER, P_OUTPUT OUT NUMBER) AS BEGIN P_OUTPUT := P_INPUT + 1; END; END;",
            CancellationToken.None);

        using var directory = await connection.GetSchemaAsync("Procedures", new string?[] { null, null, "DoIt", null, name });
        Assert.Single(directory.Rows.Cast<DataRow>());
        await using var command = db.Command(connection, "\"" + name + "\".\"DoIt\"");
        command.CommandType = CommandType.StoredProcedure;
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Equal(2, command.Parameters.Count);
        command.Parameters[0].Value = 41m;
        command.Parameters[1].Value = DBNull.Value;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        DatabaseContext.EqualNumber(command.Parameters[1].Value, 42, "Quoted procedure output mismatch.");
    }
}
