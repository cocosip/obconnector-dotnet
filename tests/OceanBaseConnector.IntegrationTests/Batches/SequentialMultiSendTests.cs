using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class SequentialMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from owned in new[] { false, true }
        select new object[] { profile.Name, asynchronous, owned };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task SequentialMultiSendStreamsAndSkipsFieldsAcrossGroups(string profileName, bool asynchronous, bool owned)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY,bytes " + (db.Oracle ? "RAW(2000),value VARCHAR2(2000)" : "MEDIUMBLOB,value LONGTEXT"), CancellationToken.None);
        var bytes = Enumerable.Range(0, db.Oracle ? 2000 : 100000).Select(i => (byte)(i % 251)).ToArray();
        var text = string.Concat(Enumerable.Repeat("A中😀Z", db.Oracle ? 50 : 2000));
        for (var id = 1; id <= 3; id++)
        {
            using var seed = db.Command(setup, "INSERT INTO " + table + " VALUES(" + db.ParameterSql(":id,:bytes,:value") + ")");
            DatabaseContext.Parameter(seed, "id", id);
            DatabaseContext.Parameter(seed, "bytes", id == 2 ? DBNull.Value : bytes, DbType.Binary);
            DatabaseContext.Parameter(seed, "value", id == 2 ? DBNull.Value : text, DbType.String);
            Assert.Equal(1, await seed.ExecuteNonQueryAsync());
        }
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (!owned) await connection.OpenAsync();
        using var batch = owned ? source.CreateBatch() : connection.CreateBatch();
        for (var id = 1; id <= 3; id++)
        {
            var item = batch.CreateBatchCommand(); item.CommandText = "SELECT bytes,value FROM " + table + " WHERE id=" + db.ParameterSql(":id");
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id }); batch.BatchCommands.Add(item);
        }
        var update = batch.CreateBatchCommand(); update.CommandText = "UPDATE " + table + " SET value='updated' WHERE id=2"; batch.BatchCommands.Add(update);
        using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection)
            : batch.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection);
        for (var id = 1; id <= 3; id++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            if (id == 2)
            {
                Assert.True(asynchronous ? await reader.IsDBNullAsync(0) : reader.IsDBNull(0));
                Assert.True(asynchronous ? await reader.IsDBNullAsync(1) : reader.IsDBNull(1));
            }
            else
            {
                using var stream = reader.GetStream(0);
                if (id == 1)
                {
                    var prefix = new byte[13]; if (asynchronous) await stream.ReadExactlyAsync(prefix); else stream.ReadExactly(prefix);
                    Assert.Equal(bytes.Take(13), prefix);
                }
                else
                {
                    using var target = new MemoryStream(); if (asynchronous) await stream.CopyToAsync(target); else stream.CopyTo(target);
                    Assert.Equal(bytes, target.ToArray());
                }
                using var chars = reader.GetTextReader(1);
                Assert.Throws<InvalidOperationException>(() => stream.Read(new byte[1], 0, 1));
                if (id == 1) Assert.Equal(text, asynchronous ? await chars.ReadToEndAsync() : chars.ReadToEnd());
                else Assert.Equal('A', chars.Read());
            }
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Equal(0, reader.FieldCount); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Equal(1, reader.RecordsAffected);
        Assert.Equal(new[] { -1, -1, -1, 1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
        if (asynchronous) await reader.CloseAsync(); else reader.Close();
        if (!owned) Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(3, await db.CountAsync(setup, table, CancellationToken.None));
        Assert.Equal("updated", await db.ScalarAsync(setup, "SELECT value FROM " + table + " WHERE id=2", CancellationToken.None));
    }
}
