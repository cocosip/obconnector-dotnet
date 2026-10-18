using System;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseUsesInitDbAndRepreparesStatementsInNewDatabase(bool asynchronous)
        {
            const string database = "Mixed目标'`;\\库";
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 37, 0);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(OceanBaseCommand.SqlPacket(database, 2), await StatementCommand(channel, 2));
                await channel.WritePacketAsync(TimeZoneOk("Asia/Tokyo"));
                await StatementCommand(channel, 22); await Prepared(channel, 38, 0);
                var execute = await StatementCommand(channel, 23); Assert.Equal(38, execute[1]); await channel.WritePacketAsync(Ok(2));
                var firstClose = await StatementCommand(channel, 25); var secondClose = await StatementCommand(channel, 25);
                Assert.Contains((byte)37, new[] { firstClose[1], secondClose[1] }); Assert.Contains((byte)38, new[] { firstClose[1], secondClose[1] });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Database=Initial");
            try
            {
                Assert.Equal("Initial", connection.Database); await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "update t set n=1";
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                if (asynchronous) await connection.ChangeDatabaseAsync(database); else connection.ChangeDatabase(database);
                Assert.Equal(database, connection.Database); Assert.Equal("Asia/Tokyo", connection.GetOpenSession().SessionTimeZone);
                Assert.Equal("Initial", new OceanBaseConnectionStringBuilder(connection.ConnectionString).Database);
                Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            Assert.Equal("Initial", connection.Database);
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseKnownErrorLeavesPreviousDatabaseAndConnectionUsable(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 2); await channel.WritePacketAsync(Error());
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Database=Initial");
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.ChangeDatabaseAsync("denied") : RunSync(() => connection.ChangeDatabase("denied")));
                Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal("Initial", connection.Database); Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ChangeDatabasePreCancellationDoesNotSendOrChangeState()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ChangeDatabaseAsync("other", canceled.Token));
                Assert.Equal(canceled.Token, error.CancellationToken); Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal("", connection.Database);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseValidatesNamesAndOracleRejectsWithoutSending(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? connection.ChangeDatabaseAsync("other") : RunSync(() => connection.ChangeDatabase("other")));
                UseInternalMySqlFixture(connection);
                foreach (var name in new[] { null, "", "db\0suffix", "bad\ud800", "bad\udfff" })
                    await Assert.ThrowsAnyAsync<ArgumentException>(() => asynchronous ? connection.ChangeDatabaseAsync(name!) : RunSync(() => connection.ChangeDatabase(name!)));
                var oversized = new string('中', (int)(PacketChannel.DefaultMaxPacketSize / 3 + 1));
                await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? connection.ChangeDatabaseAsync(oversized) : RunSync(() => connection.ChangeDatabase(oversized)));
                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                    await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? connection.ChangeDatabaseAsync("other") : RunSync(() => connection.ChangeDatabase("other")));
                Assert.Equal(ConnectionState.Open, connection.State); Assert.Empty(transport.Written);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        [InlineData(false, 5)]
        [InlineData(true, 5)]
        public async Task ChangeDatabaseRejectsNonTerminalOrMalformedSuccess(bool asynchronous, int fault)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Database=Initial");
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var packet = fault switch { 0 => new byte[] { 1 }, 1 => Eof(), 2 => Ok(1), 3 => Ok(status: 14), 4 => Ok(status: 70), _ => new byte[] { 0, 0, 1, 6, 0, 0, 0 } };
                var transport = new ProtocolFaultStream(ResponseFrame(packet, 1)); InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.ChangeDatabaseAsync("target") : RunSync(() => connection.ChangeDatabase("target")));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal("Initial", connection.Database);
                Assert.Equal(ResponseFrame(OceanBaseCommand.SqlPacket("target", 2), 0), transport.Written);
                if (asynchronous) Assert.Equal(0, transport.SyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 6)]
        [InlineData(true, 6)]
        public async Task ChangeDatabasePartialWriteDestroysSessionWithoutReplay(bool asynchronous, int bytes)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>(), bytes); InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.ChangeDatabaseAsync("target") : RunSync(() => connection.ChangeDatabase("target")));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal("", connection.Database);
                Assert.Equal(bytes, transport.Written.Length); Assert.True(transport.Disposed);
                Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("replay")); Assert.Equal(bytes, transport.Written.Length);
                if (asynchronous) Assert.Equal(0, transport.SyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("token")]
        [InlineData("timeout")]
        [InlineData("close")]
        public async Task ChangeDatabaseAsyncWaitDoesNotBlockAndUsesCommandBudget(string origin)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel => { await StatementCommand(channel, 2); received.TrySetResult(); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=" + (origin == "timeout" ? 1 : 10));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var pending = connection.ChangeDatabaseAsync("target", cancellation.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                Assert.Equal("", connection.Database); Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("busy"));
                if (origin == "token") cancellation.Cancel(); else if (origin == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                if (origin == "token") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(origin == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseAllowedInLocalTransactionAndRejectsBusyReader(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel, "REPEATABLE READ");
                await StatementCommand(channel, 3); await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n", 3));
                await channel.WritePacketAsync(Eof(4)); await channel.WritePacketAsync(new byte[] { 1, 49 }); await channel.WritePacketAsync(Eof(4));
                await StatementCommand(channel, 2); await channel.WritePacketAsync(Ok(status: 4)); channel.BeginCommand();
                await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection); using var transaction = await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "select n";
                using (var reader = await command.ExecuteReaderAsync())
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? connection.ChangeDatabaseAsync("target") : RunSync(() => connection.ChangeDatabase("target")));
                    Assert.True(await reader.ReadAsync()); Assert.False(await reader.ReadAsync());
                }
                if (asynchronous) await connection.ChangeDatabaseAsync("target"); else connection.ChangeDatabase("target");
                Assert.Equal("target", connection.Database); Assert.Same(connection, transaction.Connection);
                using var unbound = connection.CreateCommand(); unbound.CommandText = "must not send"; Assert.Throws<InvalidOperationException>(() => unbound.ExecuteNonQuery());
                await transaction.RollbackAsync(); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseKnownReplyWinsLateCancellation(bool serverError)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(ResponseFrame(serverError ? Error() : Ok(), 1)) { AfterInputConsumed = cancellation.Cancel };
                InstallFaultStream(connection, transport);
                if (serverError)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.ChangeDatabaseAsync("target", cancellation.Token));
                    Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                }
                else await connection.ChangeDatabaseAsync("target", cancellation.Token);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(0, transport.SyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ChangeDatabasePublishesBeforeCompletionCallbackAndDoesNotAlterNewDataSourceConnections()
        {
            var server = Server(async channel => { await StatementCommand(channel, 2); await channel.WritePacketAsync(Ok()); Assert.Equal(0, await channelStreamRead(channel)); });
            using var log = new RecordingLoggerFactory();
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port) + ";Database=Initial").UseLoggerFactory(log).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection); string? published = null;
                log.OnLog = id => { if (id.Id == 1100) { log.OnLog = null; published = connection.Database; connection.Close(); } };
                await connection.ChangeDatabaseAsync("target"); Assert.Equal("target", published); Assert.Equal("Initial", connection.Database);
                using var next = source.CreateConnection(); Assert.Equal("Initial", next.Database);
                Assert.Equal("Initial", new OceanBaseConnectionStringBuilder(source.ConnectionString).Database);
            }
            finally { log.OnLog = null; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseOldCompletionCannotChangeReopenedSession(bool asynchronous)
        {
            var first = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            var second = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(first.Port) + ";Database=Initial");
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(ResponseFrame(Ok(), 1))
                {
                    AfterInputConsumed = () => { connection.Close(); connection.ConnectionString = Configuration(second.Port) + ";Database=Fresh"; connection.Open(); }
                };
                InstallFaultStream(connection, transport);
                if (asynchronous) await connection.ChangeDatabaseAsync("old target"); else connection.ChangeDatabase("old target");
                Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal("Fresh", connection.Database);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChangeDatabaseDeniedKeepsPreparedStatementBoundToPreviousDatabase(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 37, 0);
                await StatementCommand(channel, 2); await channel.WritePacketAsync(Error());
                var execute = await StatementCommand(channel, 23); Assert.Equal(37, execute[1]); await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Database=Initial");
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection); using var command = connection.CreateCommand(); command.CommandText = "update t set n=1";
                await command.PrepareAsync();
                await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.ChangeDatabaseAsync("denied") : RunSync(() => connection.ChangeDatabase("denied")));
                Assert.Equal("Initial", connection.Database); Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ChangeDatabaseReaderTokenInterruptsPartialAsyncWrite()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>(), 6) { PauseAfterPartialWrite = true }; InstallFaultStream(connection, transport);
                var pending = connection.ChangeDatabaseAsync("target", cancellation.Token);
                await transport.PartialWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(6, transport.Written.Length); Assert.Equal(0, transport.SyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
