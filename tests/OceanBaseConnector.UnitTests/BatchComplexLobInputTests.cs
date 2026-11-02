using System;
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
        public async Task BatchMultiSendNamedLobMembersUseInlinePayloads(bool asynchronous, bool collection, bool character)
        {
            var payload = character ? Encoding.UTF8.GetBytes("患者🙂\0tail") : new byte[] { 0, 128, 255 };
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: character ? "CLOB" : "BLOB", collection: collection);
                for (byte id = 90; id < 94; id++) { await StatementCommand(channel, 22); await Prepared(channel, id, 1); }
                for (byte i = 0; i < 4; i++)
                {
                    var type = i == 1 ? new byte[] { 6, 0 }
                        : new byte[] { 160, 0, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 };
                    var body = i == 1 ? Array.Empty<byte>()
                        : i == 2 ? (collection ? new byte[] { 1, 1 } : new byte[] { 1 })
                        : i == 3 ? (collection ? new byte[] { 0 } : new byte[] { 0, 0 })
                        : (collection ? new byte[] { 1, 0 } : new byte[] { 0 }).Concat(WithLength(payload)).ToArray();
                    Assert.Equal(new byte[] { 23, (byte)(90 + i), 0, 0, 0, 0, 1, 0, 0, 0, i == 1 ? (byte)1 : (byte)0, 1 }
                        .Concat(type).Concat(body), await StatementCommand(channel, 23));
                }
                for (var i = 0; i < 4; i++) { channel.BeginResponse(); await channel.WritePacketAsync(Ok(1)); }
                for (var i = 0; i < 4; i++) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=4");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                object value = character ? Encoding.UTF8.GetString(payload) : payload;
                object empty = character ? "" : Array.Empty<byte>();
                using (var batch = connection.CreateBatch())
                {
                    var values = new object[] { type.CreateValue(value), DBNull.Value, type.CreateValue((object?)null),
                        collection ? type.CreateValue(Array.Empty<object?>()) : type.CreateValue(empty) };
                    foreach (var input in values)
                    {
                        var item = BatchItem(batch, "insert into t values(:p)");
                        item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = input });
                    }
                    Assert.Equal(4, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
                }
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
