using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static void InstallFaultStream(OceanBaseConnection connection, ProtocolFaultStream stream)
        {
            using var lease = connection.GetOpenSession().Acquire();
            lease.Channel.ReplaceUnderlyingStream(stream); lease.Complete();
        }
        private static byte[] ResponseFrame(byte[] payload, byte sequence = 1)
        {
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)payload.Length; frame[1] = (byte)(payload.Length >> 8); frame[2] = (byte)(payload.Length >> 16); frame[3] = sequence;
            Array.Copy(payload, 0, frame, 4, payload.Length); return frame;
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ExchangeCanceledBeforeSendReleasesLeaseWithoutSendingOrPoisoningSession(bool asynchronous, bool callerToken)
        {
            var server = Server(async channel => { await Query(channel, "after pre-send cancel"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var cancellation = new CancellationTokenSource();
                var releases = 0;
                using (var exchange = new CommandExchange(connection.GetOpenSession(), new ConnectionLog(null), 0, cancellation.Token, () => releases++))
                {
                    if (callerToken) cancellation.Cancel(); else exchange.Cancel();
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asynchronous
                        ? exchange.SendAsync(OceanBaseCommand.SqlPacket("must not send", 3))
                        : RunSync(() => exchange.Send(OceanBaseCommand.SqlPacket("must not send", 3))));
                    if (callerToken) Assert.Equal(cancellation.Token, error.CancellationToken);
                    Assert.False(exchange.HasSentSql); Assert.Equal(1, releases);
                }
                Assert.Equal(1, releases); Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "after pre-send cancel";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "query", 2)]
        [InlineData(true, "query", 2)]
        [InlineData(false, "query", 6)]
        [InlineData(true, "query", 6)]
        [InlineData(false, "prepare", 2)]
        [InlineData(true, "prepare", 6)]
        [InlineData(false, "execute", 2)]
        [InlineData(true, "execute", 6)]
        [InlineData(false, "commit", 2)]
        [InlineData(true, "commit", 6)]
        [InlineData(false, "rollback", 2)]
        [InlineData(true, "rollback", 6)]
        public async Task PartialTransportWriteDestroysSessionAndNeverReplays(bool asynchronous, string operation, int written)
        {
            var server = Server(async channel =>
            {
                if (operation == "commit" || operation == "rollback") await TransactionBegin(channel);
                if (operation == "execute") { await StatementCommand(channel, 22); await Prepared(channel, 37, 0); }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var transaction = operation == "commit" || operation == "rollback" ? await connection.BeginTransactionAsync() : null;
                using var command = connection.CreateCommand(); command.CommandText = "update partial";
                if (operation == "execute") await command.PrepareAsync();
                var transport = new ProtocolFaultStream(Array.Empty<byte>(), written); InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? operation switch
                {
                    "prepare" => command.PrepareAsync(),
                    "commit" => transaction!.CommitAsync(),
                    "rollback" => transaction!.RollbackAsync(),
                    _ => command.ExecuteNonQueryAsync()
                } : RunSync(() =>
                {
                    switch (operation)
                    {
                        case "prepare": command.Prepare(); break;
                        case "commit": transaction!.Commit(); break;
                        case "rollback": transaction!.Rollback(); break;
                        default: command.ExecuteNonQuery(); break;
                    }
                }));
                Assert.Equal(operation != "prepare", error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(transport.Disposed);
                Assert.Equal(written, transport.Written.Length);
                if (asynchronous) { Assert.Equal(0, transport.SyncCalls); Assert.True(transport.AsyncCalls > 0); }
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                Assert.Equal(written, transport.Written.Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("query", "cancel")]
        [InlineData("query", "token")]
        [InlineData("query", "timeout")]
        [InlineData("prepare", "token")]
        [InlineData("prepare", "timeout")]
        [InlineData("commit", "timeout")]
        [InlineData("rollback", "token")]
        public async Task CancellationDuringPartialAsyncSendDestroysSessionWithoutReplay(string operation, string origin)
        {
            var server = Server(async channel =>
            {
                if (operation == "commit" || operation == "rollback") await TransactionBegin(channel);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var cancellation = new CancellationTokenSource(); using var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=1", log);
            try
            {
                await connection.OpenAsync();
                using var transaction = operation == "commit" || operation == "rollback" ? await connection.BeginTransactionAsync() : null;
                using var command = connection.CreateCommand(); command.CommandText = "update partial cancel";
                var transport = new ProtocolFaultStream(Array.Empty<byte>(), 6) { PauseAfterPartialWrite = true };
                InstallFaultStream(connection, transport);
                var pending = operation switch
                {
                    "prepare" => command.PrepareAsync(cancellation.Token),
                    "commit" => transaction!.CommitAsync(cancellation.Token),
                    "rollback" => transaction!.RollbackAsync(cancellation.Token),
                    _ => command.ExecuteNonQueryAsync(cancellation.Token)
                };
                await transport.PartialWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (origin == "token") cancellation.Cancel();
                else if (origin == "cancel") command.Cancel();
                var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Exception? cause = error;
                if (operation != "prepare")
                {
                    var databaseError = Assert.IsType<OceanBaseException>(error);
                    Assert.True(databaseError.IsExecutionOutcomeUnknown); cause = databaseError.InnerException;
                }
                if (origin == "timeout") Assert.IsType<TimeoutException>(cause);
                else
                {
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(cause);
                    if (origin == "token") Assert.Equal(cancellation.Token, canceled.CancellationToken);
                }
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(transport.Disposed);
                var sql = operation == "query" || operation == "prepare" ? command.CommandText : operation.ToUpperInvariant();
                var expected = ResponseFrame(OceanBaseCommand.SqlPacket(sql, operation == "prepare" ? (byte)22 : (byte)3), 0);
                Assert.Equal(expected.AsSpan(0, 6).ToArray(), transport.Written);
                Assert.Equal(0, transport.SyncCalls); Assert.True(transport.AsyncCalls > 0);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery()); Assert.Equal(6, transport.Written.Length);
                Assert.Contains(log.Entries, entry => entry.EventId.Id == (transaction == null ? 1101 : 1201) &&
                    entry.Message.Contains("Canceled=" + (origin != "timeout"), StringComparison.Ordinal));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public async Task FullyReceivedTerminalResponseWinsOverCancellationBeforeParsing(bool asynchronous, bool serverError, bool callerToken)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var cancellation = new CancellationTokenSource(); using var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "update terminal";
                var transport = new ProtocolFaultStream(ResponseFrame(serverError ? Error() : Ok(3)));
                transport.AfterInputConsumed = () => { if (callerToken) cancellation.Cancel(); else command.Cancel(); };
                InstallFaultStream(connection, transport);
                if (serverError)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(cancellation.Token) : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(1064, error.ServerErrorCode);
                }
                else Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync(cancellation.Token) : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.All(log.Entries, entry => { Assert.Null(entry.Error); Assert.DoesNotContain("secret", entry.Message); Assert.DoesNotContain("update terminal", entry.Message); });
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IntermediateOkWithMoreResultsDoesNotCountAsConfirmedCommandCompletion(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "multi";
                var transport = new ProtocolFaultStream(ResponseFrame(Ok(1, 14))) { AfterInputConsumed = command.Cancel };
                InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
