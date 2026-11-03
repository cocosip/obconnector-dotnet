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

public sealed class ClosingLobReaderTests
{
    public static IEnumerable<object[]> Rows() =>
        from row in LobMultiSendTests.Rows()
        from owned in new[] { false, true }
        from sequential in new[] { false, true }
        select new[] { row[0], row[1], row[2], owned, sequential };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ExternalLobsReadAfterMultiSendDrainBeforeReaderClosesConnection(string profileName, bool asynchronous, bool character, bool owned, bool sequential)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY,value " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
        var bytes = Enumerable.Range(0, 100000).Select(i => (byte)(i % 251)).ToArray();
        var text = string.Concat(Enumerable.Repeat("患者😀z", 20000));
        await using (var seed = await setup.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob))
        {
            try
            {
                if (character) await seed.WriteTextAsync(0, text); else await seed.WriteBytesAsync(0, bytes, 0, bytes.Length);
                using var insert = db.Command(setup, "INSERT INTO " + table + " VALUES(1,:p)");
                DatabaseContext.Parameter(insert, "p", seed); Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            finally { await seed.FreeTemporaryAsync(CancellationToken.None); }
        }
        await db.ExecuteAsync(setup, "INSERT INTO " + table + " VALUES(2,NULL)", CancellationToken.None);
        await db.ExecuteAsync(setup, "INSERT INTO " + table + " SELECT 3,value FROM " + table + " WHERE id=1", CancellationToken.None);
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (!owned) { if (asynchronous) await connection.OpenAsync(); else connection.Open(); }
        using var batch = owned ? source.CreateBatch() : connection.CreateBatch();
        for (var id = 1; id <= 3; id++)
        {
            var item = batch.CreateBatchCommand(); item.CommandText = "SELECT value FROM " + table + " WHERE id=:id";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", Value = id }); batch.BatchCommands.Add(item);
        }
        var handles = new List<OceanBaseLob>();
        try
        {
            var behavior = CommandBehavior.CloseConnection | (sequential ? CommandBehavior.SequentialAccess : 0);
            using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (i == 1) Assert.True(reader.IsDBNull(0));
                else
                {
                    var lob = reader.GetFieldValue<OceanBaseLob>(0); handles.Add(lob);
                    Assert.False(lob.Locator.HasInlineData, "Acceptance requires a real remote locator.");
                    Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            using var stale = new OceanBaseLob(handles[0].Context, handles[0].BindingBytes(), character);
            foreach (var lob in handles)
            {
                if (character)
                {
                    using var contents = lob.OpenTextReader(); Assert.Equal(text, asynchronous ? await contents.ReadToEndAsync() : contents.ReadToEnd());
                }
                else
                {
                    using var contents = lob.OpenRead(); using var target = new MemoryStream();
                    if (asynchronous) await contents.CopyToAsync(target); else contents.CopyTo(target);
                    Assert.Equal(bytes, target.ToArray());
                }
            }
            Assert.Equal(ConnectionState.Open, stale.Context.Connection.State);
            if (asynchronous) await reader.CloseAsync(); else reader.Close();
            Assert.Equal(ConnectionState.Closed, stale.Context.Connection.State);
            Assert.Throws<InvalidOperationException>(() => { if (character) stale.OpenTextReader(); else stale.OpenRead(); });
            Assert.Equal(3, await db.CountAsync(setup, table, CancellationToken.None));
        }
        finally { foreach (var lob in handles) lob.Dispose(); }
    }
}
