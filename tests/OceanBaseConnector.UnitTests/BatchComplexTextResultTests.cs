using System;
using System.Collections.Generic;
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
        [InlineData(false)] [InlineData(true)]
        public void BatchMultiSendNamedObjectTextUsesExistingScalarDecoder(bool ownerless)
        {
            var type = ComplexResultType(false, ComplexResultScalar("N", 0), ComplexResultScalar("S", 253));
            var types = new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type };
            var column = ResultColumn.Parse(ComplexColumn(ownerless ? "" : "Owner"), OceanBaseCompatibilityMode.Oracle,
                complexTypes: types, textResult: true);
            column.RequireBatchMultiSendResult(binary: false);
            Assert.Equal(DBNull.Value, column.Decode(null));
            var result = Assert.IsType<OceanBaseComplexValue>(column.Decode(Encoding.UTF8.GetBytes("Thing(7,'患者, ''q')")));
            Assert.Same(type, result.Type); Assert.Equal("7", Assert.IsType<OceanBaseDecimal>(result.GetValues()[0]).ToString());
            Assert.Equal("患者, 'q", result.GetValues()[1]);
        }

        [Theory]
        [InlineData(false, "reader")] [InlineData(true, "reader")]
        [InlineData(false, "scalar")] [InlineData(true, "scalar")]
        [InlineData(false, "nonquery")] [InlineData(true, "nonquery")]
        public async Task BatchMultiSendNamedObjectTextMixesWithBinaryAcrossGroups(bool asynchronous, string api)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: "VARCHAR2", collection: false);
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Column("s", 253)); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                channel.BeginCommand(); await Query(channel, "select value from first_table");
                Assert.Equal(90, (await StatementCommand(channel, 23))[1]);
                channel.BeginResponse();
                await BatchObjectTextResponse(channel, "Thing('患者, ''q')");
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("s", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(WithLength(Encoding.UTF8.GetBytes("binary"))).ToArray());
                await channel.WritePacketAsync(Eof());
                channel.BeginCommand(); await Query(channel, "select value from last_table");
                await BatchObjectTextResponse(channel, "Thing(NULL)");
                await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select value from first_table"); BatchItem(batch, "select s from middle_table");
                    BatchItem(batch, "select value from last_table");
                    if (api == "reader")
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            if (i == 1) Assert.Equal("binary", reader.GetString(0));
                            else
                            {
                                Assert.Equal("Thing", reader.GetDataTypeName(0));
                                var value = reader.GetFieldValue<OceanBaseComplexValue>(0); Assert.Same(type, value.Type);
                                Assert.Equal(i == 0 ? "患者, 'q" : null, value.GetValues()[0]);
                                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
                            }
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == "scalar")
                    {
                        var value = Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                        Assert.Same(type, value.Type); Assert.Equal("患者, 'q", value.GetValues()[0]);
                    }
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData((byte)203)] [InlineData((byte)12)] [InlineData((byte)202)]
        [InlineData((byte)210)] [InlineData((byte)211)] [InlineData((byte)163)]
        public void BatchMultiSendNamedObjectTextKeepsUnsupportedMemberRestrictions(byte member)
        {
            var type = ComplexResultType(false, ComplexResultScalar("M", member));
            var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle,
                complexTypes: new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type }, textResult: true);
            Assert.Throws<NotSupportedException>(() => column.RequireBatchMultiSendResult(binary: false, allowLobResults: true));
        }

        [Fact]
        public void BatchMultiSendNamedObjectTextRequiresUnambiguousObjectIdentity()
        {
            var type = ComplexResultType(false, ComplexResultScalar("N", 0));
            var types = new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type, [("Other", "Thing")] = type };
            var ambiguous = ResultColumn.Parse(ComplexColumn(""), OceanBaseCompatibilityMode.Oracle, complexTypes: types, textResult: true);
            Assert.Throws<NotSupportedException>(() => ambiguous.RequireBatchMultiSendResult(binary: false));
            var collection = ComplexResultType(true, ComplexResultScalar("N", 0));
            types[("Owner", "Thing")] = collection;
            var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle, complexTypes: types, textResult: true);
            Assert.Throws<NotSupportedException>(() => column.RequireBatchMultiSendResult(binary: false));
            var anonymous = ResultColumn.Parse(ComplexColumn("", "", element: 0), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => anonymous.RequireBatchMultiSendResult(binary: false));
        }

        private static async Task BatchObjectTextResponse(PacketChannel channel, string value)
        {
            await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(ComplexColumn("", "Thing")); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(WithLength(Encoding.UTF8.GetBytes(value)));
            await channel.WritePacketAsync(new byte[] { 251 }); await channel.WritePacketAsync(Eof());
        }
    }
}
