using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class QuotedParameterRoutineTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.CreateBuilder().UseOraclePrepareExecute)
        from asynchronous in new[] { false, true }
        from kind in new[] { "procedure", "package", "function" }
        select new object[] { profile.Name, asynchronous, kind };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task QuotedParameterLabelsDeriveAndExecuteWithoutRenaming(string profileName, bool asynchronous, string kind)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("PROCEDURE", registerCleanup: false);
        var function = kind == "function";
        var signature = function ? "(\"input value\" IN NUMBER, \"result\" IN OUT NUMBER) RETURN NUMBER"
            : "(\"input value\" IN NUMBER, \"output value\" OUT NUMBER, \"result\" IN OUT NUMBER)";
        var body = " AS BEGIN \"result\" := \"result\" + \"input value\"; " +
            (function ? "RETURN \"input value\" + 1;" : "\"output value\" := \"input value\" + 1;") + " END;";
        string routine;
        if (kind == "package")
        {
            await db.ExecuteAsync(connection, "CREATE PACKAGE " + name + " AS PROCEDURE run" + signature + "; END;", CancellationToken.None);
            db.RegisterDropCleanup("PACKAGE", name);
            await db.ExecuteAsync(connection, "CREATE PACKAGE BODY " + name + " AS PROCEDURE run" + signature + body + " END;", CancellationToken.None);
            routine = name + ".run";
        }
        else
        {
            var keyword = function ? "FUNCTION" : "PROCEDURE";
            await db.ExecuteAsync(connection, "CREATE " + keyword + " " + name + signature + body, CancellationToken.None);
            db.RegisterDropCleanup(keyword, name); routine = name;
        }

        // The existing text PL path proves that the server accepts these quoted formal parameters.
        using (var control = db.Command(connection, function ? "BEGIN :out := " + routine + "(:input,:state); END;" : "BEGIN " + routine + "(:input,:out,:state); END;"))
        {
            DatabaseContext.Parameter(control, "input", 41m);
            var output = DatabaseContext.Parameter(control, "out", -1m); output.Direction = ParameterDirection.Output;
            var state = DatabaseContext.Parameter(control, "state", 10m); state.Direction = ParameterDirection.InputOutput;
            if (asynchronous) await control.ExecuteNonQueryAsync(); else control.ExecuteNonQuery();
            DatabaseContext.EqualNumber(output.Value, 42, "The server must accept the quoted formal names.");
            DatabaseContext.EqualNumber(state.Value, 51, "The control INOUT result must be correct.");
        }
        using var command = db.Command(connection, routine); command.CommandType = CommandType.StoredProcedure;
        var old = new OceanBaseParameter { ParameterName = "old", Value = 9 }; command.Parameters.Add(old);
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Null(old.Owner);
        Assert.Equal(function ? new[] { "RETURN_VALUE", "input value", "result" } : new[] { "input value", "output value", "result" },
            command.Parameters.Cast<OceanBaseParameter>().Select(p => p.ParameterName));
        var result = command.Parameters[function ? "RETURN_VALUE" : "output value"];
        Assert.NotSame(result, command.Parameters["result"]);
        for (var run = 0; run < 2; run++)
        {
            if (run == 1) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            command.Parameters["input value"].Value = 41m + run; command.Parameters["result"].Value = 10m + run; result.Value = -1m;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            DatabaseContext.EqualNumber(result.Value, 42 + run, "Quoted parameter output mismatch.");
            DatabaseContext.EqualNumber(command.Parameters["result"].Value, 51 + run * 2, "Quoted INOUT mismatch.");
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The caller connection must remain reusable.");
    }
}
