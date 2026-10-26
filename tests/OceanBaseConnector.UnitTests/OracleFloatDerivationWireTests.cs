using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, "FLOAT")]
        [InlineData(true, "FLOAT")]
        [InlineData(false, "NUMBER_FLOAT")]
        [InlineData(true, "NUMBER_FLOAT")]
        public async Task DeriveOracleFloatFunctionSignatureKeepsDirectionsExactBindingsAndAtomicFailure(bool asynchronous, string type)
        {
            using var routines = DerivationRoutines(false, "FUNCTION");
            using var arguments = DerivationArguments(false);
            arguments.Rows.Clear();
            for (var i = 3; i >= 0; i--)
            {
                var row = arguments.NewRow(); row["ROUTINE_SCHEMA"] = "OWNER"; row["ROUTINE_NAME"] = "PROC"; row["SUBPROGRAM_ID"] = 1;
                row["PARAMETER_NAME"] = i == 0 ? DBNull.Value : "p" + i;
                row["ORDINAL_POSITION"] = i; row["PARAMETER_MODE"] = new[] { "RETURN", "IN", "INOUT", "OUT" }[i];
                row["DATA_TYPE"] = type; row["NUMERIC_PRECISION"] = new[] { 126, 53, 24, 1 }[i];
                row["NUMERIC_SCALE"] = i == 1 ? (object)(-85) : i == 2 ? -1 : DBNull.Value;
                arguments.Rows.Add(row);
            }
            using var invalid = arguments.Copy(); invalid.Rows[0]["NUMERIC_PRECISION"] = 127;
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" });
                Assert.Equal(new byte[] { 25, 72, 0, 0, 0 }, await StatementCommand(channel, 25));
                var expected = new[] { new OceanBaseParameter { Value = DBNull.Value }, new OceanBaseParameter { Value = new OceanBaseDecimal(BigInteger.One, -125) },
                    new OceanBaseParameter { Value = new OceanBaseDecimal(7.2500m) }, new OceanBaseParameter { Value = DBNull.Value } };
                Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN ? := owner.proc(?, ?, ?); END;", 4, PreparedParameters.Encode(expected, OceanBaseCompatibilityMode.Oracle)),
                    await StatementCommand(channel, 0xa1));
                await channel.WritePacketAsync(OracleExecuteHeader(4, 3));
                for (var i = 0; i < 4; i++) await channel.WritePacketAsync(ValueColumnPacket(206, 126, 171));
                await channel.WritePacketAsync(Eof());
                for (var i = 0; i < 3; i++) await channel.WritePacketAsync(ValueColumnPacket(206, 126, 171));
                await channel.WritePacketAsync(Eof());
                using (var row = new MemoryStream())
                {
                    row.Write(new byte[] { 0, 0 });
                    foreach (var value in new[] { "7.2500", "8.5000", "700" })
                    { var bytes = Encoding.ASCII.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
                    await channel.WritePacketAsync(row.ToArray());
                }
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok());
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                await ServeDerivationTable(channel, 73, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 74, invalid, false, true, new object[] { 1, "OWNER", "PROC" });
                channel.BeginCommand(); Assert.Equal(new byte[] { 25, 74, 0, 0, 0 }, await channel.ReadPacketAsync());
                var request = await StatementCommand(channel, 3);
                Assert.Equal("reuse", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc" };
                var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
                if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
                var parameters = command.Parameters.Cast<OceanBaseParameter>().ToArray();
                Assert.Null(original.Owner); Assert.Equal(4, parameters.Length);
                Assert.Equal(new[] { "RETURN_VALUE", "p1", "p2", "p3" }, parameters.Select(p => p.ParameterName));
                Assert.Equal(new[] { ParameterDirection.ReturnValue, ParameterDirection.Input, ParameterDirection.InputOutput, ParameterDirection.Output }, parameters.Select(p => p.Direction));
                Assert.All(parameters, p => { Assert.Equal(OceanBaseDbType.Decimal, p.OceanBaseDbType); Assert.Equal(0, p.Precision); Assert.Null(p.DeclaredDecimalScale); Assert.Equal(DBNull.Value, p.Value); });
                parameters[1].Value = new OceanBaseDecimal(BigInteger.One, -125); parameters[2].Value = new OceanBaseDecimal(7.2500m);
                var plan = OracleCommandPlan.Create(command.CommandText, command.CommandType, (OceanBaseParameterCollection)command.Parameters);
                Assert.Equal("BEGIN ? := owner.proc(?, ?, ?); END;", plan.Bindings.CommandText);
                Assert.Equal(new[] { parameters[0], parameters[2], parameters[3] }, plan.Outputs);
                foreach (var parameter in parameters)
                    Assert.Equal(PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = parameter.Value, OceanBaseDbType = OceanBaseDbType.Decimal, Direction = parameter.Direction } }, OceanBaseCompatibilityMode.Oracle),
                        PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(new OceanBaseDecimal(7.2500m), parameters[0].Value);
                Assert.Equal(new OceanBaseDecimal(8.5000m), parameters[2].Value);
                Assert.Equal(new OceanBaseDecimal(700m), parameters[3].Value);
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.DeriveParametersAsync());
                else Assert.Throws<NotSupportedException>(() => command.DeriveParameters());
                Assert.Equal(parameters, command.Parameters.Cast<OceanBaseParameter>());
                Assert.All(parameters, p => Assert.Same(command.Parameters, p.Owner));
                Assert.Equal(new OceanBaseDecimal(8.5000m), parameters[2].Value);
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse";
                Assert.Equal(1, asynchronous ? await reuse.ExecuteNonQueryAsync() : reuse.ExecuteNonQuery());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "VARCHAR2", "valid")]
        [InlineData(true, "VARCHAR2", "valid")]
        [InlineData(false, "VARCHAR", "valid")]
        [InlineData(true, "VARCHAR", "valid")]
        [InlineData(false, "CHAR", "valid")]
        [InlineData(true, "CHAR", "valid")]
        [InlineData(false, "NCHAR", "valid")]
        [InlineData(true, "NCHAR", "valid")]
        [InlineData(false, "NVARCHAR2", "valid")]
        [InlineData(true, "NVARCHAR2", "valid")]
        [InlineData(false, "VARCHAR2", "changed")]
        [InlineData(true, "VARCHAR2", "changed")]
        [InlineData(false, "VARCHAR2", "missing")]
        [InlineData(true, "VARCHAR2", "missing")]
        public async Task OracleDerivationLoadsCharacterLengthsOnlyForCharacterArgumentsAndKeepsSignaturesAtomic(bool asynchronous, string type, string shape)
        {
            using var routines = DerivationRoutines(false); using var arguments = DerivationArguments(false);
            var first = arguments.Rows[0]; first["DATA_TYPE"] = type; first["DATA_LENGTH"] = 12L;
            first["NUMERIC_PRECISION"] = first["NUMERIC_SCALE"] = DBNull.Value;
            using var lengths = arguments.Copy(); lengths.Rows[0]["CHARACTER_LENGTH"] = 4L;
            if (shape == "changed") lengths.Rows[0]["DATA_LENGTH"] = 16L;
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" });
                async Task<bool> Missing()
                {
                    await channel.WritePacketAsync(new byte[] { 255, 136, 3, 35, 52, 50, 48, 48, 48 }.Concat(Encoding.UTF8.GetBytes("invalid identifier CHAR_LENGTH")).ToArray());
                    return false;
                }
                await ServeDerivationTable(channel, 73, lengths, false, true, new object[] { 1, "OWNER", "PROC" }, shape == "missing" ? Missing : null, characterLengths: true);
                Assert.Equal(new byte[] { 25, 73, 0, 0, 0 }, await StatementCommand(channel, 25));
                var request = await StatementCommand(channel, 3); Assert.Equal("reuse", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(Ok(1)); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc" };
                var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
                async Task Derive() { if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters(); }
                if (shape == "valid")
                {
                    await Derive(); var parameter = Assert.IsType<OceanBaseParameter>(Assert.Single(command.Parameters.Cast<OceanBaseParameter>()));
                    Assert.Equal(4, parameter.Size); Assert.NotSame(original, parameter); Assert.Null(original.Owner);
                }
                else
                {
                    if (shape == "changed") await Assert.ThrowsAsync<NotSupportedException>(Derive);
                    else Assert.Equal(904, (await Assert.ThrowsAsync<OceanBaseException>(Derive)).ServerErrorCode);
                    Assert.Same(original, Assert.Single(command.Parameters.Cast<OceanBaseParameter>())); Assert.Same(command.Parameters, original.Owner); Assert.Equal(42, original.Value);
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse";
                Assert.Equal(1, asynchronous ? await reuse.ExecuteNonQueryAsync() : reuse.ExecuteNonQuery());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
