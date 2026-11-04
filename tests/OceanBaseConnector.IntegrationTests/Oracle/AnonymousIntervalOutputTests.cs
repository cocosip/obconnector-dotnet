using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class AnonymousIntervalOutputTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    yield return new object[] { profile, asynchronous, prepared };
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task LocalCollectionsKeepExactIntervalsNullsAndEmptyOutputs(string profileName, bool asynchronous, bool prepared)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Exception? failure = null;
        try
        {
            await using var connection = await db.OpenAsync();
            var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,ym INTERVAL YEAR(9) TO MONTH,ds INTERVAL DAY(9) TO SECOND(9),n INTEGER", CancellationToken.None);
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(1,INTERVAL '-123456789-11' YEAR(9) TO MONTH,INTERVAL '987654321 23:59:58.123456789' DAY(9) TO SECOND(9),0)", CancellationToken.None);
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(2,NULL,NULL,0)", CancellationToken.None);
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(3,INTERVAL '0-0' YEAR(9) TO MONTH,INTERVAL '-0 00:00:00.000000001' DAY(9) TO SECOND(9),0)", CancellationToken.None);
            await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
            var ym = connection.CreateArrayType("INTERVAL YEAR TO MONTH", transaction);
            var ds = connection.CreateArrayType("INTERVAL DAY TO SECOND", transaction);
            using var command = db.Command(connection,
                "DECLARE TYPE years_t IS TABLE OF INTERVAL YEAR(9) TO MONTH; TYPE days_t IS TABLE OF INTERVAL DAY(9) TO SECOND(9); " +
                "ys years_t; ds days_t; BEGIN SELECT ym,ds BULK COLLECT INTO ys,ds FROM " + table + " WHERE id>:minimum ORDER BY id; " +
                "IF :minimum=4 THEN ys:=NULL; ds:=NULL; END IF; :oy:=ys; :od:=ds; " +
                "IF :fail=1 THEN RAISE_APPLICATION_ERROR(-20001,'Interval output failure'); END IF; UPDATE " + table + " SET n=n+1 WHERE id=1; END;", transaction);
            var minimum = DatabaseContext.Parameter(command, "minimum", 0);
            var fail = DatabaseContext.Parameter(command, "fail", 1);
            var years = DatabaseContext.Parameter(command, "oy", "old years"); years.ComplexType = ym; years.Direction = ParameterDirection.Output;
            var days = DatabaseContext.Parameter(command, "od", "old days"); days.ComplexType = ds; days.Direction = ParameterDirection.Output;
            if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            Assert.Equal("old years", years.Value); Assert.Equal("old days", days.Value);
            Assert.Equal(0, DatabaseContext.IntegerResult(await db.ScalarAsync(connection, "SELECT n FROM " + table + " WHERE id=1", CancellationToken.None, transaction)));
            var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync())
                : Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
            Assert.Equal(20001, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Equal("old years", years.Value); Assert.Equal("old days", days.Value); Assert.Equal(ConnectionState.Open, connection.State);
            fail.Value = 0;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                minimum.Value = attempt == 1 ? 3 : attempt == 2 ? 4 : 0;
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                if (attempt == 2) { Assert.Equal(DBNull.Value, years.Value); Assert.Equal(DBNull.Value, days.Value); continue; }
                var returnedYears = Assert.IsType<OceanBaseComplexValue>(years.Value); Assert.Same(ym, returnedYears.Type);
                var returnedDays = Assert.IsType<OceanBaseComplexValue>(days.Value); Assert.Same(ds, returnedDays.Type);
                if (attempt == 1) { Assert.Empty(returnedYears.GetValues()); Assert.Empty(returnedDays.GetValues()); }
                else
                {
                    Assert.Equal(new object?[] { new OceanBaseIntervalYearToMonth(true, 123456789, 11), null, new OceanBaseIntervalYearToMonth(false, 0, 0) }, returnedYears.GetValues());
                    Assert.Equal(new object?[] { new OceanBaseIntervalDayToSecond(false, 987654321, 23, 59, 58, 123456789), null, new OceanBaseIntervalDayToSecond(true, 0, 0, 0, 0, 1) }, returnedDays.GetValues());
                }
                Assert.Same(connection, transaction.Connection);
            }
            Assert.Equal(3, await db.CountAsync(connection, table, CancellationToken.None, transaction));
            Assert.Equal(4, DatabaseContext.IntegerResult(await db.ScalarAsync(connection, "SELECT n FROM " + table + " WHERE id=1", CancellationToken.None, transaction)));
            await using var observer = await db.OpenAsync();
            Assert.Equal(0, DatabaseContext.IntegerResult(await db.ScalarAsync(observer, "SELECT n FROM " + table + " WHERE id=1", CancellationToken.None)));
            command.Dispose();
            if (asynchronous) await transaction.CommitAsync(); else transaction.Commit();
            using var query = db.Command(observer, "SELECT ym,ds,n FROM " + table + " ORDER BY id");
            if (asynchronous) await query.PrepareAsync(); else query.Prepare();
            using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(new OceanBaseIntervalYearToMonth(true, 123456789, 11), reader.GetValue(0));
            Assert.Equal(new OceanBaseIntervalDayToSecond(false, 987654321, 23, 59, 58, 123456789), reader.GetValue(1));
            Assert.Equal(4, DatabaseContext.IntegerResult(reader.GetValue(2)));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(new OceanBaseIntervalYearToMonth(false, 0, 0), reader.GetValue(0));
            Assert.Equal(new OceanBaseIntervalDayToSecond(true, 0, 0, 0, 0, 1), reader.GetValue(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await db.DisposeAsync(); }
            catch (Exception cleanup) when (failure != null) { throw new AggregateException(failure, cleanup); }
        }
    }
}
