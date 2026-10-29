using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class InOutStreamTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var size in new[] { 0, 1, 20000, 32765 })
                        foreach (var verifierLocators in new[] { false, true }) yield return new object[] { profile, asynchronous, prepared, size, verifierLocators };
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task OracleInOutStreamStoresExactInputsAndPublishesRawOutput(string profileName, bool asynchronous, bool prepared, int size, bool verifierLocators)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, a BLOB, b BLOB", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(n IN NUMBER, a IN RAW, b IN OUT RAW, o OUT NUMBER) AS BEGIN " +
            "INSERT INTO " + table + " VALUES(n,a,b); b := UTL_RAW.CONCAT(b,HEXTORAW('00FF')); o := UTL_RAW.LENGTH(b); END;", CancellationToken.None);
        using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        var id = DatabaseContext.Parameter(command, "n", 1);
        var input = DatabaseContext.Parameter(command, "a", DBNull.Value, DbType.Binary);
        var inout = DatabaseContext.Parameter(command, "b", DBNull.Value, DbType.Binary); inout.Direction = ParameterDirection.InputOutput;
        var output = DatabaseContext.Parameter(command, "o", -1); output.Direction = ParameterDirection.Output;
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var a = new MemoryStream(new byte[] { 99, 0, 255, 23 }); a.Position = 1;
            using var b = new MemoryStream(new byte[] { 99, 88 }.Concat(bytes).ToArray()); b.Position = 2;
            id.Value = attempt + 1; input.Value = a; inout.Value = b; output.Value = -1;
            if (attempt == 0 && prepared)
            {
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(1, a.Position); Assert.Equal(2, b.Position);
            }
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Equal(Convert.ToHexString(bytes.Concat(new byte[] { 0, 255 }).ToArray()), Assert.IsType<string>(inout.Value));
            DatabaseContext.EqualNumber(output.Value, size + 2, "Scalar output differs.");
            Assert.True(a.CanRead); Assert.True(b.CanRead); Assert.Equal(a.Length, a.Position); Assert.Equal(b.Length, b.Position);
            var verifierBuilder = db.Profile.CreateBuilder(); verifierBuilder.UseLobLocators = verifierLocators;
            await using var verifier = new OceanBaseConnection(verifierBuilder.ConnectionString); await verifier.OpenAsync();
            using var query = db.Command(verifier, "SELECT a,b FROM " + table + " WHERE id=" + (attempt + 1));
            using var stored = await query.ExecuteReaderAsync(); Assert.True(await stored.ReadAsync());
            var storedA = stored.GetValue(0); var storedB = size == 0 ? null : stored.GetValue(1);
            using var storedALob = storedA as OceanBaseLob; using var storedBLob = storedB as OceanBaseLob;
            if (size == 0) Assert.True(stored.IsDBNull(1));
            Assert.False(await stored.ReadAsync()); Assert.False(await stored.NextResultAsync());
            Assert.Equal(new byte[] { 0, 255, 23 }, await ReadBlob(storedA));
            if (storedB != null) Assert.Equal(bytes, await ReadBlob(storedB));
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Session unusable.");
    }

    public static IEnumerable<object[]> FailureProfiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var uploaded in new[] { false, true }) yield return new object[] { profile, asynchronous, uploaded };
    }

    [IntegrationTheory]
    [MemberData(nameof(FailureProfiles))]
    public async Task OracleInOutStreamSourceFailureDoesNotExecutePlOrPublish(string profileName, bool asynchronous, bool uploaded)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        using var command = db.Command(connection, "BEGIN INSERT INTO " + table + " VALUES(1); :p := HEXTORAW('00FF'); END;");
        using var source = new FailingSource(uploaded);
        var inout = DatabaseContext.Parameter(command, "p", source); inout.Direction = ParameterDirection.InputOutput;
        await Assert.ThrowsAsync<IOException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
        Assert.Same(source, inout.Value); Assert.True(source.CanRead);
        Assert.Equal(uploaded ? ConnectionState.Broken : ConnectionState.Open, connection.State);
        await using var verifier = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verifier, table, CancellationToken.None));
        if (!uploaded) DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Session unusable before upload.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task OracleInOutStreamBatchPublishesDistinctSourcesAndStoresBoth(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v RAW(20)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(n IN NUMBER, p IN OUT RAW) AS BEGIN INSERT INTO " + table +
            " VALUES(n,p); p := UTL_RAW.CONCAT(p,HEXTORAW('AB')); END;", CancellationToken.None);
        foreach (var asynchronous in new[] { false, true })
        {
            using var first = new MemoryStream(new byte[] { 0, 255 }); using var second = new MemoryStream(new byte[] { 17, 23 });
            using var batch = connection.CreateBatch();
            var parameters = new System.Collections.Generic.List<System.Data.Common.DbParameter>();
            var id = asynchronous ? 3 : 1;
            foreach (var source in new[] { first, second })
            {
                var item = batch.CreateBatchCommand(); item.CommandText = procedure; item.CommandType = CommandType.StoredProcedure;
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = id++ });
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = source };
                item.Parameters.Add(parameter); parameters.Add(parameter); batch.BatchCommands.Add(item);
            }
            if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery();
            Assert.Equal("00FFAB", Assert.IsType<string>(parameters[0].Value));
            Assert.Equal("1117AB", Assert.IsType<string>(parameters[1].Value));
            Assert.True(first.CanRead); Assert.True(second.CanRead);
        }
        await using var verifier = await db.OpenAsync(); using var query = db.Command(verifier, "SELECT v FROM " + table + " ORDER BY id");
        using var reader = await query.ExecuteReaderAsync();
        foreach (var expected in new[] { new byte[] { 0, 255 }, new byte[] { 17, 23 }, new byte[] { 0, 255 }, new byte[] { 17, 23 } })
        { Assert.True(await reader.ReadAsync()); Assert.Equal(expected, reader.GetFieldValue<byte[]>(0)); }
        Assert.False(await reader.ReadAsync());
    }

    private sealed class FailingSource(bool uploaded) : MemoryStream(new byte[20000])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!uploaded || Position >= 8193) throw new IOException("source failed");
            return base.Read(buffer, offset, count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));
    }

    private static async Task<byte[]> ReadBlob(object value)
    {
        if (value is byte[] bytes) return bytes;
        var lob = Assert.IsType<OceanBaseLob>(value);
        using var source = lob.OpenRead(); using var destination = new MemoryStream();
        await source.CopyToAsync(destination); return destination.ToArray();
    }
}
