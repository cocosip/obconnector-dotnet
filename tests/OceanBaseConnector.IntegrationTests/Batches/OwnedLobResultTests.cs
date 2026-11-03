using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class OwnedLobResultTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from character in new[] { false, true }
        from route in new[] { "scalar", "scalar-sequential", "returning", "routine", "returning-remote", "routine-dml-remote" }
        select new object[] { profile.Name, asynchronous, character, route };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task OwnedBatchKeepsLobResultsAndOutputsAndPreservesAutocommitLimits(string profileName, bool asynchronous, bool character, string route)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = route != "scalar-sequential"; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY,n INTEGER,value " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
        var inline = route == "returning";
        var bytes = Enumerable.Range(0, inline ? 512 : 100000).Select(i => (byte)(i % 251)).ToArray();
        var text = string.Concat(Enumerable.Repeat("患者😀z", inline ? 16 : 20000));
        await using (var seed = await setup.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob))
        {
            try
            {
                if (character) await seed.WriteTextAsync(0, text); else await seed.WriteBytesAsync(0, bytes, 0, bytes.Length);
                using var insert = db.Command(setup, "INSERT INTO " + table + " VALUES(1,0,:p)");
                DatabaseContext.Parameter(insert, "p", seed); Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            finally { await seed.FreeTemporaryAsync(CancellationToken.None); }
        }
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        using var batch = source.CreateBatch();
        var scalarResult = route.StartsWith("scalar", System.StringComparison.Ordinal);
        var first = batch.CreateBatchCommand();
        var returning = route.StartsWith("returning", System.StringComparison.Ordinal);
        var changesRow = returning || route == "routine-dml-remote";
        first.CommandText = scalarResult ? "SELECT value FROM " + table + " WHERE id=1"
            : returning ? "UPDATE " + table + " SET n=n+1 WHERE id=1 RETURNING value INTO :output"
            : "BEGIN " + (changesRow ? "UPDATE " + table + " SET n=n+1 WHERE id=1; " : "") + "SELECT value INTO :output FROM " + table + " WHERE id=1; END;";
        OceanBaseParameter? output = null;
        if (!scalarResult)
        {
            output = new OceanBaseParameter { ParameterName = "output", Direction = returning ? ParameterDirection.Output : ParameterDirection.InputOutput,
                OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, Value = System.DBNull.Value };
            first.Parameters.Add(output);
        }
        batch.BatchCommands.Add(first);
        var last = batch.CreateBatchCommand(); last.CommandText = "UPDATE " + table + " SET n=n+" + (changesRow ? "2" : "3") + " WHERE id=1";
        batch.BatchCommands.Add(last);

        object? returned = null;
        if (scalarResult) returned = asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar();
        else Assert.True((asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()) >= 1);
        using var lob = Assert.IsType<OceanBaseLob>(scalarResult ? returned : output!.Value);
        Assert.Equal(inline, lob.Locator.HasInlineData);
        Assert.Equal(ConnectionState.Open, lob.Context.Connection.State); Assert.Equal(1, last.RecordsAffected);
        var originalSession = lob.Context.Session;
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
        async Task ReadContent(OceanBaseLob value)
        {
            if (character) { using var reader = value.OpenTextReader(); Assert.Equal(text, await reader.ReadToEndAsync()); }
            else { using var reader = value.OpenRead(); using var copy = new MemoryStream(); await reader.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray()); }
        }
        if (route.EndsWith("-remote", System.StringComparison.Ordinal))
        {
            // Current 4.4.2.2 autocommit DML locators are rejected by DBMS_LOB.READ; JDBC PL controls match.
            Assert.Equal(new System.Version(4, 4, 2, 2), originalSession.Info!.ProductVersion);
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => ReadContent(lob));
            Assert.Equal(600, error.ServerErrorCode); Assert.Equal("0A000", error.SqlState);
            await using var caller = await db.OpenAsync();
            await using var transaction = await caller.BeginTransactionAsync();
            using var ordinary = db.Command(caller, first.CommandText); ordinary.Transaction = transaction;
            var result = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output,
                OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, Value = System.DBNull.Value };
            ordinary.Parameters.Add(result); await ordinary.ExecuteNonQueryAsync();
            using var ordinaryLob = Assert.IsType<OceanBaseLob>(result.Value);
            Assert.False(ordinaryLob.Locator.HasInlineData); await ReadContent(ordinaryLob);
            await transaction.RollbackAsync();
        }
        else await ReadContent(lob);
        DatabaseContext.EqualNumber(await db.ScalarAsync(setup, "SELECT n FROM " + table + " WHERE id=1", CancellationToken.None), 3, "Rejected reexecution must not change stored rows.");
        if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
        Assert.False(originalSession.HasLiveLobHandles);
        if (output != null) output.Value = System.DBNull.Value;
        returned = asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar();
        if (!scalarResult) Assert.Null(returned);
        using var final = Assert.IsType<OceanBaseLob>(scalarResult ? returned : output!.Value);
        Assert.NotSame(originalSession, final.Context.Session);
        Assert.Equal(inline, final.Locator.HasInlineData);
        DatabaseContext.EqualNumber(await db.ScalarAsync(setup, "SELECT n FROM " + table + " WHERE id=1", CancellationToken.None), 6, "Disposed results must allow a fresh owned lease.");
        if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
        Assert.Equal(ConnectionState.Closed, final.Context.Connection.State);
        Assert.Throws<System.InvalidOperationException>(() => { if (character) final.OpenTextReader(); else final.OpenRead(); });
    }
}
