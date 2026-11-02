using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        [InlineData(false, true, true)] [InlineData(true, true, true)]
        public async Task BatchMultiSendNamedComplexInputsPreserveWireAndResponseOrder(bool asynchronous, bool collection, bool fail)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, collection: collection);
                for (byte id = 90; id < 93; id++)
                {
                    await StatementCommand(channel, 22); await Prepared(channel, id, 1);
                }
                // The server withholds every response until the complete group arrives.
                for (byte i = 0; i < 3; i++)
                {
                    var request = await StatementCommand(channel, 23);
                    var header = i == 1 ? new byte[] { 6, 0 }
                        : new byte[] { 160, 0, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 };
                    var payload = i == 1 ? Array.Empty<byte>()
                        : (collection ? new byte[] { 1, 0, 1 } : new byte[] { 0, 1 }).Concat(new byte[] { (byte)(42 + i) }).ToArray();
                    Assert.Equal(new byte[] { 23, (byte)(90 + i), 0, 0, 0, 0, 1, 0, 0, 0, i == 1 ? (byte)1 : (byte)0, 1 }
                        .Concat(header).Concat(payload), request);
                }
                channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                channel.BeginResponse(); await channel.WritePacketAsync(fail ? Error() : Ok(2));
                if (fail)
                {
                    try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
                    return;
                }
                channel.BeginResponse(); await channel.WritePacketAsync(Ok(3));
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 25);
                channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=3");
            try
            {
                await connection.OpenAsync();
                var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var item = BatchItem(batch, "insert into t values(:p)");
                        item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type,
                            Value = i == 1 ? DBNull.Value : type.CreateValue(new byte[] { (byte)(42 + i) }) });
                    }
                    if (fail)
                    {
                        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                        Assert.Same(batch.BatchCommands[1], error.BatchCommand);
                        Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
                        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);
                        Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected);
                        Assert.Equal(ConnectionState.Broken, connection.State);
                    }
                    else
                    {
                        Assert.Equal(6, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                        Assert.Equal(new[] { 1, 2, 3 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(c => c.RecordsAffected));
                    }
                }
                if (!fail)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after";
                    Assert.Equal(7, await after.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "BLOB")] [InlineData(true, "BLOB")]
        [InlineData(false, "CLOB")] [InlineData(true, "CLOB")]
        public async Task BatchMultiSendRejectsNamedLobMembersBeforePreparing(bool asynchronous, string scalar)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: scalar, collection: false);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync();
                var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var batch = connection.CreateBatch();
                var item = BatchItem(batch, "insert into t values(:p)");
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = DBNull.Value });
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync());
                else Assert.Throws<NotSupportedException>(() => batch.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
