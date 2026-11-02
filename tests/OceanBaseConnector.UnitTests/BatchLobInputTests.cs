using System;
using System.Data;
using System.Linq;
using System.Text;
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
        public async Task BatchMultiSendLobInputsPreserveLocatorsAndCallerOwnership(bool asynchronous, bool character, bool external)
        {
            var data = Encoding.UTF8.GetBytes("患者🙂");
            var locator = external ? ExternalLob(data.Length) : InlineLob(data);
            var type = character ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, type, locator);
                for (byte id = 90; id < 93; id++)
                {
                    await StatementCommand(channel, 22); await Prepared(channel, id, 1);
                }
                // Withhold responses to prove locator inputs need no intervening LOB request.
                for (byte i = 0; i < 3; i++)
                {
                    var request = await StatementCommand(channel, 23);
                    Assert.Equal(new byte[] { 23, (byte)(90 + i), 0, 0, 0, 0, 1, 0, 0, 0,
                        i == 1 ? (byte)1 : (byte)0, 1, i == 1 ? (byte)6 : type, 0 }
                        .Concat(i == 1 ? Array.Empty<byte>() : WithLength(locator)), request);
                }
                for (var i = 0; i < 3; i++)
                {
                    channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                }
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 25);
                channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=3");
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var item = BatchItem(batch, "insert into t values(:p)");
                        var parameter = new OceanBaseParameter { ParameterName = "p", Value = i == 1 ? DBNull.Value : lob };
                        if (i != 2) parameter.OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                        item.Parameters.Add(parameter);
                    }
                    Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
                }
                Assert.Equal(locator, lob.BindingBytes());
                Assert.Equal(data.Length, lob.ByteLength);
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
