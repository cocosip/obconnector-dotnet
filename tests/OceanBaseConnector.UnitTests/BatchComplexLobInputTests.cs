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

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendNamedLobMembersUseConstructorLocatorBindings(bool asynchronous, bool character)
        {
            var locator = InlineLob(new byte[] { 1, 2, 3 });
            var typeCode = character ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: character ? "CLOB" : "BLOB", collection: false);
                channel.BeginCommand(); await LobQueryReply(channel, typeCode, locator);
                for (byte id = 90; id < 92; id++)
                {
                    var prepare = await StatementCommand(channel, 22);
                    Assert.Equal("insert into t values(\"Owner\".\"Thing\"(?))", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    await Prepared(channel, id, 1);
                }
                for (byte i = 0; i < 2; i++)
                {
                    var request = await StatementCommand(channel, 23);
                    Assert.Equal(new byte[] { 23, (byte)(90 + i), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, typeCode, 0 }
                        .Concat(WithLength(locator)).ToArray(), request);
                    channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                }
                for (var i = 0; i < 2; i++) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await source.ExecuteScalarAsync())!;
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var item = BatchItem(batch, "insert into t values(:p)");
                        item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = type.CreateValue(lob) });
                    }
                    Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
                }
                Assert.Equal(locator, lob.BindingBytes());
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            catch
            {
                if (server.Completion.IsFaulted) await server.Completion;
                throw;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendNestedCollectionLobLocatorsUseRecursiveConstructors(bool asynchronous)
        {
            var locator = InlineLob(new byte[] { 1, 2, 3 });
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 72, "Owner", "Leaf_List", true,
                    new[] { "Owner", "Leaf_List", "COLLECTION", null });
                await StatementCommand(channel, 25);
                var collectionMembers = await StatementCommand(channel, 22);
                Assert.Contains("ALL_COLL_TYPES", Encoding.UTF8.GetString(collectionMembers));
                await Prepared(channel, 73, 2);
                await StatementCommand(channel, 23);
                await ComplexDictionaryRows(channel, ComplexMemberNames,
                    new[] { (string?)null, "1", "Owner", "Leaf", "OBJECT" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 74, "Owner", "Leaf", true,
                    new[] { "Owner", "Leaf", "OBJECT", "2" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 75, "Owner", "Leaf", false,
                    new[] { "A", "1", null, "BLOB", "BLOB" },
                    new[] { "N", "2", null, "NUMBER", "NUMBER" });
                await StatementCommand(channel, 25);
                channel.BeginCommand(); await LobQueryReply(channel, 210, locator);

                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("insert into t values(\"Owner\".\"Leaf_List\"(\"Owner\".\"Leaf\"(?,?)))",
                    Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 76, 2);
                prepare = await StatementCommand(channel, 22);
                Assert.Equal("insert into t values(\"Owner\".\"Leaf_List\"(\"Owner\".\"Leaf\"(?,?)))",
                    Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 77, 2);
                var first = await StatementCommand(channel, 23);
                Assert.Equal(76, first[1]); await channel.WritePacketAsync(Ok(1));
                var second = await StatementCommand(channel, 23);
                Assert.Equal(77, second[1]); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 76, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 77, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                var type = await connection.GetComplexTypeAsync("Owner", "Leaf_List");
                var leaf = type.Members[0].ComplexType!;
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await source.ExecuteScalarAsync())!;
                var value = type.CreateValue(leaf.CreateValue(lob, 7));
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var item = BatchItem(batch, "insert into t values(:p)");
                        item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value });
                    }
                    Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
                }
                Assert.Equal(locator, lob.BindingBytes());
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            catch
            {
                if (server.Completion.IsFaulted) await server.Completion;
                throw;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
