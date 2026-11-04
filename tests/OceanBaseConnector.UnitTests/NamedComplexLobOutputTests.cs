using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed partial class TextCommandTests
{
    public static IEnumerable<object[]> NamedLobScalarOutputCases()
    {
        foreach (var asynchronous in new[] { false, true })
            foreach (var kind in new[] { "text", "procedure", "function" })
                foreach (var prepared in new[] { false, true })
                    foreach (var failure in new[] { false, true })
                        yield return new object[] { asynchronous, kind, prepared, failure };
    }

    [Theory]
    [MemberData(nameof(NamedLobScalarOutputCases))]
    public async Task NamedLobInputAndScalarOutputPublishOnlyAfterSuccessfulTail(bool asynchronous, string kind, bool prepared, bool failure)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sql = kind == "function" ? "BEGIN ? := consume(\"Owner\".\"Thing\"(?)); END;"
            : "BEGIN consume(\"Owner\".\"Thing\"(?), ?); END;";
        var server = Server(async channel =>
        {
            await ComplexScalarType(channel, scalar: "BLOB", collection: false);
            channel.BeginCommand();
            await LobQueryReply(channel, 210, InlineLob(new byte[] { 1, 2, 3 }));
            if (prepared)
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal(sql, Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 42, 2);
            }
            var execute = await StatementCommand(channel, 0xa1);
            var cursor = new ByteCursor(execute);
            cursor.Skip(1);
            Assert.Equal(prepared ? 42u : 0u, cursor.ReadUInt32());
            cursor.Skip(5);
            Assert.Equal(sql, Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
            Assert.Equal(2u, cursor.ReadUInt32());
            await OracleOutputReply(channel, false);
            waiting.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await channel.WritePacketAsync(failure ? Error() : Ok(1));
            await StatementCommand(channel, 25);
            channel.BeginCommand(); await Query(channel, "after output"); await channel.WritePacketAsync(Ok(1));
        }, lobLocators: true);
        using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
        try
        {
            await connection.OpenAsync();
            var type = await connection.GetComplexTypeAsync("Owner", "Thing");
            using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
            using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
            using (var command = connection.CreateCommand())
            {
                command.CommandType = kind == "text" ? CommandType.Text : CommandType.StoredProcedure;
                command.CommandText = kind == "text" ? "BEGIN consume(:p, :n); END;" : "consume";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = type.CreateValue(lob) });
                var output = new OceanBaseParameter
                {
                    ParameterName = "n",
                    DbType = DbType.Int64,
                    Value = 9L,
                    Direction = kind == "function" ? ParameterDirection.ReturnValue : ParameterDirection.InputOutput
                };
                command.Parameters.Add(output);
                if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                var executing = Task.Run(async () => asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(9L, output.Value); Assert.Equal(2, command.Parameters.Count);
                release.SetResult();
                if (failure)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(async () => await executing);
                    Assert.False(error.IsExecutionOutcomeUnknown);
                    Assert.Equal(9L, output.Value);
                }
                else { Assert.Equal(1, await executing); Assert.Equal(42L, output.Value); }
            }
            using var after = connection.CreateCommand(); after.CommandText = "after output";
            Assert.Equal(1, await after.ExecuteNonQueryAsync());
            using var contents = lob.OpenRead();
            var bytes = new byte[3]; contents.ReadExactly(bytes);
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        }
        catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
        finally { release.TrySetResult(); connection.Close(); server.Stop(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false, false, "stale")]
    [InlineData(true, false, "stale")]
    [InlineData(false, true, "stale")]
    [InlineData(true, true, "stale")]
    [InlineData(false, false, "disposed")]
    [InlineData(true, false, "disposed")]
    [InlineData(false, true, "disposed")]
    [InlineData(true, true, "disposed")]
    [InlineData(false, false, "complex-inout")]
    [InlineData(true, false, "complex-inout")]
    [InlineData(false, true, "complex-inout")]
    [InlineData(true, true, "complex-inout")]
    public async Task NamedLobInputWithScalarOutputRejectsInvalidResourcesBeforeSend(bool asynchronous, bool procedure, string fault)
    {
        var server = Server(async channel =>
        {
            await ComplexScalarType(channel, scalar: "BLOB", collection: false);
            channel.BeginCommand(); await LobQueryReply(channel, 210, InlineLob(new byte[] { 1 }));
            channel.BeginCommand(); await Query(channel, "after rejection"); await channel.WritePacketAsync(Ok(1));
        }, lobLocators: true);
        using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
        try
        {
            await connection.OpenAsync();
            var type = await connection.GetComplexTypeAsync("Owner", "Thing");
            using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
            using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
            var value = type.CreateValue(lob);
            if (fault == "stale") connection.ClearComplexTypeCache();
            if (fault == "disposed") lob.Dispose();
            using var command = (OceanBaseCommand)connection.CreateCommand();
            command.CommandType = procedure ? CommandType.StoredProcedure : CommandType.Text;
            command.CommandText = procedure ? "consume" : "BEGIN consume(:p, :n); END;";
            command.Parameters.Add(new OceanBaseParameter
            {
                ParameterName = "p",
                ComplexType = type,
                Value = value,
                Direction = fault == "complex-inout" ? ParameterDirection.InputOutput : ParameterDirection.Input
            });
            var output = new OceanBaseParameter { ParameterName = "n", Direction = ParameterDirection.Output, Value = 9L };
            command.Parameters.Add(output);
            Func<Task> execute = async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); };
            if (fault == "stale") await Assert.ThrowsAsync<InvalidOperationException>(execute);
            else if (fault == "disposed") await Assert.ThrowsAsync<ObjectDisposedException>(execute);
            else await Assert.ThrowsAsync<NotSupportedException>(execute);
            Assert.False(command.HasSentSql); Assert.Equal(9L, output.Value);
            using var after = connection.CreateCommand(); after.CommandText = "after rejection";
            Assert.Equal(1, await after.ExecuteNonQueryAsync());
        }
        catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
        finally { connection.Close(); server.Stop(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
