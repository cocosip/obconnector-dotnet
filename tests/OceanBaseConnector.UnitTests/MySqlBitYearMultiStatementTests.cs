using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class MySqlBitYearLiteralTests
    {
        public static IEnumerable<object[]> Values()
        {
            yield return new object[] { OceanBaseDbType.Bit, 0UL, 1, (byte)0, "0" };
            yield return new object[] { OceanBaseDbType.Bit, 1UL, 1, (byte)0, "1" };
            yield return new object[] { OceanBaseDbType.Bit, 511UL, 9, (byte)0, "511" };
            yield return new object[] { OceanBaseDbType.Bit, 1UL << 63, 64, (byte)0, "9223372036854775808" };
            yield return new object[] { OceanBaseDbType.Bit, ulong.MaxValue, 0, (byte)0, "18446744073709551615" };
            yield return new object[] { OceanBaseDbType.Bit, ulong.MaxValue, 64, (byte)0, "18446744073709551615" };
            yield return new object[] { OceanBaseDbType.Bit, DBNull.Value, 64, (byte)0, "NULL" };
            yield return new object[] { OceanBaseDbType.Year, (ushort)0, 0, (byte)4, "0" };
            yield return new object[] { OceanBaseDbType.Year, (ushort)1901, 0, (byte)0, "1901" };
            yield return new object[] { OceanBaseDbType.Year, (ushort)2026, 0, (byte)4, "2026" };
            yield return new object[] { OceanBaseDbType.Year, (ushort)2155, 0, (byte)4, "2155" };
            yield return new object[] { OceanBaseDbType.Year, DBNull.Value, 0, (byte)4, "NULL" };
        }

        [Theory]
        [MemberData(nameof(Values))]
        public void MySqlBitYearMultiLiteralsPreserveExactNumbersAndNull(OceanBaseDbType type, object value, int size, byte precision, string literal)
        {
            var parameters = new OceanBaseParameterCollection();
            var parameter = new OceanBaseParameter { OceanBaseDbType = type, Value = value, Size = size, Precision = precision };
            parameters.Add(parameter);
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = type, Value = DBNull.Value, Size = size, Precision = precision });
            Assert.Equal("SELECT (" + literal + "); SELECT (NULL)", ClientPreparedSql.Build(
                ParameterBindingPlan.Create("SELECT ?; SELECT ?", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql, multiStatement: true));
            Assert.Same(value, parameter.Value);
        }

        [Theory]
        [InlineData(OceanBaseDbType.Bit)]
        [InlineData(OceanBaseDbType.Year)]
        public void MySqlBitYearInputsRetainOracleModeRefusals(OceanBaseDbType type)
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = type, Value = DBNull.Value });
            Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(ParameterBindingPlan.Create("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameters), OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(ParameterBindingPlan.Create("SELECT :p FROM DUAL; SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameters), OceanBaseCompatibilityMode.Oracle, multiStatement: true));
        }
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MySqlBitYearMultiStatementsUseOnlyTextAndReuseSession(bool asynchronous, bool deprecated)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await Query(channel, "INSERT INTO t VALUES ((18446744073709551615),(2155)); UPDATE t SET b=(NULL),y=(NULL)", deprecated);
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
                channel.BeginCommand(); await Query(channel, "after bit year multi", deprecated); await channel.WritePacketAsync(Ok(1, 2));
            }, deprecated: deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES (?,?); UPDATE t SET b=?,y=?";
                var bits = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = ulong.MaxValue, Size = 64 };
                var year = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = (ushort)2155, Precision = 4 };
                command.Parameters.Add(bits); command.Parameters.Add(year);
                command.Parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = DBNull.Value, Size = 1 });
                command.Parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = DBNull.Value, Precision = 4 });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ulong.MaxValue, bits.Value); Assert.Equal((ushort)2155, year.Value);
                command.Parameters.Clear(); command.CommandText = "after bit year multi";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlBitYearInvalidDeclarationsSendNoScript(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await Query(channel, "after rejected bit year multi"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES (1); INSERT INTO t VALUES (?)";
                var invalid = new[]
                {
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = 2UL, Size = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = (ushort)1900 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = (ushort)2156 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = 2026 },
                };
                foreach (var parameter in invalid)
                {
                    command.Parameters.Clear(); command.Parameters.Add(parameter);
                    await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                var declarations = new[]
                {
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = DBNull.Value, Size = 65 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = DBNull.Value, Precision = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = DBNull.Value, Scale = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = DBNull.Value, Precision = 2 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = DBNull.Value, Scale = 1 },
                };
                foreach (var parameter in declarations)
                {
                    command.Parameters.Clear(); command.Parameters.Add(parameter);
                    await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                command.Parameters.Clear(); command.CommandText = "after rejected bit year multi";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
