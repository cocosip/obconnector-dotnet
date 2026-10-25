using OceanBaseConnector.UnitTests.TestInfrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using System.Threading;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static LoopbackServer IdentityProbeServer(Func<PacketChannel, Task> exchange) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
            await channel.ReadPacketAsync(); await channel.WritePacketAsync(Ok()); channel.BeginCommand();
            await Query(channel, "select ob_version(), @@ob_compatibility_mode from dual");
            await exchange(channel);
        });

        private static async Task OracleOutputReply(PacketChannel channel, bool deprecated, uint flags = 4, bool rows = true, bool extraRow = false, bool metadataOutput = false)
        {
            await channel.WritePacketAsync(OracleExecuteHeader(2, 1, flags));
            await channel.WritePacketAsync(Column("in", 8)); await channel.WritePacketAsync(Column("out", 8));
            if (!deprecated) await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(Column("output", 8));
            if (!deprecated) await channel.WritePacketAsync(metadataOutput ? new byte[] { 254, 0, 0, 0, 16 } : Eof());
            if (rows) await channel.WritePacketAsync(new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 });
            if (extraRow) await channel.WritePacketAsync(new byte[] { 0, 0, 43, 0, 0, 0, 0, 0, 0, 0 });
            var end = deprecated ? Ok() : Eof(); if (deprecated) end[0] = 254;
            await channel.WritePacketAsync(end);
        }

        public static IEnumerable<object[]> OraclePublicExecutionCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var sequential in new[] { false, true })
                    foreach (var deprecated in new[] { false, true })
                        foreach (var function in new[] { false, true })
                            foreach (var metadataOutput in new[] { false, true })
                                if (!deprecated || !metadataOutput) yield return new object[] { async, sequential, deprecated, function, metadataOutput };
        }

        [Theory]
        [MemberData(nameof(OraclePublicExecutionCases))]
        public async Task OracleProcedurePublishesOnlyAfterTailAndReusesVersionAndClosesStatements(bool async, bool sequential, bool deprecated, bool function, bool metadataOutput)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sql = function ? "BEGIN ? := pkg.func(?); END;" : "BEGIN pkg.proc(?, ?); END;";
            var encoded = function ? new byte[] { 1, 1, 6, 0, 8, 0, 7, 0, 0, 0, 0, 0, 0, 0 }
                : new byte[] { 2, 1, 8, 0, 6, 0, 7, 0, 0, 0, 0, 0, 0, 0 };
            var server = Server(async channel =>
            {

                for (var i = 0; i < 2; i++)
                {
                    var request = await StatementCommand(channel, 0xa1, deprecated);
                    Assert.Equal(OraclePrepareExecutePacket.Build(0, sql, 2, encoded), request);
                    await OracleOutputReply(channel, deprecated, flags: metadataOutput ? 0u : 4u, metadataOutput: metadataOutput);
                    if (i == 0) { waiting.SetResult(); await release.Task; }
                    await channel.WritePacketAsync(Ok(2));
                    var close = await StatementCommand(channel, 25, deprecated); Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, close);
                }
                channel.BeginCommand(); await Query(channel, "after", deprecated); await channel.WritePacketAsync(Ok(1));
            }, deprecated, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = function ? "pkg.func" : "pkg.proc";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "output", Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.Output, Value = "old" };
                command.Parameters.Add(output);
                var executing = Task.Run(async () =>
                {
                    using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.Equal(0, reader.FieldCount); Assert.False(async ? await reader.ReadAsync() : reader.Read());
                    Assert.False(async ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(2, reader.RecordsAffected);
                });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal("old", output.Value);
                release.SetResult(); await executing.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(42L, output.Value);
                output.Value = "old again";
                Assert.Equal(2, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); Assert.Equal(42L, output.Value);
                if (async) await command.DisposeAsync(); else command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "42", true)]
        [InlineData(true, "42", true)]
        [InlineData(false, "1.2300", true)]
        [InlineData(true, "1.2300", true)]
        [InlineData(false, "12345678901234567890123456789012345678", true)]
        [InlineData(true, "12345678901234567890123456789012345678", true)]
        [InlineData(false, null, true)]
        [InlineData(true, null, true)]
        [InlineData(false, "1e126", false)]
        [InlineData(true, "1e126", false)]
        [InlineData(false, "1e-131", false)]
        [InlineData(true, "1e-131", false)]
        public async Task OracleUnconstrainedNumericOutputPreservesExactValuesAndModeLimits(bool async, string? text, bool valid)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 0));
                await channel.WritePacketAsync(Column("p", 6)); await channel.WritePacketAsync(Eof(64));
                await channel.WritePacketAsync(ValueColumnPacket(246, 0, 255));
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 0, 16 });
                await channel.WritePacketAsync(text == null ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(WithLength(Encoding.ASCII.GetBytes(text))).ToArray());
                if (valid)
                {
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok());
                    await StatementCommand(channel, 25); channel.BeginCommand();
                    await Query(channel, "after number output"); await channel.WritePacketAsync(Ok(1));
                }
                else await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "BEGIN :result := 42; END;";
                var output = new OceanBaseParameter { ParameterName = "result", DbType = DbType.Decimal, Direction = ParameterDirection.Output, Value = "old" };
                command.Parameters.Add(output);
                if (valid)
                {
                    Assert.Equal(0, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    if (text == null) Assert.Equal(DBNull.Value, output.Value);
                    else Assert.Equal(text, Assert.IsType<OceanBaseDecimal>(output.Value).ToString());
                    using var after = connection.CreateCommand(); after.CommandText = "after number output";
                    Assert.Equal(1, await after.ExecuteNonQueryAsync());
                }
                else
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                    Assert.IsType<OverflowException>(error.InnerException); Assert.True(error.IsExecutionOutcomeUnknown);
                    Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "2.2.75")]
        [InlineData(true, "2.2.75")]
        [InlineData(false, "5.7.25-OceanBase 2.2.75")]
        [InlineData(true, "5.7.25-OceanBase 2.2.75")]
        [InlineData(false, "unknown-secret")]
        [InlineData(true, "unknown-secret")]
        public async Task OracleProcedureUnsupportedVersionNeverSendsSqlAndLeavesSessionUsable(bool async, string version)
        {
            var server = Server(async channel =>
            {

                if (version == "unknown-secret") return;
                await Query(channel, "after unsupported"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: version);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                if (version == "unknown-secret")
                {
                    var opening = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await connection.OpenAsync(); else connection.Open(); });
                    Assert.DoesNotContain("secret", opening.Message); Assert.False(opening.IsExecutionOutcomeUnknown);
                    return;
                }
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                var error = await Assert.ThrowsAsync<NotSupportedException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.DoesNotContain("secret", error.Message);
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                using var after = connection.CreateCommand(); after.CommandText = "after unsupported"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 1U, false)]
        [InlineData(true, 1U, false)]
        [InlineData(false, 3U, false)]
        [InlineData(true, 3U, false)]
        [InlineData(false, 1U, true)]
        [InlineData(true, 1U, true)]
        [InlineData(false, 3U, true)]
        [InlineData(true, 3U, true)]
        public async Task OracleReturningIntoUsesBinaryBindingsAndPublishesZeroRowsAsNull(bool async, uint flags, bool empty)
            => await OracleReturningIntoUsesBinaryBindingsAndPublishesZeroRowsAsNullCore(async, flags, empty);

        private static async Task OracleReturningIntoUsesBinaryBindingsAndPublishesZeroRowsAsNullCore(bool async, uint flags, bool empty, int fetchSize = 0)
        {
            const string sql = "update t set x=? returning x into ?";
            var inout = flags == 3 && !empty;
            var server = Server(async channel =>
            {

                var request = await StatementCommand(channel, 0xa1);
                var encoded = inout ? new byte[] { 0, 1, 8, 0, 8, 0, 7, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0 }
                    : new byte[] { 2, 1, 8, 0, 6, 0, 7, 0, 0, 0, 0, 0, 0, 0 };
                Assert.Equal(OraclePrepareExecutePacket.Build(0, sql, 2, encoded), request);
                await OracleOutputReply(channel, false, flags, rows: !empty); await channel.WritePacketAsync(Ok(empty ? (byte)0 : (byte)1));
                await StatementCommand(channel, 25);
            }, serverVersion: "2.2.76");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = (OceanBaseCommand)connection.CreateCommand(); command.FetchSize = fetchSize; command.CommandText = "update t set x=:input returning x into :output";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "output", Direction = inout ? ParameterDirection.InputOutput : ParameterDirection.Output, Value = inout ? (object)8L : "old" };
                command.Parameters.Add(output);
                Assert.Null(async ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
                Assert.Equal(empty ? DBNull.Value : (object)42L, output.Value);
                if (async) await command.DisposeAsync(); else command.Dispose();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task OracleOutputWaitsForAllResultsAndEarlyClosePreservesOldValue(bool async, bool earlyClose)
            => await OracleOutputWaitsForAllResultsAndEarlyClosePreservesOldValueCore(async, earlyClose);

        private static async Task OracleOutputWaitsForAllResultsAndEarlyClosePreservesOldValueCore(bool async, bool earlyClose, int fetchSize = 0)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(status: 14));
                if (earlyClose) { await AssertOracleExecuteTransportClosed(channel); return; }
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("visible", 8));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 55, 0, 0, 0, 0, 0, 0, 0 }); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 25);
            }, serverVersion: "4.0.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = (OceanBaseCommand)connection.CreateCommand(); command.FetchSize = fetchSize; command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                using var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                Assert.Equal("old", output.Value); Assert.Equal(0, reader.FieldCount);
                if (!earlyClose)
                {
                    Assert.True(async ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal("old", output.Value); Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(55L, reader.GetInt64(0));
                    Assert.False(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(42L, output.Value);
                    Assert.False(async ? await reader.NextResultAsync() : reader.NextResult());
                }
                if (async) await reader.CloseAsync(); else reader.Close();
                if (earlyClose) Assert.Equal("old", output.Value);
                if (async) await command.DisposeAsync(); else command.Dispose();
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task OracleOutputErrorsNeverPublishAndKnownErrorsAllowSessionReuse(bool async, bool laterError)
            => await OracleOutputErrorsNeverPublishAndKnownErrorsAllowSessionReuseCore(async, laterError);

        private static async Task OracleOutputErrorsNeverPublishAndKnownErrorsAllowSessionReuseCore(bool async, bool laterError, int fetchSize = 0)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await OracleOutputReply(channel, false);
                if (laterError) await channel.WritePacketAsync(Ok(status: 14));
                await channel.WritePacketAsync(Error());
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after error"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = (OceanBaseCommand)connection.CreateCommand(); command.FetchSize = fetchSize; command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.InputOutput, Value = 8L }; command.Parameters.Add(output);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(1064, error.ServerErrorCode); Assert.Equal(8L, output.Value);
                if (async) await command.DisposeAsync(); else command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after error"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "flags")]
        [InlineData(true, "flags")]
        [InlineData(false, "multirow")]
        [InlineData(true, "multirow")]
        [InlineData(false, "missing")]
        [InlineData(true, "missing")]
        [InlineData(false, "decode")]
        [InlineData(true, "decode")]
        [InlineData(false, "metadata-count")]
        [InlineData(true, "metadata-count")]
        [InlineData(false, "metadata-no-outputs")]
        [InlineData(true, "metadata-no-outputs")]
        [InlineData(false, "ordinary")]
        [InlineData(true, "ordinary")]
        public async Task OracleInvalidOutputsAreAtomicAndBreakUnknownExchanges(bool async, string fault)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                if (fault == "flags") { await channel.WritePacketAsync(OracleExecuteHeader(flags: 2)); return; }
                if (fault == "missing")
                {
                    await channel.WritePacketAsync(OracleExecuteHeader(2, 0, 0, rows: false));
                    await channel.WritePacketAsync(Column("p")); await channel.WritePacketAsync(Column("p"));
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok()); return;
                }
                var metadataOutput = fault.StartsWith("metadata-", StringComparison.Ordinal);
                await channel.WritePacketAsync(OracleExecuteHeader(2, fault == "decode" || fault == "metadata-count" ? (ushort)2 : (ushort)1,
                    flags: metadataOutput || fault == "ordinary" ? 0u : 4u));
                await channel.WritePacketAsync(Column("p")); await channel.WritePacketAsync(Column("p")); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("first", 8));
                if (fault == "decode" || fault == "metadata-count") await channel.WritePacketAsync(Column("bad", 253));
                await channel.WritePacketAsync(metadataOutput ? new byte[] { 254, 0, 0, 0, 16 } : Eof());
                if (metadataOutput) return;
                if (fault == "decode") await channel.WritePacketAsync(new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0, 1, 255 });
                else
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 });
                    if (fault == "ordinary") { await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok()); }
                    else await channel.WritePacketAsync(new byte[] { 0, 0, 43, 0, 0, 0, 0, 0, 0, 0 });
                }
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                var first = new OceanBaseParameter { ParameterName = "first", Value = "first old", Direction = fault == "metadata-no-outputs" ? ParameterDirection.Input : ParameterDirection.Output };
                var second = new OceanBaseParameter { ParameterName = "second", Value = "second old", Direction = fault == "decode" ? ParameterDirection.Output : ParameterDirection.Input };
                command.Parameters.Add(first); command.Parameters.Add(second);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal("first old", first.Value); Assert.Equal("second old", second.Value);
                if (fault.StartsWith("metadata-", StringComparison.Ordinal))
                    Assert.Equal("Oracle output metadata does not match the bound output parameters.", Assert.IsType<ProtocolException>(error.InnerException).Message);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task OracleOutputCommitCannotPublishAfterCancellationOrSessionClose(bool async, bool close)
        {
            var server = Server(async channel => { await Query(channel, "commit boundary"); await channel.WritePacketAsync(Ok()); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession(); var published = false;
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                exchange.BeforeSuccessfulCompletion = () => published = true;
                if (async) { await exchange.SendAsync(OceanBaseCommand.SqlPacket("commit boundary", 3)); await exchange.ReceiveAsync(CancellationToken.None); }
                else { exchange.Send(OceanBaseCommand.SqlPacket("commit boundary", 3)); exchange.Receive(); }
                if (close) connection.Close(); else exchange.Cancel();
                var error = Assert.Throws<OceanBaseException>(() => { try { exchange.Complete(); } catch (Exception caught) { throw exchange.Fail(caught); } });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.False(published);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task OpeningIdentityAndOutputCancellationNeverPublishOrReplay(bool async, bool versionProbe, bool timeout)
            => await OpeningIdentityAndOutputCancellationNeverPublishOrReplayCore(async, versionProbe, timeout);

        private static async Task OpeningIdentityAndOutputCancellationNeverPublishOrReplayCore(bool async, bool versionProbe, bool timeout, int fetchSize = 0)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = versionProbe ? IdentityProbeServer(async channel =>
            {
                waiting.SetResult(); await AssertOracleExecuteTransportClosed(channel);
            }) : Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false);
                waiting.SetResult(); await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.2.0");
            using var cancellation = new CancellationTokenSource();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true" + (versionProbe && timeout ? ";Connection Timeout=1" : ""));
            try
            {
                if (!versionProbe) await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.FetchSize = fetchSize; command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.CommandTimeout = timeout ? 1 : 5;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "in", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "out", Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                Task executing = versionProbe
                    ? async ? connection.OpenAsync(cancellation.Token) : Task.Run(connection.Open)
                    : async ? command.ExecuteNonQueryAsync(cancellation.Token) : Task.Run(command.ExecuteNonQuery);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (!timeout) { if (async) cancellation.Cancel(); else if (versionProbe) connection.Close(); else command.Cancel(); }
                var error = await Record.ExceptionAsync(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
                if (!versionProbe) { var unknown = Assert.IsType<OceanBaseException>(error); Assert.True(unknown.IsExecutionOutcomeUnknown); error = unknown.InnerException; }
                if (timeout) Assert.IsType<TimeoutException>(error);
                else
                {
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error);
                    if (async) Assert.Equal(cancellation.Token, canceled.CancellationToken);
                }
                Assert.Equal("old", output.Value); Assert.Equal(versionProbe ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleProcedureRespectsTransactionBindingAndDoesNotChangeItsOwnership(bool async)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(1, 5));
                await StatementCommand(channel, 25); channel.BeginCommand(); await TransactionEnd(channel, false);
                await Query(channel, "after rollback"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = async ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
                var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "in", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "out", Direction = ParameterDirection.Output }; command.Parameters.Add(output);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery()); command.Transaction = transaction;
                Assert.Equal(1, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); Assert.Equal(42L, output.Value); Assert.Same(connection, transaction.Connection);
                if (async) await command.DisposeAsync(); else command.Dispose();
                if (async) await transaction.RollbackAsync(); else transaction.Rollback();
                using var after = connection.CreateCommand(); after.CommandText = "after rollback"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task OpeningIdentityErrorOrMalformedResultDoesNotClaimSqlExecution(bool async, bool malformed)
        {
            var server = IdentityProbeServer(async channel =>
            {
                await channel.WritePacketAsync(malformed ? new byte[] { 0 } : Error());
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await connection.OpenAsync(); else connection.Open(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Closed, connection.State);
                if (!malformed) Assert.Equal(1064, error.ServerErrorCode);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleProcedureWithoutParametersUsesPrepareExecuteAndClosesItsId(bool async)
            => await OracleProcedureWithoutParametersUsesPrepareExecuteAndClosesItsIdCore(async);

        private static async Task OracleProcedureWithoutParametersUsesPrepareExecuteAndClosesItsIdCore(bool async, int fetchSize = 0)
        {
            var server = Server(async channel =>
            {

                Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN p(); END;", 0, Array.Empty<byte>()), await StatementCommand(channel, 0xa1));
                await channel.WritePacketAsync(OracleExecuteHeader(0, 0, 0, rows: false)); await channel.WritePacketAsync(Ok(3));
                await StatementCommand(channel, 25);
            }, serverVersion: "2.2.76.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = (OceanBaseCommand)connection.CreateCommand(); command.FetchSize = fetchSize; command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                Assert.Equal(3, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                if (async) await command.DisposeAsync(); else command.Dispose();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleInvalidCallBindingsAndExecutionModesFailBeforeVersionOrSql(bool async)
        {
            var server = Server(async channel => { await Query(channel, "after validation"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                command.CommandType = CommandType.TableDirect;
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (async) await command.PrepareAsync(); else command.Prepare(); });
                command.CommandType = CommandType.StoredProcedure; command.FetchSize = 1; command.UseCursorOffset = true;
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); }); command.FetchSize = 0; command.UseCursorOffset = false;
                command.CommandText = "p; delete from secret";
                var error = await Assert.ThrowsAsync<ArgumentException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); }); Assert.DoesNotContain("secret", error.Message);
                command.CommandType = CommandType.Text; command.CommandText = "begin p(:out, :out); end;";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "out", Direction = ParameterDirection.Output });
                await Assert.ThrowsAsync<ArgumentException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                using var after = connection.CreateCommand(); after.CommandText = "after validation"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
