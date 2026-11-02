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

public sealed class LobMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from character in new[] { false, true }
        select new object[] { profile.Name, asynchronous, character };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task LobMultiSendPreservesStoredContentAndCallerHandle(string profileName, bool asynchronous, bool character)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var type = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
        var bytes = Enumerable.Range(0, 512).Select(i => (byte)(i * 7)).ToArray();
        const string text = "患者🙂'); --\0tail";
        await using var lob = await connection.CreateTemporaryLobAsync(type);
        try
        {
            if (character) await lob.WriteTextAsync(0, text);
            else await lob.WriteBytesAsync(0, bytes, 0, bytes.Length);
            using (var batch = connection.CreateBatch())
            {
                for (var i = 1; i <= 3; i++)
                {
                    var item = batch.CreateBatchCommand();
                    item.CommandText = "INSERT INTO " + table + " VALUES(:id,:p)";
                    item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i });
                    var parameter = new OceanBaseParameter { ParameterName = "p", Value = i == 2 ? DBNull.Value : lob };
                    if (i != 3) parameter.OceanBaseDbType = type;
                    item.Parameters.Add(parameter); batch.BatchCommands.Add(item);
                }
                Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
            }
            using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES(4,:p)"))
            {
                DatabaseContext.Parameter(insert, "p", lob);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            await using var observer = await db.OpenAsync();
            Assert.Equal(4, await db.CountAsync(observer, table, CancellationToken.None));
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=2 AND value IS NULL", CancellationToken.None),
                1, "Typed NULL must survive locator batching.");
            foreach (var id in new[] { 1, 3, 4 })
            {
                using var query = db.Command(observer, "SELECT value FROM " + table + " WHERE id=:id");
                DatabaseContext.Parameter(query, "id", id);
                using var stored = Assert.IsType<OceanBaseLob>(await query.ExecuteScalarAsync());
                if (character)
                {
                    using var source = stored.OpenTextReader();
                    Assert.Equal(text, await source.ReadToEndAsync());
                }
                else
                {
                    using var source = stored.OpenRead(); using var destination = new MemoryStream();
                    await source.CopyToAsync(destination); Assert.Equal(bytes, destination.ToArray());
                }
            }
            using var results = connection.CreateBatch();
            for (var id = 1; id <= 3; id++)
            {
                var item = results.CreateBatchCommand(); item.CommandText = "SELECT value FROM " + table + " WHERE id=:id";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
                results.BatchCommands.Add(item);
            }
            var handles = new List<OceanBaseLob>();
            try
            {
                using (var reader = asynchronous ? await results.ExecuteReaderAsync() : results.ExecuteReader())
                {
                    for (var i = 0; i < 3; i++)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        if (i == 1) Assert.True(reader.IsDBNull(0));
                        else
                        {
                            var stored = reader.GetFieldValue<OceanBaseLob>(0); handles.Add(stored);
                            Assert.Throws<InvalidOperationException>(() => { if (character) stored.OpenTextReader(); else stored.OpenRead(); });
                        }
                        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    }
                }
                handles.Add(Assert.IsType<OceanBaseLob>(asynchronous ? await results.ExecuteScalarAsync() : results.ExecuteScalar()));
                Assert.Equal(-1, asynchronous ? await results.ExecuteNonQueryAsync() : results.ExecuteNonQuery());
                foreach (var stored in handles)
                {
                    if (character)
                    {
                        using var source = stored.OpenTextReader(); Assert.Equal(text, await source.ReadToEndAsync());
                    }
                    else
                    {
                        using var source = stored.OpenRead(); using var destination = new MemoryStream();
                        await source.CopyToAsync(destination); Assert.Equal(bytes, destination.ToArray());
                    }
                }
            }
            finally { foreach (var stored in handles) stored.Dispose(); }
        }
        finally { await lob.FreeTemporaryAsync(CancellationToken.None); }
    }
}
