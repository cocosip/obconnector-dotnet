using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData((byte)0)] [InlineData((byte)3)] [InlineData((byte)246)]
        public async Task AnonymousNumberOutputMatchesCanonicalElementLayout(byte element)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("NUMBER");
                var parameters = new OceanBaseParameterCollection();
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Direction = ParameterDirection.Output, Value = "old" }; parameters.Add(parameter);
                var plan = OracleCommandPlan.Create("pkg.p", CommandType.StoredProcedure, parameters);
                var column = ResultColumn.Parse(ComplexColumn("", "", element: element), OceanBaseCompatibilityMode.Oracle);
                plan.ValidateOutputColumn(0, column); Assert.Equal(element, column.ComplexMetadata!.ElementType);
                Assert.Same(type, column.ComplexType);
                plan.CaptureOutput(new[] { column }, new[] { new byte[] { 1, 0, 2, 52, 50 } });
                Assert.Equal("old", parameter.Value); plan.PublishOutputs();
                var value = Assert.IsType<OceanBaseComplexValue>(parameter.Value); Assert.Same(type, value.Type);
                Assert.Equal("42", Assert.Single(value.GetValues())!.ToString());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, ParameterDirection.Output)] [InlineData(true, ParameterDirection.Output)]
        [InlineData(false, ParameterDirection.InputOutput)] [InlineData(true, ParameterDirection.InputOutput)]
        [InlineData(false, ParameterDirection.ReturnValue)] [InlineData(true, ParameterDirection.ReturnValue)]
        public async Task AnonymousOutputRejectsStaleFactoryBeforeSending(bool async, ParameterDirection direction)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(7)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW"); var value = type.CreateValue(new byte[] { 42 });
                connection.ClearComplexTypeCache(); connection.CreateArrayType("RAW");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Direction = direction }; command.Parameters.Add(parameter);
                foreach (var item in new object[] { value, DBNull.Value })
                {
                    parameter.Value = item;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.False(((OceanBaseCommand)command).HasSentSql); Assert.Equal(ConnectionState.Open, connection.State); Assert.Same(item, parameter.Value);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> AnonymousOutputCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var direction in new[] { ParameterDirection.Output, ParameterDirection.InputOutput, ParameterDirection.ReturnValue })
                    foreach (var name in new[] { "NUMBER", "VARCHAR2", "CHAR", "RAW", "DATE", "TIMESTAMP" })
                        yield return new object[] { async, direction, name };
        }

        [Theory]
        [MemberData(nameof(AnonymousOutputCases))]
        public async Task AnonymousScalarArraysRoundTripAllOutputDirections(bool async, ParameterDirection direction, string name)
        {
            var code = name == "NUMBER" ? (byte)0 : name == "VARCHAR2" ? (byte)253 : name == "CHAR" ? (byte)254 : name == "RAW" ? (byte)203 : name == "DATE" ? (byte)12 : (byte)202;
            var body = Convert.FromHexString(name == "NUMBER" ? "023432" : name == "VARCHAR2" ? "0AE682A3E88085F09F9880" : name == "CHAR" ? "0161" : name == "RAW" ? "0200FF"
                : name == "DATE" ? "07E8070203040506" : "0C1418020304050615CD5B0709");
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0");
                var request = await StatementCommand(channel, 0xa1);
                var input = direction == ParameterDirection.InputOutput;
                var encoded = new byte[] { input ? (byte)0 : (byte)1, 1, 160, 0, 0, 0, 0, code == 0 ? (byte)246 : code }
                    .Concat(input ? new byte[] { 1, 0 }.Concat(body) : Array.Empty<byte>()).ToArray();
                Assert.Equal(OraclePrepareExecutePacket.Build(0, direction == ParameterDirection.ReturnValue ? "BEGIN ? := pkg.p(); END;" : "BEGIN pkg.p(?); END;", 1, encoded), request);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, direction == ParameterDirection.ReturnValue ? 1u : input ? 4u : 3u));
                await channel.WritePacketAsync(ComplexColumn("", "", element: code)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn("WireOwner", "", 17, code)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 2, 8 }.Concat(body).ToArray());
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok(2)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType(name);
                object item = name == "NUMBER" ? 42 : name == "VARCHAR2" ? "患者😀" : name == "CHAR" ? "a" : name == "RAW" ? new byte[] { 0, 255 }
                    : name == "DATE" ? new DateTime(2024, 2, 3, 4, 5, 6) : new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), 123456789, 9);
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = direction, Value = direction == ParameterDirection.InputOutput ? type.CreateValue(item) : (object)"old" };
                if (direction != ParameterDirection.InputOutput) parameter.ComplexType = type;
                command.Parameters.Add(parameter);
                Assert.Equal(2, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                var value = Assert.IsType<OceanBaseComplexValue>(parameter.Value); Assert.Same(type, value.Type);
                var items = value.GetValues(); Assert.Equal(2, items.Length); Assert.Null(items[1]);
                if (name == "NUMBER") Assert.Equal("42", Assert.IsType<OceanBaseDecimal>(items[0]).ToString()); else Assert.Equal(item, items[0]);
                Assert.Equal(2, type.CreateValue(items).GetValues().Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> AnonymousOutputResults()
        {
            foreach (var async in new[] { false, true })
                foreach (var result in new[] { "value", "null", "empty", "zero", "tail-error", "wrong-element", "wrong-null", "wrong-zero", "wrong-named", "wrong-scalar", "unknown-element" })
                    yield return new object[] { async, result };
        }

        [Theory]
        [MemberData(nameof(AnonymousOutputResults))]
        public async Task AnonymousOutputMetadataAndTailControlAtomicPublication(bool async, string result)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mismatch = result.StartsWith("wrong-", StringComparison.Ordinal) || result == "unknown-element";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0");
                var request = await StatementCommand(channel, 0xa1);
                Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN pkg.p(?, ?); END;", 2, new byte[] { 3, 1, 6, 0, 160, 0, 0, 0, 0, 203 }), request);
                await channel.WritePacketAsync(OracleExecuteHeader(2, 2));
                await channel.WritePacketAsync(Column("n", 253)); await channel.WritePacketAsync(ComplexColumn("", "")); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("n", 8));
                await channel.WritePacketAsync(result == "wrong-named" ? ComplexColumn() : result == "wrong-scalar" ? Column("bad", 8)
                    : ComplexColumn("", "", element: result == "unknown-element" ? (byte)160 : mismatch ? (byte)253 : (byte)203));
                if (mismatch) { Assert.Equal(0, await channelStreamRead(channel)); return; }
                await channel.WritePacketAsync(Eof());
                if (result != "zero") await channel.WritePacketAsync(new byte[] { 0, result == "null" ? (byte)8 : (byte)0 }.Concat(BitConverter.GetBytes(42L))
                    .Concat(result == "null" ? Array.Empty<byte>() : result == "empty" ? new byte[] { 0, 0 } : new byte[] { 1, 0, 1, 42 }).ToArray());
                await channel.WritePacketAsync(Eof()); waiting.SetResult(); await release.Task;
                await channel.WritePacketAsync(result == "tail-error" ? new byte[] { 255, 82, 4, 35, 72, 89, 48, 48, 48, 98, 97, 100 } : Ok(2));
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var scalar = new OceanBaseParameter { ParameterName = "n", Direction = ParameterDirection.Output, Value = "scalar-old" };
                var array = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, ComplexType = type, Value = "array-old" };
                command.Parameters.Add(scalar); command.Parameters.Add(array);
                var pending = async ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery());
                if (!mismatch)
                {
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                    Assert.Equal("scalar-old", scalar.Value); Assert.Equal("array-old", array.Value);
                    array.ComplexType = null; release.SetResult();
                }
                if (mismatch || result == "tail-error")
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(mismatch, error.IsExecutionOutcomeUnknown);
                    if (mismatch) Assert.IsType<ProtocolException>(error.InnerException);
                    Assert.Equal("scalar-old", scalar.Value); Assert.Equal("array-old", array.Value);
                }
                else
                {
                    Assert.Equal(2, await pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(result == "zero" ? DBNull.Value : (object)42L, scalar.Value);
                    if (result == "null" || result == "zero") Assert.Equal(DBNull.Value, array.Value);
                    else
                    {
                        var value = Assert.IsType<OceanBaseComplexValue>(array.Value); Assert.Same(type, value.Type);
                        if (result == "empty") Assert.Empty(value.GetValues()); else Assert.Equal(new byte[] { 42 }, Assert.Single(value.GetValues()));
                    }
                }
                if (!mismatch) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync()); }
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task AnonymousOutputCancellationOrCloseBeforeTailKeepsOldValue(bool async, bool close)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1)); await channel.WritePacketAsync(ComplexColumn("", "")); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn("", "")); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, 42 });
                await channel.WritePacketAsync(Eof()); waiting.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, ComplexType = type, Value = "old" }; command.Parameters.Add(parameter);
                var pending = async ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery()); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (close) connection.Close(); else command.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal("old", parameter.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
