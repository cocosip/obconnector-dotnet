using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> OracleLargeBinaryRows() =>
        from row in OracleParameterRows()
        from size in new[] { 0, 2000, 2001, 4000, 32767, 65536, 1048576 }
        select row.Concat(new object[] { size }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(OracleLargeBinaryRows))]
    public async Task OracleLargeBinaryLiteralsPreserveRawResultsAndIndependentBlobStorage(string profileName, bool asynchronous, bool sequential, int size)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var data = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v BLOB", CancellationToken.None);
        using (var command = db.Command(connection,
            "INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (2,:P); INSERT INTO " + table + " VALUES (3,:n)"))
        {
            var parameter = DatabaseContext.Parameter(command, "p", data); parameter.OceanBaseDbType = OceanBaseDbType.Binary;
            DatabaseContext.Parameter(command, "n", DBNull.Value, DbType.Binary);
            Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Same(data, parameter.Value);
        }
        await using var verification = await db.OpenAsync();
        Assert.Equal(3, await db.CountAsync(verification, table, CancellationToken.None));
        for (var id = 1; id <= 3; id++)
        {
            var actual = await ReadLargeBinaryBlob(db, verification, table, id, asynchronous, sequential);
            if (id == 3 || size == 0) Assert.Null(actual);
            else Assert.True(data.AsSpan().SequenceEqual(actual), "Independent BLOB storage must preserve every byte.");
        }
        using (var command = db.Command(connection, "SELECT :p v, :n n FROM DUAL; SELECT :P v, :N n FROM DUAL"))
        {
            DatabaseContext.Parameter(command, "p", data, DbType.Binary);
            DatabaseContext.Parameter(command, "n", DBNull.Value, DbType.Binary);
            using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
            for (var result = 0; result < 2; result++)
            {
                if (result > 0) Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal("RAW", reader.GetDataTypeName(0)); Assert.Equal(typeof(byte[]), reader.GetFieldType(0));
                if (size == 0) Assert.True(reader.IsDBNull(0));
                else Assert.True(data.AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(0)), "RAW expression bytes must remain exact.");
                Assert.True(reader.IsDBNull(1)); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleLargeBinaryLiteralsKeepTargetRawLimitAndKnownTransactionPrefix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v RAW(2000)", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        using var command = db.Command(connection,
            "INSERT INTO " + table + " VALUES (1,:good); INSERT INTO " + table + " VALUES (2,:large); INSERT INTO " + table + " VALUES (3,:good)", transaction);
        DatabaseContext.Parameter(command, "good", Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray(), DbType.Binary);
        DatabaseContext.Parameter(command, "large", new byte[2001], DbType.Binary);
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(25137, error.ServerErrorCode); Assert.Equal("22003", error.SqlState); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT id FROM " + table, CancellationToken.None, transaction), 1, "Only confirmed prefix is executed.");
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
        if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }

    private static async Task<byte[]?> ReadLargeBinaryBlob(DatabaseContext db, OceanBaseConnection connection, string table, int id, bool asynchronous, bool sequential)
    {
        byte[]? actual = null; OceanBaseLob? lob = null;
        using (var command = db.Command(connection, "SELECT v FROM " + table + " WHERE id=" + id))
        using (var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
            : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            if (!reader.IsDBNull(0))
            {
                if (reader.GetFieldType(0) == typeof(OceanBaseLob)) lob = (OceanBaseLob)reader.GetValue(0);
                else
                {
                    using var source = reader.GetStream(0); using var target = new MemoryStream();
                    if (asynchronous) await source.CopyToAsync(target); else source.CopyTo(target);
                    actual = target.ToArray();
                }
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        if (lob != null)
        {
            using (lob)
            using (var source = lob.OpenRead())
            using (var target = new MemoryStream())
            {
                if (asynchronous) await source.CopyToAsync(target); else source.CopyTo(target);
                actual = target.ToArray();
            }
        }
        return actual;
    }
}
