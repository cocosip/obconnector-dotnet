using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed class MultiStatementTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> ResultRows() =>
        from row in Rows()
        from disposeEarly in new[] { false, true }
        select row.Concat(new object[] { disposeEarly }).ToArray();

    public static IEnumerable<object[]> OracleRows() =>
        Rows().Where(row => IntegrationEnvironment.GetProfile((string)row[0]).Oracle);

    public static IEnumerable<object[]> OracleParameterRows() =>
        from row in OracleRows()
        from sequential in new[] { false, true }
        select row.Concat(new object[] { sequential }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(OracleParameterRows))]
    public async Task OracleScalarParametersPreserveValuesAcrossMultipleResults(string profileName, bool asynchronous, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection,
            "SELECT :n, :s, :b, :empty FROM DUAL; SELECT :s, :N FROM DUAL");
        var number = DatabaseContext.Parameter(command, "n", 7);
        var text = DatabaseContext.Parameter(command, "s", "quote'\\中文; :n --");
        var bytes = new byte[] { 0, 39, 92, 255 };
        DatabaseContext.Parameter(command, "b", bytes);
        DatabaseContext.Parameter(command, "empty", DBNull.Value);
        for (var value = 7; value <= 8; value++)
        {
            number.Value = value;
            text.Value = "quote'\\中文; :n --" + value;
            var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
            using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), value, "First result number.");
            Assert.Equal(text.Value, reader.GetString(1));
            Assert.Equal(bytes, reader.GetFieldValue<byte[]>(2));
            Assert.True(reader.IsDBNull(3));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(text.Value, reader.GetString(0));
            DatabaseContext.EqualNumber(reader.GetValue(1), value, "Repeated binding in second result.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Connection reuse.");
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleParameterizedMiddleErrorPreservesTransactionAndStopsSuffix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:a); INSERT INTO " + table +
            " VALUES (:b); INSERT INTO " + table + " VALUES (:c)", (OceanBaseTransaction)transaction);
        DatabaseContext.Parameter(command, "a", 1); DatabaseContext.Parameter(command, "b", 1); DatabaseContext.Parameter(command, "c", 2);
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.NotEqual(0, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None, (OceanBaseTransaction)transaction));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT SUM(id) FROM " + table, CancellationToken.None,
            (OceanBaseTransaction)transaction), 1, "Only the confirmed prefix executes.");
        await using var observer = await db.OpenAsync();
        Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
        if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        Assert.Same(session, connection.GetOpenSession());
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleParameterizedMultiStatementsRequireOptInBeforeSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: false);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id); INSERT INTO " + table + " VALUES (:id)");
        DatabaseContext.Parameter(command, "id", 1);
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ResultRows))]
    public async Task ResultsAreConsumedOrDiscardedBeforeConnectionReuse(string profileName, bool asynchronous, bool disposeEarly)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var session = connection.GetOpenSession();
        using (var command = db.Command(connection, db.SelectOne + "; " + db.ScalarSql("2")))
        {
            var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            try
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 1, "First statement result.");
                if (!disposeEarly)
                {
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), 2, "Second statement result.");
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
            }
            finally { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); }
        }
        if (disposeEarly)
        {
            Assert.Equal(ConnectionState.Broken, connection.State);
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            await Open(connection, asynchronous);
            Assert.NotSame(session, connection.GetOpenSession());
        }
        else Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A completed response or fresh connection must support subsequent commands.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task MiddleServerErrorStopsLaterStatementsAndKeepsConnectionUsable(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var missing = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1); SELECT id FROM " + missing +
            "; INSERT INTO " + table + " VALUES (2)");
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        });
        Assert.NotEqual(0, error.ServerErrorCode);
        Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Same(session, connection.GetOpenSession());
        await using var observer = await db.OpenAsync();
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, CancellationToken.None), 1,
            "The statement after the error must not execute.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A confirmed middle error must leave the connection reusable.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task DisabledMultiStatementsRejectBeforeSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: false);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1); INSERT INTO " + table + " VALUES (2)");
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        });
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    private static OceanBaseConnection CreateConnection(DatabaseContext db, bool enabled)
    {
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = enabled;
        return new OceanBaseConnection(builder.ConnectionString);
    }

    private static async Task Open(OceanBaseConnection connection, bool asynchronous)
    {
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
    }
}
