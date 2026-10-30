using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class RefCursorLifecycleTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var fetchSize in new[] { 1, int.MaxValue })
                        yield return new object[] { profile, asynchronous, prepared, fetchSize };
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task OutputHintsPreserveIndependentCursorPagingAndAtomicPublicationAfterErrors(string profileName, bool asynchronous, bool prepared, int fetchSize)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,b RAW(8)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(k IN NUMBER,b IN RAW,f IN NUMBER,p IN OUT NUMBER,c OUT SYS_REFCURSOR) IS " +
            "BEGIN INSERT INTO " + table + " VALUES(k,b); p:=NVL(p,0)+1; " +
            "OPEN c FOR SELECT 1 AS n FROM DUAL UNION ALL SELECT 2 FROM DUAL UNION ALL SELECT 3 FROM DUAL ORDER BY 1; " +
            "IF f=1 THEN INSERT INTO " + table + " VALUES(k,b); END IF; END;", CancellationToken.None);
        var session = connection.GetOpenSession();
        await using var observer = await db.OpenAsync();
        using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        command.FetchSize = fetchSize;
        var id = DatabaseContext.Parameter(command, "k", 1, DbType.Int32);
        var bytes = DatabaseContext.Parameter(command, "b", DBNull.Value, DbType.Binary);
        var fail = DatabaseContext.Parameter(command, "f", 0, DbType.Int32);
        var state = DatabaseContext.Parameter(command, "p", DBNull.Value, DbType.Int32); state.Direction = ParameterDirection.InputOutput;
        var output = DatabaseContext.Parameter(command, "c", "unpublished"); output.Direction = ParameterDirection.Output; output.OceanBaseDbType = OceanBaseDbType.RefCursor;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            id.Value = attempt + 1; state.Value = attempt == 0 ? DBNull.Value : 7;
            using var source = new MemoryStream(new byte[] { 42 }); bytes.Value = source;
            var originalState = state.Value; var originalCursor = output.Value;
            command.UseCursorOffset = true;
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            });
            Assert.False(command.HasSentSql); Assert.Equal(0, source.Position);
            Assert.Same(originalState, state.Value); Assert.Same(originalCursor, output.Value);
            Assert.Equal(attempt, await db.CountAsync(observer, table, CancellationToken.None));
            Assert.Same(session, connection.GetOpenSession());
            command.UseCursorOffset = false;
            if (prepared)
            {
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(0, source.Position);
                Assert.Equal(attempt, await db.CountAsync(observer, table, CancellationToken.None));
            }
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            DatabaseContext.EqualNumber(state.Value, attempt == 0 ? 1 : 8, "INOUT publication mismatch.");
            Assert.Equal(1, source.Position); Assert.Equal(fetchSize, command.FetchSize);
            await ReadCursor(Assert.IsType<OceanBaseRefCursor>(output.Value), asynchronous);
            Assert.Equal(attempt + 1, await db.CountAsync(observer, table, CancellationToken.None));
        }

        id.Value = 3; fail.Value = 1; state.Value = 10; output.Value = "unpublished";
        var previousState = state.Value; var previousCursor = output.Value;
        using var failedSource = new MemoryStream(new byte[] { 43 }); bytes.Value = failedSource;
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        });
        Assert.Equal(1, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Same(previousState, state.Value); Assert.Same(previousCursor, output.Value);
        Assert.Same(session, connection.GetOpenSession());
        Assert.Equal(2, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=3", CancellationToken.None), 0, "Failed PL execution must not publish its inserted row.");

        fail.Value = 0;
        using var correctedSource = new MemoryStream(new byte[] { 44 }); bytes.Value = correctedSource;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        DatabaseContext.EqualNumber(state.Value, 11, "INOUT after a known error mismatch.");
        var cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
        command.Dispose();
        await ReadCursor(cursor, asynchronous);
        Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
        using var stored = db.Command(observer, "SELECT id,b FROM " + table + " ORDER BY id");
        if (asynchronous) await stored.PrepareAsync(); else stored.Prepare();
        using (var reader = asynchronous ? await stored.ExecuteReaderAsync() : stored.ExecuteReader())
        {
            for (var row = 1; row <= 3; row++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), row, "Stored call identity mismatch.");
                Assert.Equal(new byte[] { row == 3 ? (byte)44 : (byte)42 }, reader.GetFieldValue<byte[]>(1));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Cursor disposal must leave the original session reusable.");
    }

    private static async Task ReadCursor(OceanBaseRefCursor cursor, bool asynchronous)
    {
        using (cursor)
        using (var reader = asynchronous ? await cursor.GetDataReaderAsync(1, CommandBehavior.SequentialAccess) : cursor.GetDataReader(1, CommandBehavior.SequentialAccess))
        {
            for (var row = 1; row <= 3; row++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), row, "Child cursor paging mismatch.");
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cursor.GetDataReaderAsync());
    }
}
