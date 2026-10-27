using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddRoutines(List<AcceptanceCase> cases)
    {
        foreach (var asynchronous in new[] { false, true })
        {
            var suffix = asynchronous ? "async" : "sync";
            cases.Add(new("mysql.function-return-value." + suffix, "routines",
                (c, token) => MySqlFunctionReturnValue(c, asynchronous, token),
                Mode: "MySql", RequiresObjects: true));
        }
    }

    private static async Task MySqlFunctionReturnValue(CaseContext c, bool asynchronous, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var name = "OBA_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        await c.ExecuteAsync(connection,
            "CREATE FUNCTION " + name + "(p BIGINT) RETURNS BIGINT DETERMINISTIC RETURN p + 35", token);
        c.RegisterCleanup("FUNCTION", name, async () =>
        {
            await using var cleanup = await c.OpenAsync(CancellationToken.None);
            await c.ExecuteAsync(cleanup, "DROP FUNCTION " + name, CancellationToken.None);
        });

        await using var command = c.Command(connection, name);
        command.CommandType = CommandType.StoredProcedure;
        var returned = CaseContext.Parameter(command, "RETURN_VALUE", -1L, DbType.Int64);
        returned.Direction = ParameterDirection.ReturnValue;
        CaseContext.Parameter(command, "p", 7L, DbType.Int64);
        if (asynchronous) await command.ExecuteNonQueryAsync(token); else command.ExecuteNonQuery();
        c.EqualNumber(returned.Value, 42, "MySQL function ReturnValue mismatch.");
    }
}
