using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class IntervalArrayBindingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var kind in new[] { "insert", "returning" })
                        yield return new object[] { profile, asynchronous, prepared, kind };
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task IntervalColumnsKeepExactValuesNullsAndCallerTransaction(string profileName, bool asynchronous, bool prepared, string kind)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,ym INTERVAL YEAR(9) TO MONTH,ds INTERVAL DAY(9) TO SECOND(9)", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var number = connection.CreateArrayType("NUMBER", transaction);
        var ym = connection.CreateArrayType("INTERVAL YEAR TO MONTH", transaction);
        var ds = connection.CreateArrayType("INTERVAL DAY TO SECOND", transaction);
        var years = new object?[] { new OceanBaseIntervalYearToMonth(true, 123456789, 11), null, new OceanBaseIntervalYearToMonth(false, 0, 0) };
        var days = new object?[] { new OceanBaseIntervalDayToSecond(false, 987654321, 23, 59, 58, 123456789), null, new OceanBaseIntervalDayToSecond(true, 0, 0, 0, 0, 1) };
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES(:i,:y,:d)" +
            (kind == "returning" ? " RETURNING ym,ds INTO :oy,:od" : ""), transaction);
        var ids = DatabaseContext.Parameter(command, "i", number.CreateValue(1, 2, 3));
        DatabaseContext.Parameter(command, "y", ym.CreateValue(years));
        DatabaseContext.Parameter(command, "d", ds.CreateValue(days));
        OceanBaseParameter? outYears = null, outDays = null;
        if (kind != "insert")
        {
            outYears = DatabaseContext.Parameter(command, "oy", "old years"); outYears.ComplexType = ym; outYears.Direction = ParameterDirection.Output;
            outDays = DatabaseContext.Parameter(command, "od", "old days"); outDays.ComplexType = ds; outDays.Direction = ParameterDirection.Output;
        }
        if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        if (outYears != null) { Assert.Equal("old years", outYears.Value); Assert.Equal("old days", outDays!.Value); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ids.Value = number.CreateValue(attempt * 3 + 1, attempt * 3 + 2, attempt * 3 + 3);
            var result = asynchronous ? await command.ExecuteArrayAsync(3) : command.ExecuteArray(3);
            Assert.True(result.IsComplete); Assert.Equal(3, result.RecordsAffected); Assert.Same(result, command.ArrayBindingResult);
            Assert.Equal(3, result.Items.Count);
            for (var row = 0; row < 3; row++)
            {
                Assert.Equal(OceanBaseArrayBindingStatus.Succeeded, result.Items[row].Status); Assert.Equal(0, result.Items[row].ServerErrorCode);
                if (kind == "insert") { Assert.Equal(1, result.Items[row].RecordsAffected); Assert.Empty(result.Items[row].OutputRows); }
                else
                {
                    var output = Assert.Single(result.Items[row].OutputRows);
                    Assert.Equal(years[row] ?? DBNull.Value, output[0]); Assert.Equal(days[row] ?? DBNull.Value, output[1]);
                }
            }
            if (outYears != null)
            {
                var returnedYears = Assert.IsType<object[][]>(outYears.Value); var returnedDays = Assert.IsType<object[][]>(outDays!.Value);
                Assert.Equal(3, returnedYears.Length); Assert.Equal(3, returnedDays.Length);
                for (var row = 0; row < 3; row++)
                {
                    Assert.Equal(years[row] ?? DBNull.Value, Assert.Single(returnedYears[row]));
                    Assert.Equal(days[row] ?? DBNull.Value, Assert.Single(returnedDays[row]));
                }
            }
            Assert.Same(connection, transaction.Connection);
            Assert.Equal((attempt + 1) * 3, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        }
        command.Dispose();
        if (asynchronous) await transaction.CommitAsync(); else transaction.Commit();
        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT id,ym,ds FROM " + table + " ORDER BY id");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        for (var row = 0; row < 6; row++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(row + 1, DatabaseContext.IntegerResult(reader.GetValue(0)));
            Assert.Equal(years[row % 3] ?? DBNull.Value, reader.GetValue(1)); Assert.Equal(days[row % 3] ?? DBNull.Value, reader.GetValue(2));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    public static IEnumerable<object[]> ProcedureCases()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true }) yield return new object[] { profile, asynchronous, prepared };
    }

    [IntegrationTheory]
    [MemberData(nameof(ProcedureCases))]
    public async Task ProcedureIntervalOutputsWithUnknownPrecisionRemainRejected(string profileName, bool asynchronous, bool prepared)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "ym INTERVAL YEAR(9) TO MONTH,ds INTERVAL DAY(9) TO SECOND(9)", CancellationToken.None);
        var routine = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + routine +
            "(y IN INTERVAL YEAR TO MONTH,d IN INTERVAL DAY TO SECOND,oy OUT INTERVAL YEAR TO MONTH,od OUT INTERVAL DAY TO SECOND) IS BEGIN " +
            "INSERT INTO " + table + " VALUES(y,d); oy:=y; od:=d; END;", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        using (var scalar = db.Command(connection, routine, transaction))
        {
            scalar.CommandType = CommandType.StoredProcedure;
            DatabaseContext.Parameter(scalar, "y", new OceanBaseIntervalYearToMonth(true, 123456789, 11));
            DatabaseContext.Parameter(scalar, "d", new OceanBaseIntervalDayToSecond(false, 987654321, 23, 59, 58, 123456789));
            var sy = DatabaseContext.Parameter(scalar, "oy", "old years"); sy.OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth; sy.Direction = ParameterDirection.Output;
            var sd = DatabaseContext.Parameter(scalar, "od", "old days"); sd.OceanBaseDbType = OceanBaseDbType.IntervalDayToSecond; sd.Direction = ParameterDirection.Output;
            var precisionError = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => scalar.ExecuteNonQueryAsync())
                : Assert.Throws<OceanBaseException>(() => scalar.ExecuteNonQuery());
            Assert.Equal(1873, precisionError.ServerErrorCode); Assert.False(precisionError.IsExecutionOutcomeUnknown);
            Assert.Equal("old years", sy.Value); Assert.Equal("old days", sd.Value); Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        }
        var ym = connection.CreateArrayType("INTERVAL YEAR TO MONTH", transaction);
        var ds = connection.CreateArrayType("INTERVAL DAY TO SECOND", transaction);
        using var command = db.Command(connection, routine, transaction); command.CommandType = CommandType.StoredProcedure;
        DatabaseContext.Parameter(command, "y", ym.CreateValue(new OceanBaseIntervalYearToMonth(true, 12, 11, 2), null));
        DatabaseContext.Parameter(command, "d", ds.CreateValue(new OceanBaseIntervalDayToSecond(false, 98, 23, 59, 58, 123456000, 2, 6), null));
        var oy = DatabaseContext.Parameter(command, "oy", "old years"); oy.ComplexType = ym; oy.Direction = ParameterDirection.Output;
        var od = DatabaseContext.Parameter(command, "od", "old days"); od.ComplexType = ds; od.Direction = ParameterDirection.Output;
        if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
        // Current 4.4.2.2 PL outputs report scale -1 and embed invalid precision 255 in the interval payload.
        var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteArrayAsync(2))
            : Assert.Throws<OceanBaseException>(() => command.ExecuteArray(2));
        Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
        Assert.Contains("INTERVAL metadata", Assert.IsType<NotSupportedException>(error.InnerException).Message, StringComparison.Ordinal);
        Assert.Equal("old years", oy.Value); Assert.Equal("old days", od.Value);
        command.Dispose(); connection.Close();
        await using var observer = await db.OpenAsync();
        Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
    }
}
