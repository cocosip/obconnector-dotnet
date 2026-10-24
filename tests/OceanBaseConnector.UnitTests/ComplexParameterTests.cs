using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexNamedParametersExecuteAndReuseStatementWithTypedNull(bool async, bool collection)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, collection: collection);
                await StatementCommand(channel, 22); await Prepared(channel, 197, 2);
                for (var i = 0; i < 2; i++)
                {
                    var request = await StatementCommand(channel, 23);
                    var expected = new byte[] { 23, 197, 0, 0, 0, 0, 1, 0, 0, 0, i == 0 ? (byte)0 : (byte)1, 1, 160, 0,
                        5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0, 3, 0 }
                        .Concat(i == 1 ? Array.Empty<byte>() : collection ? new byte[] { 1, 0, 1, 42 } : new byte[] { 0, 1, 42 })
                        .Concat(new byte[] { 7, 0, 0, 0 }).ToArray();
                    Assert.Equal(expected, request); await channel.WritePacketAsync(Ok((byte)(i + 1)));
                }
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values(:p, :n)";
                var parameter = new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = collection ? OceanBaseDbType.Array : OceanBaseDbType.Struct };
                command.Parameters.Add(parameter); command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = 7 });
                // Standalone Prepare must not require a value or its type dictionary declaration.
                if (async) await command.PrepareAsync(); else command.Prepare();
                parameter.Value = type.CreateValue(new byte[] { 42 });
                Assert.Equal(1, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                parameter.ComplexType = type; parameter.Value = DBNull.Value;
                Assert.Equal(2, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> ComplexOutputCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var collection in new[] { false, true })
                    foreach (var result in new[] { "value", "null", "zero", "tail-error", "wrong-scalar", "wrong-schema", "wrong-name" })
                        yield return new object[] { async, collection, result };
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
        public async Task ComplexInputOutputInferenceAndFunctionReturnUseNamedHeaders(bool async, bool collection, bool function)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, collection: collection); channel.BeginCommand();
                var request = await StatementCommand(channel, 0xa1);
                var encoded = function ? new byte[] { 1, 1, 6, 0 }
                    : new byte[] { 0, 1, 160, 0, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 }
                        .Concat(collection ? new byte[] { 1, 0, 1, 7 } : new byte[] { 0, 1, 7 }).ToArray();
                Assert.Equal(OraclePrepareExecutePacket.Build(0, function ? "BEGIN ? := pkg.p(); END;" : "BEGIN pkg.p(?); END;", 1, encoded), request);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, function ? 1u : 4u)); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(collection ? new byte[] { 1, 0, 1, 42 } : new byte[] { 0, 1, 42 }).ToArray());
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok(2)); await StatementCommand(channel, 25);
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.InputOutput,
                    Value = function ? (object)"old" : type.CreateValue(new byte[] { 7 }) };
                if (function) parameter.ComplexType = type;
                command.Parameters.Add(parameter); Assert.Equal(2, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                var value = Assert.IsType<OceanBaseComplexValue>(parameter.Value); Assert.Same(type, value.Type); Assert.Equal(new byte[] { 42 }, value.GetValues()[0]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexOutputCancellationOrCloseBeforeTailKeepsOldValue(bool async, bool close)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); channel.BeginCommand(); await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1)); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, 42 });
                await channel.WritePacketAsync(Eof()); waiting.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, ComplexType = type, Value = "old" }; command.Parameters.Add(parameter);
                var pending = async ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery());
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (close) connection.Close(); else command.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal("old", parameter.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [MemberData(nameof(ComplexOutputCases))]
        public async Task ComplexOutputMetadataAndTailControlAtomicPublication(bool async, bool collection, string result)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mismatch = result.StartsWith("wrong-", StringComparison.Ordinal);
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, collection: collection); channel.BeginCommand();
                var request = await StatementCommand(channel, 0xa1);
                var encoded = new byte[] { 3, 1, 6, 0, 6, 0 };
                Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN pkg.p(?, ?); END;", 2, encoded), request);
                await channel.WritePacketAsync(OracleExecuteHeader(2, 2));
                await channel.WritePacketAsync(Column("p", 253)); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("scalar", 8));
                await channel.WritePacketAsync(result == "wrong-scalar" ? Column("bad", 8) : ComplexColumn(result == "wrong-schema" ? "owner" : "Owner", result == "wrong-name" ? "thing" : "Thing"));
                if (mismatch) { Assert.Equal(0, await channelStreamRead(channel)); return; }
                await channel.WritePacketAsync(Eof());
                if (result != "zero")
                    await channel.WritePacketAsync(new byte[] { 0, result == "null" ? (byte)8 : (byte)0 }.Concat(BitConverter.GetBytes(42L))
                        .Concat(result == "null" ? Array.Empty<byte>() : collection ? new byte[] { 1, 0, 1, 42 } : new byte[] { 0, 1, 42 }).ToArray());
                await channel.WritePacketAsync(Eof()); waiting.SetResult(); await release.Task;
                await channel.WritePacketAsync(result == "tail-error" ? new byte[] { 255, 82, 4, 35, 72, 89, 48, 48, 48, 98, 97, 100 } : Ok(2));
                await StatementCommand(channel, 25); channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var scalar = new OceanBaseParameter { ParameterName = "n", Direction = ParameterDirection.Output, Value = "scalar-old" };
                var complex = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, ComplexType = type, Value = "complex-old" };
                command.Parameters.Add(scalar); command.Parameters.Add(complex);
                var pending = async ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery());
                if (!mismatch)
                {
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                    Assert.Equal("scalar-old", scalar.Value); Assert.Equal("complex-old", complex.Value);
                    complex.ComplexType = null; release.SetResult();
                }
                if (mismatch || result == "tail-error")
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(mismatch, error.IsExecutionOutcomeUnknown);
                    Assert.Equal("scalar-old", scalar.Value); Assert.Equal("complex-old", complex.Value);
                }
                else
                {
                    Assert.Equal(2, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(result == "zero" ? DBNull.Value : (object)42L, scalar.Value);
                    if (result == "null" || result == "zero") Assert.Equal(DBNull.Value, complex.Value);
                    else { var value = Assert.IsType<OceanBaseComplexValue>(complex.Value); Assert.Same(type, value.Type); Assert.Equal(new byte[] { 42 }, value.GetValues()[0]); }
                }
                if (!mismatch)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
                }
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, ParameterDirection.Input)] [InlineData(true, ParameterDirection.Input)]
        [InlineData(false, ParameterDirection.Output)] [InlineData(true, ParameterDirection.Output)]
        [InlineData(false, ParameterDirection.ReturnValue)] [InlineData(true, ParameterDirection.ReturnValue)]
        public async Task ComplexParameterInvalidatedCacheIsRejectedBeforePreparingAndKeepsSessionUsable(bool async, ParameterDirection direction)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                var value = type.CreateValue(new byte[] { 42 }); connection.ClearComplexTypeCache();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values(:p)";
                if (direction != ParameterDirection.Input) { command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure; }
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = direction,
                    Value = direction == ParameterDirection.Input ? value : DBNull.Value, ComplexType = type });
                await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(((OceanBaseCommand)command).HasSentSql); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public void ComplexParameterHeadersPrecedeAllValuesAndPreserveTypedNull(bool collection, bool isNull)
        {
            var type = ComplexResultType(collection, ComplexResultScalar("A", 203));
            var value = new OceanBaseComplexValue(type, new object?[] { new byte[] { 42 } });
            var parameter = new OceanBaseParameter { ComplexType = type, Value = isNull ? DBNull.Value : value };
            var encoded = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = 7 }, parameter, new OceanBaseParameter { Value = "z" } }, OceanBaseCompatibilityMode.Oracle);
            var expected = new byte[] { isNull ? (byte)2 : (byte)0, 1, 3, 0, 160, 0, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0, 253, 0, 7, 0, 0, 0 }
                .Concat(isNull ? Array.Empty<byte>() : collection ? new byte[] { 1, 0, 1, 42 } : new byte[] { 0, 1, 42 }).Concat(new byte[] { 1, 122 }).ToArray();
            Assert.Equal(expected, encoded);
            Assert.Same(type, parameter.Snapshot().ComplexType);
            Assert.Equal(collection ? OceanBaseDbType.Array : OceanBaseDbType.Struct, parameter.OceanBaseDbType);
            Assert.Equal(DbType.Object, parameter.DbType);
            if (!isNull) Assert.Equal(encoded, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = 7 }, new OceanBaseParameter { Value = value }, new OceanBaseParameter { Value = "z" } }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData("kind")] [InlineData("value")] [InlineData("identity")] [InlineData("size")]
        [InlineData("precision")] [InlineData("scale")] [InlineData("mysql")] [InlineData("missing")]
        public void ComplexParameterRejectsInvalidDeclarationsBeforeEncoding(string invalid)
        {
            var type = ComplexResultType(false, ComplexResultScalar("A", 203));
            var parameter = new OceanBaseParameter { ComplexType = type, Value = null };
            switch (invalid)
            {
                case "kind": parameter.OceanBaseDbType = OceanBaseDbType.Array; break;
                case "value": parameter.Value = "wrong"; break;
                case "identity": parameter.Value = new OceanBaseComplexValue(ComplexResultType(false, ComplexResultScalar("A", 203)), new object?[] { null }); break;
                case "size": parameter.Size = 1; break;
                case "precision": parameter.Precision = 1; break;
                case "scale": parameter.Scale = 1; break;
                case "missing": parameter.ComplexType = null; parameter.OceanBaseDbType = OceanBaseDbType.Struct; break;
            }
            var mode = invalid == "mysql" ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
            if (invalid == "mysql") Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, mode));
            else Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, mode));
        }

        [Fact]
        public void ComplexParameterTypeResetsFollowScalarParameterContract()
        {
            var type = ComplexResultType(false, ComplexResultScalar("A", 203));
            var parameter = new OceanBaseParameter { ComplexType = type };
            parameter.DbType = DbType.String; Assert.Null(parameter.ComplexType); Assert.Null(parameter.OceanBaseDbType);
            parameter.ComplexType = type; parameter.OceanBaseDbType = OceanBaseDbType.Int32; Assert.Null(parameter.ComplexType);
            parameter.ComplexType = type; parameter.ResetDbType(); Assert.Null(parameter.ComplexType); Assert.Null(parameter.OceanBaseDbType);
            parameter.ComplexType = type; parameter.ComplexType = null; Assert.Null(parameter.OceanBaseDbType);
        }

        [Fact]
        public void ComplexNestedParameterUsesBitZeroAtEachLevelWithoutAnOuterLength()
        {
            var child = ComplexResultType(true, ComplexResultScalar("A", 203));
            var root = ComplexResultType(false, new OceanBaseComplexMember("CHILD", "Owner", "Thing", child, child.Layout), ComplexResultScalar("A", 203));
            var nested = new OceanBaseComplexValue(child, new object?[] { null, new byte[] { 42 } });
            var value = new OceanBaseComplexValue(root, new object?[] { nested, null });
            var expected = new byte[] { 0, 1, 160, 0, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0, 2, 2, 1, 1, 42 };
            Assert.Equal(expected, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle));
        }

        [Fact]
        public void ComplexParameterTypeHeaderUsesUtf8ByteLengthsAndLengthEncodedIdentifiers()
        {
            var type = new OceanBaseComplexType(null!, new string('界', 100), new string('型', 100), false, new[] { ComplexResultScalar("A", 203) });
            var encoded = PreparedParameters.Encode(new[] { new OceanBaseParameter { ComplexType = type } }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(new byte[] { 1, 1, 160, 0, 252, 44, 1 }, encoded.Take(7));
            Assert.Equal(Encoding.UTF8.GetBytes(type.SchemaName), encoded.Skip(7).Take(300));
            Assert.Equal(new byte[] { 252, 44, 1 }, encoded.Skip(307).Take(3));
            Assert.Equal(Encoding.UTF8.GetBytes(type.TypeName), encoded.Skip(310).Take(300));
            Assert.Equal(0, encoded[610]); Assert.Equal(611, encoded.Length);
        }

        [Fact]
        public void ComplexParameterPacketBudgetIncludesEarlierParametersAndTypeHeaders()
        {
            var type = ComplexResultType(false, ComplexResultScalar("A", 203));
            // Each value fits alone; binding both must not grant each a separate packet budget.
            var value = new OceanBaseComplexValue(type, new object?[] { new byte[32 * 1024 * 1024] });
            var parameter = new OceanBaseParameter { Value = value };
            var error = Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter, parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Contains("packet size limit", error.Message);
        }

        [Theory]
        [InlineData(ParameterDirection.Output)] [InlineData(ParameterDirection.ReturnValue)]
        public void ComplexOutputRequiresExplicitTypeInsteadOfInferringFromDiscardedOldValue(ParameterDirection direction)
        {
            var type = ComplexResultType(false, ComplexResultScalar("A", 203));
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = direction, Value = new OceanBaseComplexValue(type, new object?[] { null }) });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("proc", CommandType.StoredProcedure, parameters));
        }

        [Theory]
        [InlineData(false, "success")] [InlineData(true, "success")]
        [InlineData(false, "early-close")] [InlineData(true, "early-close")]
        [InlineData(false, "later-error")] [InlineData(true, "later-error")]
        public async Task ComplexOutputWaitsForAllResultsAndKeepsOldValueOnEarlyCloseOrLaterError(bool async, string outcome)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); channel.BeginCommand(); await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1)); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, 42 });
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok(status: 14));
                if (outcome == "early-close") { Assert.Equal(0, await channelStreamRead(channel)); return; }
                if (outcome == "later-error") await channel.WritePacketAsync(Error());
                else
                {
                    await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("visible", 8)); await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(55L)).ToArray()); await channel.WritePacketAsync(Eof());
                }
                await StatementCommand(channel, 25);
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, ComplexType = type, Value = "old" }; command.Parameters.Add(parameter);
                using var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader(); Assert.Equal(0, reader.FieldCount); Assert.Equal("old", parameter.Value);
                if (outcome == "later-error")
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => async ? reader.NextResultAsync() : Task.FromResult(reader.NextResult()));
                    Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal("old", parameter.Value);
                }
                else if (outcome == "success")
                {
                    Assert.True(async ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal("old", parameter.Value);
                    Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(55L, reader.GetInt64(0));
                    Assert.False(async ? await reader.ReadAsync() : reader.Read()); Assert.Same(type, Assert.IsType<OceanBaseComplexValue>(parameter.Value).Type);
                }
                if (async) await reader.CloseAsync(); else reader.Close();
                if (outcome == "early-close") Assert.Equal("old", parameter.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexParameterRejectsForeignOrReopenedSessionIncludingTypedNull(bool async, bool reopen)
        {
            var server = Server(async channel => await ComplexScalarType(channel));
            var next = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(7)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var foreign = new OceanBaseConnection(Configuration(next.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing"); var value = type.CreateValue(new byte[] { 42 });
                OceanBaseConnection target;
                if (reopen) { connection.Close(); connection.ConnectionString = Configuration(next.Port); await connection.OpenAsync(); target = connection; }
                else { await foreign.OpenAsync(); target = foreign; }
                using var command = target.CreateCommand(); command.CommandText = "insert into t values(:p)";
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value }; command.Parameters.Add(parameter);
                foreach (var isNull in new[] { false, true })
                {
                    parameter.Value = isNull ? DBNull.Value : value;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.False(((OceanBaseCommand)command).HasSentSql); Assert.Equal(ConnectionState.Open, target.State);
                }
                using var after = target.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); foreign.Close(); server.Stop(); next.Stop(); }
            await Task.WhenAll(server.Completion, next.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ComplexParameterRejectsBothTransactionEpochBoundariesWithoutSending(bool async)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); channel.BeginCommand(); await TransactionBegin(channel);
                await ComplexScalarType(channel); channel.BeginCommand(); await TransactionEnd(channel, false);
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var initial = await connection.GetComplexTypeAsync("Owner", "Thing"); var before = initial.CreateValue(new byte[] { 1 });
                using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values(:p)"; command.Transaction = transaction;
                var parameter = new OceanBaseParameter { ParameterName = "p", Value = before }; command.Parameters.Add(parameter);
                await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                var current = await connection.GetComplexTypeAsync("Owner", "Thing", transaction); parameter.Value = current.CreateValue(new byte[] { 2 });
                await transaction.RollbackAsync(); command.Transaction = null;
                await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(((OceanBaseCommand)command).HasSentSql);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(ParameterDirection.Output)] [InlineData(ParameterDirection.InputOutput)] [InlineData(ParameterDirection.ReturnValue)]
        public void ComplexOracleOutputsKeepTheirDeclarationSnapshotAndPublishOnlyOnCompletion(ParameterDirection direction)
        {
            var type = ComplexResultType(false, ComplexResultScalar("A", 203));
            var parameter = new OceanBaseParameter { ParameterName = "p", Direction = direction, ComplexType = type,
                Value = direction == ParameterDirection.InputOutput ? new OceanBaseComplexValue(type, new object?[] { new byte[] { 7 } }) : (object)"old" };
            var original = parameter.Value;
            var parameters = new OceanBaseParameterCollection(); parameters.Add(parameter);
            var plan = OracleCommandPlan.Create("proc", CommandType.StoredProcedure, parameters);
            Assert.Same(type, plan.Bindings.Parameters[0].ComplexType);
            var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle,
                complexTypes: new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type });
            parameter.ComplexType = ComplexResultType(true, ComplexResultScalar("A", 203));
            plan.CaptureOutput(new[] { column }, new byte[]?[] { new byte[] { 0, 1, 42 } });
            Assert.Same(original, parameter.Value);
            plan.PublishOutputs();
            var value = Assert.IsType<OceanBaseComplexValue>(parameter.Value); Assert.Same(type, value.Type);
            Assert.Equal(new byte[] { 42 }, value.GetValues()[0]);
        }
    }
}
