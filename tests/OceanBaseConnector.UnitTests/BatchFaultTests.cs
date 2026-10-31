using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public async Task BatchMultiSendRejectsOutputParametersBeforeWriting()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var command = BatchItem(batch, "select :value");
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "value", Direction = ParameterDirection.Output });
                await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync());
                Assert.Equal(-1, command.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task BatchMultiSendRejectsCursorResultBeforeExecute()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Column("cursor", 163)); await channel.WritePacketAsync(Eof());
                Assert.Equal(90, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "select cursor");
                await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteScalarAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task BatchMultiSendBreaksSessionWhenAGroupResponseFails()
        {
            var server = Server(async channel =>
            {
                for (byte id = 90; id < 93; id++)
                {
                    await StatementCommand(channel, 22); await Prepared(channel, id, 0);
                }
                await StatementCommand(channel, 23); await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(Error());
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=3");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var first = BatchItem(batch, "one"); var second = BatchItem(batch, "two"); var third = BatchItem(batch, "three");
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => batch.ExecuteNonQueryAsync());
                Assert.Same(first, error.BatchCommand);
                Assert.Equal(-1, first.RecordsAffected); Assert.Equal(-1, second.RecordsAffected); Assert.Equal(-1, third.RecordsAffected);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task BatchMultiSendCancellationAfterWriteDoesNotReplay()
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                await StatementCommand(channel, 23); sent.SetResult();
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "one");
                var running = batch.ExecuteNonQueryAsync(cancellation.Token);
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => running);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task OldCloseConnectionReaderCannotCloseReopenedSession(bool batchReader, bool asynchronous)
        {
            var first = Server(async channel => { await Query(channel, "first"); await channel.WritePacketAsync(Ok(1)); Assert.Equal(0, await channelStreamRead(channel)); });
            var second = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(2)); });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "first");
                using var command = connection.CreateCommand(); command.CommandText = "first";
                var reader = batchReader ? await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection) : await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                connection.Close(); connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                Assert.Equal(ConnectionState.Open, connection.State);
                command.CommandText = "after"; Assert.Equal(2, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("token")]
        [InlineData("cancel")]
        [InlineData("timeout")]
        public async Task BatchPartialSendCancellationRetainsPrefixAndUnknownAttribution(string origin)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); batch.Timeout = origin == "timeout" ? 1 : 30;
                var one = BatchItem(batch, "one"); var two = BatchItem(batch, "two"); var suffix = BatchItem(batch, "never");
                var prefixBytes = ResponseFrame(OceanBaseCommand.SqlPacket("one", 3), 0).Length;
                var ok = TimeZoneOk("+00:00"); ok[1] = 2;
                var stream = new ProtocolFaultStream(ResponseFrame(ok, 1), prefixBytes + 2) { PauseAfterPartialWrite = true }; InstallFaultStream(connection, stream);
                var execution = batch.ExecuteNonQueryAsync(cancellation.Token); await stream.PartialWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (origin == "token") cancellation.Cancel(); else if (origin == "cancel") batch.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(two, error.BatchCommand);
                Assert.Equal(2, one.RecordsAffected); Assert.Equal(-1, two.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected); Assert.Equal(prefixBytes + 2, stream.Written.Length);
                if (origin == "token") Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException).CancellationToken);
                if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                Assert.NotEqual(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchFieldStreamFailureReleasesOperationWithoutNeedingExtraClose(bool materialize)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var item = BatchItem(batch, "stream"); BatchItem(batch, "never");
                using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in new[] { new byte[] { 1 }, Column("value", 252, 63), Eof(), new byte[] { 5, 1, 2, 3, 4, 5 } }) input.Write(ResponseFrame(packet, sequence++));
                var bytes = input.ToArray(); InstallFaultStream(connection, new ProtocolFaultStream(bytes[..^3]));
                using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    if (materialize) await reader.GetFieldValueAsync<byte[]>(0);
                    else { using var stream = reader.GetStream(0); var buffer = new byte[5]; await stream.ReadExactlyAsync(buffer); }
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(item, error.BatchCommand); Assert.True(reader.IsClosed);
                batch.BatchCommands.Clear(); Assert.Empty(batch.BatchCommands);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task BatchSyncAndAsyncFaultsNeverReplayOrSendSuffix(bool asynchronous, bool partialWrite)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var first = BatchItem(batch, "first"); var suffix = BatchItem(batch, "never");
                var stream = new ProtocolFaultStream(Array.Empty<byte>(), partialWrite ? 2 : int.MaxValue); InstallFaultStream(connection, stream);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(first, error.BatchCommand); Assert.Equal(-1, suffix.RecordsAffected);
                Assert.Equal(partialWrite ? 2 : ResponseFrame(OceanBaseCommand.SqlPacket("first", 3), 0).Length, stream.Written.Length);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("token")]
        [InlineData("cancel")]
        [InlineData("close")]
        public async Task BatchPendingResponseCancellationIsUnknownAndStopsSuffix(string origin)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel => { await Query(channel, "pending"); received.SetResult(); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var first = BatchItem(batch, "pending"); BatchItem(batch, "never");
                var execution = batch.ExecuteNonQueryAsync(cancellation.Token); await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (origin == "token") cancellation.Cancel(); else if (origin == "cancel") batch.Cancel(); else connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Same(first, error.BatchCommand); Assert.True(error.IsExecutionOutcomeUnknown);
                if (origin == "token") Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException).CancellationToken);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchCancelFromCompletionCallbackKeepsTerminalResultAndStopsSuffix(bool suffix)
        {
            var server = Server(async channel => { await Query(channel, "first"); await channel.WritePacketAsync(Ok(2)); Assert.Equal(0, await channelStreamRead(channel)); });
            using var log = new RecordingLoggerFactory(); using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var first = BatchItem(batch, "first"); if (suffix) BatchItem(batch, "never");
                log.OnLog = id => { if (id.Id == 1100) batch.Cancel(); };
                if (suffix) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteNonQueryAsync());
                else Assert.Equal(2, await batch.ExecuteNonQueryAsync());
                Assert.Equal(2, first.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { log.OnLog = null; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchTransportUsesOnlyRequestedSyncOrAsyncIo(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "first"); BatchItem(batch, "second");
                var ok = TimeZoneOk("+00:00"); ok[1] = 2;
                var stream = new ProtocolFaultStream(ResponseFrame(ok, 1).Concat(ResponseFrame(ok, 1)).ToArray()); InstallFaultStream(connection, stream);
                Assert.Equal(4, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(0, asynchronous ? stream.SyncCalls : stream.AsyncCalls); Assert.True((asynchronous ? stream.AsyncCalls : stream.SyncCalls) > 0);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchCancellationBetweenStatementsPreservesOriginalCallerToken(bool prepare)
        {
            var server = Server(async channel =>
            {
                if (prepare) { await StatementCommand(channel, 22); await Prepared(channel, 90, 0); await StatementCommand(channel, 25); }
                else { await Query(channel, "first"); await channel.WritePacketAsync(Ok(2)); }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var log = new RecordingLoggerFactory(); using var connection = new OceanBaseConnection(Configuration(server.Port), log); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); await using var batch = connection.CreateBatch(); BatchItem(batch, "first"); BatchItem(batch, "never");
                log.OnLog = id => { if (id.Id == 1100) cancellation.Cancel(); };
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare ? batch.PrepareAsync(cancellation.Token) : batch.ExecuteNonQueryAsync(cancellation.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { log.OnLog = null; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task BatchNextResultPartialSendRetainsItsOwnReaderToken()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "one"); var two = BatchItem(batch, "two");
                var ok = TimeZoneOk("+00:00"); ok[1] = 2;
                var stream = new ProtocolFaultStream(ResponseFrame(ok, 1), ResponseFrame(OceanBaseCommand.SqlPacket("one", 3), 0).Length + 2) { PauseAfterPartialWrite = true }; InstallFaultStream(connection, stream);
                await using var reader = await batch.ExecuteReaderAsync(); var execution = reader.NextResultAsync(cancellation.Token);
                await stream.PartialWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(two, error.BatchCommand);
                Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException).CancellationToken);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchNeverMigratesSuffixToConnectionReopenedByLogger(bool asynchronous)
        {
            var first = Server(async channel => { await Query(channel, "first"); await channel.WritePacketAsync(Ok(1)); Assert.Equal(0, await channelStreamRead(channel)); });
            var second = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(2)); });
            using var log = new RecordingLoggerFactory(); using var connection = new OceanBaseConnection(Configuration(first.Port), log);
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "first"); var suffix = BatchItem(batch, "never");
                log.OnLog = id => { if (id.Id != 1100) return; log.OnLog = null; connection.Close(); connection.ConnectionString = Configuration(second.Port); connection.Open(); };
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Equal(-1, suffix.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(2, await command.ExecuteNonQueryAsync());
            }
            finally { log.OnLog = null; connection.Close(); first.Stop(); second.Stop(); }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
