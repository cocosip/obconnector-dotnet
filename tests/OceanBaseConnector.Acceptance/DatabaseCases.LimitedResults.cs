using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static async Task BatchLimitedResults(CaseContext c, bool asynchronous, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var firstSql = c.Oracle
            ? "SELECT 1 AS value FROM DUAL UNION ALL SELECT 2 AS value FROM DUAL"
            : "SELECT 1 AS value UNION ALL SELECT 2 AS value";

        foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult })
        {
            await using var batch = connection.CreateBatch();
            var first = batch.CreateBatchCommand(); first.CommandText = firstSql; batch.BatchCommands.Add(first);
            var second = batch.CreateBatchCommand(); second.CommandText = c.SelectOne; batch.BatchCommands.Add(second);
            if (asynchronous)
            {
                await using var reader = await batch.ExecuteReaderAsync(behavior, token);
                await AssertLimitedResults(c, reader, behavior, asynchronous, token);
            }
            else
            {
                using var reader = batch.ExecuteReader(behavior);
                await AssertLimitedResults(c, reader, behavior, asynchronous, token);
            }
        }

        c.EqualNumber(await c.ScalarAsync(connection, c.SelectOne, token), 1,
            "Connection must remain usable after limited batch results.");
    }

    private static async Task AssertLimitedResults(CaseContext c, DbDataReader reader,
        CommandBehavior behavior, bool asynchronous, CancellationToken token)
    {
        c.Check(asynchronous ? await reader.ReadAsync(token) : reader.Read(), "Batch must expose its first row.");
        c.EqualNumber(reader.GetValue(0), 1, "Batch first row mismatch.");
        if (behavior == CommandBehavior.SingleResult)
        {
            c.Check(asynchronous ? await reader.ReadAsync(token) : reader.Read(),
                "SingleResult must retain later rows in the first result.");
            c.EqualNumber(reader.GetValue(0), 2, "Batch second row mismatch.");
        }
        c.Check(!(asynchronous ? await reader.ReadAsync(token) : reader.Read()),
            "Limited batch result must stop after the exposed rows.");
        c.Check(!(asynchronous ? await reader.NextResultAsync(token) : reader.NextResult()),
            "Limited batch result must not expose later batch entries.");
    }
}
