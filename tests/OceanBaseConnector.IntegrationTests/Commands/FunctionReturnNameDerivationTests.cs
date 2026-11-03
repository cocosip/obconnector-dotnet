using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Commands;

public sealed class FunctionReturnNameDerivationTests
{
    public static TheoryData<string, bool, bool> Rows()
    {
        var rows = new TheoryData<string, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects))
            foreach (var asynchronous in new[] { false, true })
                foreach (var chain in new[] { false, true }) rows.Add(profile.Name, asynchronous, chain);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task FunctionReturnLabelAvoidsRealFormalsAndExecutesAfterDerivation(string profileName, bool asynchronous, bool chain)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        if (db.Oracle) builder.UseOraclePrepareExecute = true;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var name = db.NewObject("PROCEDURE", registerCleanup: false);
        var names = chain ? new[] { "RETURN_VALUE", "return_value_1", "Return_Value_3" } : new[] { "RETURN_VALUE" };
        string Quote(string value) => db.Oracle ? "\"" + value + "\"" : "`" + value + "`";
        var signature = string.Join(",", names.Select(p => Quote(p) + (db.Oracle ? " IN NUMBER" : " BIGINT")));
        var expression = string.Join(" + ", names.Select((p, i) => Quote(p) + " * " + (i == 0 ? 1 : i == 1 ? 10 : 100).ToString(CultureInfo.InvariantCulture)));
        await db.ExecuteAsync(connection, "CREATE FUNCTION " + name + "(" + signature + ")" +
            (db.Oracle ? " RETURN NUMBER AS BEGIN RETURN " + expression + "; END;" : " RETURNS BIGINT DETERMINISTIC RETURN " + expression), CancellationToken.None);
        db.RegisterDropCleanup("FUNCTION", name);

        // An ordinary positional SQL call establishes that the real formal labels work on the server.
        using (var control = db.Command(connection, db.ScalarSql(name + "(" + string.Join(",", names.Select((_, i) => db.Oracle ? ":p" + i : "?")) + ")")))
        {
            for (var i = 0; i < names.Length; i++) DatabaseContext.Parameter(control, "p" + i, (decimal)(i + 1), DbType.Decimal);
            DatabaseContext.EqualNumber(asynchronous ? await control.ExecuteScalarAsync() : control.ExecuteScalar(), chain ? 321 : 1, "The direct function control must succeed.");
        }

        using var command = db.Command(connection, name); command.CommandType = CommandType.StoredProcedure;
        var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Null(original.Owner);
        var expectedReturnName = chain ? "RETURN_VALUE_2" : "RETURN_VALUE_1";
        Assert.Equal(new[] { expectedReturnName }.Concat(names), command.Parameters.Cast<OceanBaseParameter>().Select(p => p.ParameterName));
        var returned = (OceanBaseParameter)command.Parameters[expectedReturnName];
        Assert.Equal(ParameterDirection.ReturnValue, returned.Direction);
        Assert.Same(returned, command.Parameters[expectedReturnName.ToLowerInvariant()]);
        for (var run = 0; run < 2; run++)
        {
            if (run == 1) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            for (var i = 0; i < names.Length; i++)
            {
                Assert.Same(command.Parameters[i + 1], command.Parameters[names[i]]);
                Assert.Equal(ParameterDirection.Input, command.Parameters[i + 1].Direction);
                command.Parameters[i + 1].Value = db.Oracle ? (object)(decimal)(i + 1 + run) : (long)(i + 1 + run);
            }
            returned.Value = db.Oracle ? (object)(-1m) : -1L;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            DatabaseContext.EqualNumber(returned.Value, chain ? 321 + run * 111 : 1 + run, "The derived function return must preserve argument order.");
            for (var i = 0; i < names.Length; i++) DatabaseContext.EqualNumber(command.Parameters[i + 1].Value, i + 1 + run, "An input parameter must not receive the function return.");
        }
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Null(returned.Owner);
        Assert.Equal(expectedReturnName, command.Parameters[0].ParameterName);
        Assert.All(command.Parameters.Cast<OceanBaseParameter>(), p => Assert.Equal(DBNull.Value, p.Value));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The function call must leave the connection reusable.");
    }
}
