using System;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("", false)] [InlineData("SYS", false)]
        [InlineData("", true)] [InlineData("sys", true)]
        public void BatchMultiSendXmlUsesExistingStringCodec(string owner, bool binary)
        {
            var column = ResultColumn.Parse(ComplexColumn(owner, "XMLTYPE"), OceanBaseCompatibilityMode.Oracle);
            column.RequireBatchMultiSendResult(binary);
            Assert.Equal(typeof(string), column.FieldType);
            Assert.Equal("XMLTYPE", column.DataTypeName);
            var bytes = Encoding.UTF8.GetBytes("<a>患者🙂</a>");
            Assert.Equal("<a>患者🙂</a>", binary ? column.DecodeBinary(bytes) : column.Decode(bytes));
            Assert.Equal(DBNull.Value, binary ? column.DecodeBinary(null) : column.Decode(null));
            Assert.Throws<DecoderFallbackException>(() => binary ? column.DecodeBinary(new byte[] { 255 }) : column.Decode(new byte[] { 255 }));
            var foreign = ResultColumn.Parse(ComplexColumn("APP", "XMLTYPE"), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => foreign.RequireBatchMultiSendResult(binary));
        }

        [Theory]
        [InlineData(null, "Thing")] [InlineData("APP", "XMLTYPE")]
        public async Task BatchMultiSendXmlDoesNotGuessOmittedTypeIdentity(string? owner, string name)
        {
            var server = Server(async channel =>
            {
                await SourceComplexPrepare(channel, SourceComplexColumn(typeName: ""));
                await SourceTypeLookup(channel, rejectedRow: owner == null, rows: new[] { new string?[] { owner, name } });
                if (owner != null) await StatementCommand(channel, 25);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync();
                using var batch = connection.CreateBatch(); BatchItem(batch, "select x from t");
                var error = await Record.ExceptionAsync(() => batch.ExecuteScalarAsync());
                Assert.NotNull(error);
                if (error is OceanBaseException databaseError)
                {
                    Assert.IsType<NotSupportedException>(databaseError.InnerException);
                    Assert.False(databaseError.IsExecutionOutcomeUnknown);
                }
                else Assert.IsType<NotSupportedException>(error);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "reader", false)] [InlineData(true, "reader", false)]
        [InlineData(false, "scalar", false)] [InlineData(true, "scalar", false)]
        [InlineData(false, "nonquery", false)] [InlineData(true, "nonquery", false)]
        [InlineData(false, "reader", true)] [InlineData(true, "reader", true)]
        [InlineData(false, "sequential", false)] [InlineData(true, "sequential", false)]
        [InlineData(false, "sequential", true)] [InlineData(true, "sequential", true)]
        [InlineData(false, "skip", false)] [InlineData(true, "skip", false)]
        [InlineData(false, "skip", true)] [InlineData(true, "skip", true)]
        public async Task BatchMultiSendXmlReadsAndDrainsBinaryResults(bool asynchronous, string api, bool omittedName)
        {
            var server = Server(async channel =>
            {
                for (byte i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 22);
                    await channel.WritePacketAsync(new byte[] { 0, (byte)(90 + i), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(omittedName ? SourceComplexColumn(table: "Source" + i, typeName: "") : ComplexColumn(i == 0 ? "" : "SYS", "XMLTYPE"));
                    await channel.WritePacketAsync(Eof());
                    if (omittedName)
                    {
                        await SourceTypeLookup(channel, table: "Source" + i, rows: new[] { new string?[] { null, "XMLTYPE" } });
                        await StatementCommand(channel, 25);
                    }
                }
                Assert.Equal(90, (await StatementCommand(channel, 23))[1]);
                Assert.Equal(91, (await StatementCommand(channel, 23))[1]);
                for (var i = 0; i < 2; i++)
                {
                    channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(ComplexColumn(i == 0 ? "" : "SYS", "XMLTYPE"));
                    await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(WithLength(Encoding.UTF8.GetBytes("<a>患者🙂</a>"))).ToArray());
                    await channel.WritePacketAsync(new byte[] { 0, 4 });
                    await channel.WritePacketAsync(Eof());
                }
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select x from first_table"); BatchItem(batch, "select x from second_table");
                    if (api == "reader" || api == "sequential" || api == "skip")
                    {
                        var behavior = api == "reader" ? CommandBehavior.Default : CommandBehavior.SequentialAccess;
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                        for (var i = 0; i < 2; i++)
                        {
                            Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal("XMLTYPE", reader.GetDataTypeName(0));
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            if (api != "skip")
                            {
                                if (api == "sequential" && i == 1)
                                {
                                    using var text = reader.GetTextReader(0); Assert.Equal("<a>患者🙂</a>", asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                                }
                                else Assert.Equal("<a>患者🙂</a>", asynchronous ? await reader.GetFieldValueAsync<string>(0) : reader.GetString(0));
                            }
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i == 0, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == "scalar") Assert.Equal("<a>患者🙂</a>", asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
