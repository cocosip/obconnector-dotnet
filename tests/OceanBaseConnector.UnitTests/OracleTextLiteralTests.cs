using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OracleTextLiteralBuilderTests
    {
        [Fact]
        public void OracleOrdinaryTextUsesConnectorJValueSeparatorForLongNul()
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = string.Concat(Enumerable.Repeat("a\0b", 65536)) });
            var sql = ClientPreparedSql.Build(ParameterBindingPlan.Create("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameters), OceanBaseCompatibilityMode.Oracle);
            Assert.StartsWith("SELECT (REPLACE( '", sql);
        }

        public static IEnumerable<object[]> Values()
        {
            foreach (var multi in new[] { false, true })
                foreach (var value in new[] { "", "x ", "'\\\0;:p -- 中🙂", new string('\'', 3000), new string('中', 2000), new string('x', 1048576) })
                    yield return new object[] { multi, value };
        }

        [Theory]
        [MemberData(nameof(Values))]
        public void OrdinaryOracleTextKeepsVarcharIdentityWithoutFixedLength(bool multi, string value)
        {
            var parameters = new OceanBaseParameterCollection();
            var parameter = new OceanBaseParameter { ParameterName = "p", Value = value }; parameters.Add(parameter);
            var literal = "(REPLACE( '" + value.Replace("'", "''") + "',NULL,NULL))";
            var sql = multi ? "SELECT :p FROM DUAL; SELECT :P FROM DUAL" : "SELECT :p FROM DUAL";
            Assert.Equal("SELECT " + literal + " FROM DUAL" + (multi ? "; SELECT " + literal + " FROM DUAL" : ""),
                ClientPreparedSql.Build(ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, parameters), OceanBaseCompatibilityMode.Oracle, multiStatement: multi));
            Assert.Same(value, parameter.Value);
        }

        [Fact]
        public void RepeatedOracleTextQuotesRetainExpandedSqlBudget()
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = new string('\'', 1048576) });
            var sql = string.Join("; ", Enumerable.Repeat("SELECT :p FROM DUAL", 33));
            Assert.Throws<ArgumentException>(() => ClientPreparedSql.Build(ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, parameters),
                OceanBaseCompatibilityMode.Oracle, multiStatement: true));
        }
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(false, true, 0)]
        [InlineData(true, false, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, false, 1)]
        [InlineData(false, true, 1)]
        [InlineData(true, false, 1)]
        [InlineData(true, true, 1)]
        [InlineData(false, false, 2)]
        [InlineData(false, true, 2)]
        [InlineData(true, false, 2)]
        [InlineData(true, true, 2)]
        public async Task OracleOrdinaryTextLiteralsPreserveValueSnapshotsAndSession(bool fallback, bool asynchronous, int sample)
        {
            var text = sample == 0 ? "'\\\0;:p -- 中🙂 " : sample == 1 ? new string('中', 8192) : new string('\'', 65536);
            var literal = "(REPLACE( '" + text.Replace("'", "''") + "',NULL,NULL))";
            OceanBaseParameter? input = null;
            var server = Server(async channel =>
            {
                if (fallback)
                {
                    await StatementCommand(channel, 22);
                    input!.Value = new byte[] { 99 }; input.DbType = DbType.Binary;
                    await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand();
                }
                await Query(channel, "UPDATE t SET v=" + literal + (fallback ? "" : "; UPDATE t SET v=" + literal));
                await channel.WritePacketAsync(Ok(2, fallback ? (byte)2 : (byte)10));
                if (!fallback) await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after ordinary oracle text"); await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = fallback;
                command.CommandText = "UPDATE t SET v=:p" + (fallback ? "" : "; UPDATE t SET v=:P");
                input = new OceanBaseParameter { ParameterName = "p", Value = text }; command.Parameters.Add(input);
                Assert.Equal(fallback ? 2 : 5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                command.Parameters.Clear(); command.CommandText = "after ordinary oracle text";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
