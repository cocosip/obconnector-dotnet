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

public sealed class UnicodeRoutineTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.CreateBuilder().UseOraclePrepareExecute)
        from asynchronous in new[] { false, true }
        from packaged in new[] { false, true }
        select new object[] { profile.Name, asynchronous, packaged };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task UnquotedUnicodeRoutineDerivesAndExecutes(string profileName, bool asynchronous, bool packaged)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = "诊断" + db.NewObject("PROCEDURE", registerCleanup: false);
        const string signature = "(P_INPUT IN NUMBER, P_OUTPUT OUT NUMBER)";
        const string body = " AS BEGIN P_OUTPUT := P_INPUT + 1; END;";
        if (packaged)
        {
            await db.ExecuteAsync(connection, "CREATE PACKAGE " + name + " AS PROCEDURE 查询" + signature + "; END;", CancellationToken.None);
            db.RegisterCleanup("PACKAGE", name, async () =>
            {
                await using var cleanup = await db.OpenAsync();
                await db.ExecuteAsync(cleanup, "DROP PACKAGE " + name, CancellationToken.None);
            });
            await db.ExecuteAsync(connection, "CREATE PACKAGE BODY " + name + " AS PROCEDURE 查询" + signature + body + " END;", CancellationToken.None);
        }
        else
        {
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + name + signature + body, CancellationToken.None);
            db.RegisterDropCleanup("PROCEDURE", name);
        }
        using var directory = await connection.GetSchemaAsync("Procedures", new string?[] { null, null, packaged ? "查询" : name, null, packaged ? name : null });
        Assert.Single(directory.Rows.Cast<DataRow>());
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        await using var command = db.Command(connection, "\"" + owner.Replace("\"", "\"\"") + "\"." + name + (packaged ? ".查询" : ""));
        command.CommandType = CommandType.StoredProcedure;
        var old = new OceanBaseParameter { ParameterName = "old", Value = 9 }; command.Parameters.Add(old);
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Null(old.Owner); Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(new[] { "P_INPUT", "P_OUTPUT" }, command.Parameters.Cast<OceanBaseParameter>().Select(p => p.ParameterName));
        Assert.Equal(ParameterDirection.Input, command.Parameters[0].Direction); Assert.Equal(ParameterDirection.Output, command.Parameters[1].Direction);
        for (var i = 0; i < 2; i++)
        {
            if (i == 1) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            command.Parameters[0].Value = 41m + i; command.Parameters[1].Value = DBNull.Value;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            DatabaseContext.EqualNumber(command.Parameters[1].Value, 42 + i, "Unquoted Unicode procedure output mismatch.");
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Connection must remain reusable.");
    }
}
