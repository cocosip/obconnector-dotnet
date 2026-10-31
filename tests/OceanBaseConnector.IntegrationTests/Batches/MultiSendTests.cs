using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class MultiSendTests
{
    public static TheoryData<string, bool> OraclePreparedRows()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct")))
            foreach (var asynchronous in new[] { false, true }) rows.Add(profile.Name, asynchronous);
        return rows;
    }

    public static TheoryData<string, bool> MySqlPreparedRows()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => !p.Oracle && p.CreateObjects && p.HasTag("direct")))
            foreach (var asynchronous in new[] { false, true }) rows.Add(profile.Name, asynchronous);
        return rows;
    }

    public static TheoryData<string, bool> PreparedRows()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasTag("direct")))
            foreach (var asynchronous in new[] { false, true }) rows.Add(profile.Name, asynchronous);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlPreparedRows))]
    public async Task MySqlStreamMultiSendStoresAndReadsBack(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = profile.CreateObjects,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY, value BLOB", CancellationToken.None);
        using var first = new MemoryStream(Encoding.UTF8.GetBytes("stream-1"));
        using var second = new MemoryStream(Encoding.UTF8.GetBytes("stream-2"));
        await using (var batch = connection.CreateBatch())
        {
            foreach (var (id, source) in new[] { (1, first), (2, second) })
            {
                var item = batch.CreateBatchCommand();
                item.CommandText = "INSERT INTO " + table + " (id, value) VALUES (?, ?)";
                item.Parameters.Add(new OceanBaseParameter { DbType = DbType.Int32, Value = id });
                item.Parameters.Add(new OceanBaseParameter { DbType = DbType.Binary, Value = source });
                batch.BatchCommands.Add(item);
            }
            var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
            Assert.Equal(2, affected);
        }

        await using var command = db.Command(connection, "SELECT id, value FROM " + table + " ORDER BY id");
        await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        var rows = 0;
        while (asynchronous ? await reader.ReadAsync() : reader.Read())
        {
            var expected = Encoding.UTF8.GetBytes("stream-" + reader.GetInt32(0));
            using var stream = reader.GetStream(1);
            using var actual = new MemoryStream();
            if (asynchronous) await stream.CopyToAsync(actual); else stream.CopyTo(actual);
            Assert.Equal(expected, actual.ToArray());
            rows++;
        }
        Assert.Equal(2, rows);
    }

    [IntegrationTheory]
    [MemberData(nameof(OraclePreparedRows))]
    public async Task OracleStreamMultiSendUsesPieceDataAndReadsBack(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        builder.UseOraclePrepareExecute = false;
        builder.UseLobLocators = false;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = profile.CreateObjects,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value BLOB", CancellationToken.None);
        var firstBytes = Enumerable.Range(0, 3).Select(i => (byte)(i + 1)).ToArray();
        var secondBytes = Enumerable.Range(0, 90007).Select(i => (byte)(i * 31)).ToArray();
        using var first = new MemoryStream(firstBytes, writable: false);
        using var second = new MemoryStream(secondBytes, writable: false);
        await using (var batch = connection.CreateBatch())
        {
            foreach (var (id, source) in new[] { (1, first), (2, second) })
            {
                var item = batch.CreateBatchCommand();
                item.CommandText = "INSERT INTO " + table + " (id, value) VALUES (:id, :value)";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.Binary, Value = source });
                batch.BatchCommands.Add(item);
            }
            var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
            Assert.Equal(2, affected);
        }

        await using var command = db.Command(connection, "SELECT id, value FROM " + table + " ORDER BY id");
        await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        foreach (var expected in new[] { firstBytes, secondBytes })
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            var value = reader.GetValue(1);
            using var actual = new MemoryStream();
            if (value is OceanBaseLob lob)
            {
                using (lob)
                using (var source = lob.OpenRead())
                {
                    if (asynchronous) await source.CopyToAsync(actual); else source.CopyTo(actual);
                }
            }
            else
            {
                using var source = reader.GetStream(1);
                if (asynchronous) await source.CopyToAsync(actual); else source.CopyTo(actual);
            }
            Assert.Equal(expected, actual.ToArray());
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedRows))]
    public async Task InputOnlyPreparedDmlSendsAndReadsInOrder(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        if (profile.Oracle)
        {
            // Multi-send uses ordinary server prepared statements in this first subset.
            builder.UseOraclePrepareExecute = false;
            builder.UseLobLocators = false;
        }
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = profile.CreateObjects,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "VARCHAR2(60)" : "VARCHAR(60)"), CancellationToken.None);
        await using var batch = connection.CreateBatch();
        foreach (var (id, value) in new[] { (1, "first"), (2, "second"), (3, "third") })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = "INSERT INTO " + table + " VALUES (" + db.ParameterSql(":id, :value") + ")";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.String, Value = value });
            batch.BatchCommands.Add(item);
        }
        var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
        Assert.Equal(3, affected);
        Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
        Assert.Equal(3, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedRows))]
    public async Task PreparedScalarMultiSendReturnsFirstValueAfterDraining(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        if (profile.Oracle)
        {
            builder.UseOraclePrepareExecute = false;
            builder.UseLobLocators = false;
        }
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        await using var batch = connection.CreateBatch();
        foreach (var value in new[] { 11, 22, 33 })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = value.ToString() + (profile.Oracle ? " FROM DUAL" : "");
            item.CommandText = "SELECT " + item.CommandText;
            batch.BatchCommands.Add(item);
        }
        var scalar = asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar();
        DatabaseContext.EqualNumber(scalar, 11, "Multi-send scalar must return the first value.");
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedRows))]
    public async Task PreparedReaderMultiSendPreservesResultOrder(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        if (profile.Oracle)
        {
            builder.UseOraclePrepareExecute = false;
            builder.UseLobLocators = false;
        }
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        await using var batch = connection.CreateBatch();
        foreach (var value in new[] { 11, 22 })
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = dbScalarSql(profile, value);
            batch.BatchCommands.Add(item);
        }
        await using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        DatabaseContext.EqualNumber(reader.GetValue(0), 11, "First multi-send reader result mismatch.");
        Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        DatabaseContext.EqualNumber(reader.GetValue(0), 22, "Second multi-send reader result mismatch.");
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Equal(-1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);

        static string dbScalarSql(IntegrationProfile profile, int value) =>
            "SELECT " + value + (profile.Oracle ? " FROM DUAL" : "");
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedRows))]
    public async Task PreparedMultiSendErrorPublishesPrefixAndLeavesSuffixUnsent(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        if (profile.Oracle)
        {
            builder.UseOraclePrepareExecute = false;
            builder.UseLobLocators = false;
        }
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = profile.CreateObjects,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "VARCHAR2(60)" : "VARCHAR(60)"), CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1, 'seed')", CancellationToken.None);

        await using var batch = connection.CreateBatch();
        var first = Add(2, "first");
        var duplicate = Add(1, "duplicate");
        var suffix = Add(3, "suffix");

        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await batch.ExecuteNonQueryAsync();
            else batch.ExecuteNonQuery();
        });

        Assert.Same(duplicate, error.BatchCommand);
        Assert.NotEqual(0, error.ServerErrorCode);
        Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(1, first.RecordsAffected);
        Assert.Equal(-1, duplicate.RecordsAffected);
        Assert.Equal(-1, suffix.RecordsAffected);
        Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "The connection must remain reusable after a known multi-send error.");

        OceanBaseBatchCommand Add(int id, string value)
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = "INSERT INTO " + table + " VALUES (" + db.ParameterSql(":id, :value") + ")";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.String, Value = value });
            batch.BatchCommands.Add(item);
            return (OceanBaseBatchCommand)item;
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(OraclePreparedRows))]
    public async Task OraclePrepareExecuteStreamMultiSendFallsBackToPieceData(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        builder.UseOraclePrepareExecute = true;
        builder.UseLobLocators = true;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = profile.CreateObjects,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value BLOB", CancellationToken.None);
        var expected = new[]
        {
            Enumerable.Range(0, 3).Select(i => (byte)(i + 1)).ToArray(),
            Enumerable.Range(0, 90007).Select(i => (byte)(i * 31)).ToArray(),
        };
        using var first = new MemoryStream(expected[0], writable: false);
        using var second = new MemoryStream(expected[1], writable: false);
        await using (var batch = connection.CreateBatch())
        {
            foreach (var (id, source) in new[] { (1, first), (2, second) })
            {
                var item = batch.CreateBatchCommand();
                item.CommandText = "INSERT INTO " + table + " VALUES (:id, :value)";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "value", DbType = DbType.Binary, Value = source });
                batch.BatchCommands.Add(item);
            }
            var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
            Assert.Equal(2, affected);
        }

        var values = new List<object?>();
        await using (var command = db.Command(connection, "SELECT id, value FROM " + table + " ORDER BY id"))
        await using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
        {
            while (asynchronous ? await reader.ReadAsync() : reader.Read()) values.Add(reader.GetValue(1));
        }
        Assert.Equal(expected.Length, values.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            using var actual = new MemoryStream();
            if (values[i] is OceanBaseLob lob)
            {
                using (lob)
                using (var source = lob.OpenRead())
                {
                    if (asynchronous) await source.CopyToAsync(actual); else source.CopyTo(actual);
                }
            }
            else
            {
                var bytes = Assert.IsType<byte[]>(values[i]);
                actual.Write(bytes, 0, bytes.Length);
            }
            Assert.Equal(expected[i], actual.ToArray());
        }
    }
}
