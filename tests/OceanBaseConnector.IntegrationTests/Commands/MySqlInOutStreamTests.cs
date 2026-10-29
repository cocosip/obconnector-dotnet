using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Commands;

public sealed class MySqlInOutStreamTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in ProfileTheoryData.MySqlWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var size in new[] { 0, 20000, 1048583 })
                        yield return new object[] { profile, asynchronous, prepared, size };
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task MySqlInOutStreamStoresExactInputsAndPublishesOutputs(string profileName, bool asynchronous, bool prepared, int size)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY, a LONGBLOB, b LONGBLOB", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE", registerCleanup: false);
        db.RegisterCleanup("PROCEDURE", procedure, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP PROCEDURE IF EXISTS " + procedure, CancellationToken.None);
        });
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(IN n INT, IN p LONGBLOB, INOUT b LONGBLOB, OUT o INT) BEGIN " +
            "INSERT INTO " + table + " VALUES(n,p,b); SET b=CONCAT(b,X'00FF'); SET o=OCTET_LENGTH(p)+OCTET_LENGTH(b); SELECT 7; SELECT 9; END", CancellationToken.None);
        using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        var number = DatabaseContext.Parameter(command, "n", 1, DbType.Int32);
        var input = DatabaseContext.Parameter(command, "p", DBNull.Value, DbType.Binary);
        var inout = DatabaseContext.Parameter(command, "b", DBNull.Value, DbType.Binary); inout.Direction = ParameterDirection.InputOutput;
        var output = DatabaseContext.Parameter(command, "o", -1, DbType.Int32); output.Direction = ParameterDirection.Output;
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var inputSource = new MemoryStream(new byte[] { 17, 0, 255, 23 }); inputSource.Position = 1;
            using var inoutSource = new MemoryStream(new byte[] { 99, 88 }.Concat(bytes).ToArray()); inoutSource.Position = 2;
            number.Value = attempt + 1; input.Value = inputSource; inout.Value = inoutSource; output.Value = -1;
            if (attempt == 0 && prepared)
            {
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(1, inputSource.Position); Assert.Equal(2, inoutSource.Position);
            }
            using (var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess))
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 7, "First routine result mismatch.");
                Assert.Same(inoutSource, inout.Value); DatabaseContext.EqualNumber(output.Value, -1, "Output published early.");
                if (attempt == 0)
                {
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), 9, "Second routine result mismatch.");
                    while (asynchronous ? await reader.NextResultAsync() : reader.NextResult())
                        while (asynchronous ? await reader.ReadAsync() : reader.Read()) { }
                }
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
            }
            Assert.Equal(bytes.Concat(new byte[] { 0, 255 }), Assert.IsType<byte[]>(inout.Value));
            DatabaseContext.EqualNumber(output.Value, size + 5, "Scalar OUT mismatch.");
            Assert.True(inputSource.CanRead); Assert.True(inoutSource.CanRead);
            Assert.Equal(inputSource.Length, inputSource.Position); Assert.Equal(inoutSource.Length, inoutSource.Position);
            await using var verifier = await db.OpenAsync();
            using var query = db.Command(verifier, "SELECT a,b FROM " + table + " WHERE id=" + (attempt + 1));
            using var stored = await query.ExecuteReaderAsync(); Assert.True(await stored.ReadAsync());
            Assert.Equal(new byte[] { 0, 255, 23 }, stored.GetFieldValue<byte[]>(0));
            Assert.Equal(bytes, stored.GetFieldValue<byte[]>(1)); Assert.False(await stored.ReadAsync());
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1", CancellationToken.None), 1, "Session unusable after INOUT streaming.");
    }

    public static IEnumerable<object[]> FailureProfiles()
    {
        foreach (var profile in ProfileTheoryData.MySqlWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var uploaded in new[] { false, true }) yield return new object[] { profile, asynchronous, uploaded };
    }

    [IntegrationTheory]
    [MemberData(nameof(FailureProfiles))]
    public async Task MySqlInOutStreamFailureDoesNotExecuteProcedure(string profileName, bool asynchronous, bool uploaded)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "v LONGBLOB", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE", registerCleanup: false);
        db.RegisterCleanup("PROCEDURE", procedure, async () =>
        {
            await using var cleanup = await db.OpenAsync(); await db.ExecuteAsync(cleanup, "DROP PROCEDURE IF EXISTS " + procedure, CancellationToken.None);
        });
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(INOUT b LONGBLOB) BEGIN INSERT INTO " + table + " VALUES(b); SET b=X'00FF'; END", CancellationToken.None);
        using var source = new FailingSource(uploaded);
        using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        var parameter = DatabaseContext.Parameter(command, "b", source, DbType.Binary); parameter.Direction = ParameterDirection.InputOutput;
        await Assert.ThrowsAsync<IOException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.True(source.CanRead); Assert.Same(source, parameter.Value);
        Assert.Equal(uploaded ? ConnectionState.Broken : ConnectionState.Open, connection.State);
        await using var verifier = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verifier, table, CancellationToken.None));
        if (!uploaded) DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1", CancellationToken.None), 1, "Pre-upload failure must preserve session usability.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task MySqlInOutStreamBatchPublishesEachOutput(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var procedure = db.NewObject("PROCEDURE", registerCleanup: false);
        db.RegisterCleanup("PROCEDURE", procedure, async () =>
        {
            await using var cleanup = await db.OpenAsync(); await db.ExecuteAsync(cleanup, "DROP PROCEDURE IF EXISTS " + procedure, CancellationToken.None);
        });
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(INOUT b LONGBLOB) BEGIN SET b=CONCAT(b,X'00FF'); END", CancellationToken.None);
        foreach (var asynchronous in new[] { false, true })
        {
            using var first = new MemoryStream(new byte[] { 0, 1 }); using var second = new MemoryStream(new byte[] { 254, 255 });
            using var batch = connection.CreateBatch(); var outputs = new List<OceanBaseParameter>();
            foreach (var source in new[] { first, second })
            {
                var item = batch.CreateBatchCommand(); item.CommandText = procedure; item.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, DbType = DbType.Binary, Value = source };
                item.Parameters.Add(parameter); outputs.Add(parameter); batch.BatchCommands.Add(item);
            }
            if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery();
            Assert.Equal(new byte[] { 0, 1, 0, 255 }, Assert.IsType<byte[]>(outputs[0].Value));
            Assert.Equal(new byte[] { 254, 255, 0, 255 }, Assert.IsType<byte[]>(outputs[1].Value));
            Assert.True(first.CanRead && second.CanRead);
        }
    }

    private sealed class FailingSource(bool uploaded) : MemoryStream
    {
        private int _reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!uploaded || ++_reads > 1) throw new IOException("Test source failed.");
            buffer[offset] = 255; return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
    }
}
