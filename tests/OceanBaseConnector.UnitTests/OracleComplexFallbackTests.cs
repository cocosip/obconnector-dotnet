using System;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void OracleComplexFallbackReusesNamedConstructorsAndTypedNull(bool collection)
        {
            var type = ComplexResultType(collection, ComplexResultScalar("A", 203));
            var parameters = new OceanBaseParameterCollection();
            var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type,
                Value = new OceanBaseComplexValue(type, new object?[] { new byte[] { 0, 39, 255 } }) };
            parameters.Add(parameter);
            var bindings = ParameterBindingPlan.Create("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameters);
            Assert.Equal("SELECT (\"Owner\".\"Thing\"(HEXTORAW('0027FF'))) FROM DUAL", ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle));
            parameter.Value = DBNull.Value;
            Assert.Equal("SELECT (NULL) FROM DUAL", ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task OracleComplexFallbackUsesUnicodeConstructorsWithoutUnusedBinaryEncoding(bool asynchronous, bool collection)
        {
            var server = Server(async channel =>
            {
                await ComplexNationalDictionary(channel, "NVARCHAR2", collection: collection);
                for (var i = 0; i < 3; i++)
                {
                    await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                    channel.BeginCommand(); await Query(channel, "UPDATE t SET v=" +
                        (i == 1 ? "(NULL)" : "(\"Owner\".\"Thing\"(u'\\4e2d'))"));
                    await channel.WritePacketAsync(Ok(1));
                }
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, nationalCharacterSet: "unsupported");
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.AllowClientSidePrepareFallback = true;
                command.CommandText = "UPDATE t SET v=:p";
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type }; command.Parameters.Add(parameter);
                for (var i = 0; i < 3; i++)
                {
                    parameter.Value = i == 1 ? DBNull.Value : type.CreateValue("中");
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                command.Parameters.Clear(); command.CommandText = "after"; command.AllowClientSidePrepareFallback = false;
                Assert.Equal(7, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
