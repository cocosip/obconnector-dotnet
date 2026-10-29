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
    public static IEnumerable<object[]> StreamRows() =>
        from row in Rows()
        from size in new[] { 0, 1, 2000, 2001, 4000, 65536, 1048576 }
        from noBackslash in IntegrationEnvironment.GetProfile((string)row[0]).Oracle ? new[] { false } : new[] { false, true }
        select row.Concat(new object[] { size, noBackslash }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(StreamRows))]
    public async Task MultiStatementStreamsPreserveIndependentStoredBytes(string profileName, bool asynchronous, int size, bool noBackslash)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var binaryType = db.Oracle ? size <= 2000 ? "RAW(2000)" : "BLOB" : size <= 2000 ? "VARBINARY(2000)" : "LONGBLOB";
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + binaryType, CancellationToken.None);
        var data = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        using var first = new MemoryStream(new byte[] { 99 }.Concat(data).ToArray()); first.Position = 1;
        using var second = new MemoryStream(data);
        if (!db.Oracle) await db.ExecuteAsync(connection, "SET sql_mode='" + (noBackslash ? "NO_BACKSLASH_ESCAPES" : "") + "'", CancellationToken.None);
        using (var command = db.Command(connection,
            (!db.Oracle ? "SET sql_mode='" + (noBackslash ? "" : "NO_BACKSLASH_ESCAPES") + "'; " : "") +
            db.ParameterSql("INSERT INTO " + table + " VALUES (1,:first); INSERT INTO " + table + " VALUES (2,:second); INSERT INTO " + table + " VALUES (3,:null)")))
        {
            DatabaseContext.Parameter(command, "first", first, DbType.Binary);
            DatabaseContext.Parameter(command, "second", second, DbType.Binary);
            DatabaseContext.Parameter(command, "null", DBNull.Value, DbType.Binary);
            Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        }
        Assert.True(first.CanRead && second.CanRead); Assert.Equal(size + 1, first.Position); Assert.Equal(size, second.Position);
        await using var verification = await db.OpenAsync();
        Assert.Equal(3, await db.CountAsync(verification, table, CancellationToken.None));
        for (var id = 1; id <= 3; id++)
        {
            byte[]? actual = null; OceanBaseLob? lob = null;
            using (var command = db.Command(verification, "SELECT v FROM " + table + " WHERE id=" + id))
            using (var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess))
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
            if (id == 3 || size == 0 && db.Oracle) Assert.Null(actual);
            else Assert.Equal(data, actual);
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task MultiStatementStreamSourceFailureHasNoDatabaseSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + (db.Oracle ? "RAW(2000)" : "VARBINARY(2000)"), CancellationToken.None);
        using var source = new FailingMultiStatementSource();
        using var command = db.Command(connection, db.ParameterSql("INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (2,:q)"));
        DatabaseContext.Parameter(command, "p", source, DbType.Binary);
        DatabaseContext.Parameter(command, "q", new byte[] { 1 }, DbType.Binary);
        await Assert.ThrowsAsync<IOException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.True(source.ReadCount > 1); Assert.True(source.CanRead); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }

    private sealed class FailingMultiStatementSource : MemoryStream
    {
        internal int ReadCount { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++ReadCount > 1) throw new IOException("Test source failed.");
            buffer[offset] = 1; return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
    }
}
