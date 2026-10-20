using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static void EnableCursorOffset(DbCommand command)
        {
            var property = command.GetType().GetProperty("UseCursorOffset");
            Assert.NotNull(property); Assert.Equal(false, property.GetValue(command)); property.SetValue(command, true);
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
        public async Task CursorOffsetConsumesEveryTailBeforeReusingTheStatement(bool asynchronous, bool sequential, bool deprecated)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 2; command.CommandText = "select n";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "2.2.5", deprecated);
                await StatementCommand(channel, 22, deprecated); await Prepared(channel, 80, 0, 1, deprecated);
                for (var execution = 0; execution < 2; execution++)
                {
                    await StatementCommand(channel, 23, deprecated); await CursorMetadata(channel, 70, deprecated, deprecated);
                    for (var page = 0; page < 3; page++)
                    {
                        Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, await StatementCommand(channel, 28, deprecated));
                        await CursorMetadata(channel, 6, deprecated, deprecated);
                        if (page < 2) await channel.WritePacketAsync(CursorRow(page + 10));
                        await channel.WritePacketAsync(CursorTerminator(page == 2 ? (byte)134 : (byte)70, deprecated));
                        await channel.WritePacketAsync(Ok(page == 0 ? (byte)1 : (byte)2));
                    }
                }
                await StatementCommand(channel, 25, deprecated);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                for (var execution = 0; execution < 2; execution++)
                {
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    for (var value = 10; value < 12; value++)
                    { Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(value, reader.GetInt64(0)); }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(-1, reader.RecordsAffected);
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                command.Dispose();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("2.2.4")]
        [InlineData("2.2")]
        [InlineData("garbage")]
        [InlineData("2.2.5-unknown")]
        public async Task CursorOffsetRejectsUnprovenVersionsWithoutExecuting(string version)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, version); await Query(channel, "update allowed"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync());
                using var allowed = connection.CreateCommand(); allowed.CommandText = "update allowed"; Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorOffsetRejectsInvalidModeOrMissingFetchSizeBeforeSending(bool mysql)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = mysql ? 1 : 0; command.CommandText = "select n";
            var server = Server(async channel => { await Query(channel, "update allowed"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection); command.Connection = connection;
                Assert.Throws<NotSupportedException>(() => command.ExecuteReader());
                await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync());
                using var allowed = connection.CreateCommand(); allowed.CommandText = "update allowed"; Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "success")]
        [InlineData(true, "success")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "precancel")]
        [InlineData(true, "precancel")]
        public async Task CursorOffsetRetainsOwnershipUntilTheDelayedTailIsConfirmed(bool sequential, string outcome)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            command.CommandTimeout = outcome == "timeout" ? 1 : 5;
            var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1);
                await StatementCommand(channel, 23); await CursorMetadata(channel, 70);
                await StatementCommand(channel, 28); await CursorMetadata(channel, 6);
                await channel.WritePacketAsync(CursorRow(42)); await channel.WritePacketAsync(Eof(134)); terminated.SetResult();
                if (outcome == "success")
                {
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); await channel.WritePacketAsync(Ok(1));
                    await StatementCommand(channel, 25);
                }
                else Assert.Equal(0, await channelStreamRead(channel).WaitAsync(TimeSpan.FromSeconds(10)));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync()); Assert.Equal(42L, reader.GetInt64(0));
                using var cancellation = new CancellationTokenSource();
                await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (outcome == "precancel") cancellation.Cancel();
                var reading = reader.ReadAsync(cancellation.Token);
                if (outcome != "precancel") Assert.False(reading.IsCompleted);
                using var other = connection.CreateCommand(); other.CommandText = "must not send";
                Assert.Throws<InvalidOperationException>(() => other.ExecuteNonQuery());
                if (outcome == "success") { release.SetResult(); Assert.False(await reading); Assert.Equal(ConnectionState.Open, connection.State); command.Dispose(); }
                else
                {
                    if (outcome == "cancel") cancellation.Cancel();
                    if (outcome == "close") connection.Close();
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => reading);
                    Assert.True(error.IsExecutionOutcomeUnknown);
                    if (outcome == "cancel" || outcome == "precancel") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                    if (outcome == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                    if (outcome != "close") Assert.Equal(ConnectionState.Broken, connection.State);
                }
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "count")]
        [InlineData(true, "count")]
        [InlineData(false, "insert")]
        [InlineData(true, "insert")]
        [InlineData(false, "more")]
        [InlineData(true, "more")]
        [InlineData(false, "output")]
        [InlineData(true, "output")]
        [InlineData(false, "eof")]
        [InlineData(true, "eof")]
        [InlineData(false, "error")]
        [InlineData(true, "error")]
        [InlineData(false, "missing")]
        [InlineData(true, "missing")]
        [InlineData(false, "truncated")]
        [InlineData(true, "truncated")]
        public async Task CursorOffsetInvalidTailsDiscardTheSessionWithoutReplaying(bool sequential, string fault)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1);
                await StatementCommand(channel, 23); await CursorMetadata(channel, 70);
                await StatementCommand(channel, 28); await CursorMetadata(channel, 6);
                await channel.WritePacketAsync(CursorRow(42)); await channel.WritePacketAsync(Eof(134));
                if (fault == "missing") return;
                var tail = fault == "error" ? Error() : fault == "eof" ? Eof() : fault == "truncated" ? new byte[] { 0, 1 } : Ok(1);
                if (fault == "count") tail[1] = 2;
                if (fault == "insert") tail[2] = 1;
                if (fault == "more") tail[3] |= 8;
                if (fault == "output") tail[4] |= 16;
                await channel.WritePacketAsync(tail);
                Assert.Equal(0, await channelStreamRead(channel).WaitAsync(TimeSpan.FromSeconds(10)));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync()); Assert.Equal(42L, reader.GetInt64(0));
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reader.ReadAsync());
                Assert.Equal(fault != "error", error.IsExecutionOutcomeUnknown);
                if (fault == "error") Assert.Equal(1064, error.ServerErrorCode);
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorOffsetSharesVersionFactsButDoesNotLowerPrepareExecuteMinimum(bool asynchronous)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "2.2.75");
                await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1);
                await StatementCommand(channel, 23); await CursorMetadata(channel, 70);
                await StatementCommand(channel, 28); await CursorMetadata(channel, 6);
                await channel.WritePacketAsync(Eof(134)); await channel.WritePacketAsync(Ok());
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                { Assert.False(reader.HasRows); Assert.False(await reader.ReadAsync()); Assert.Equal(-1, reader.RecordsAffected); }
                using var procedure = connection.CreateCommand(); procedure.CommandType = CommandType.StoredProcedure; procedure.CommandText = "p";
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await procedure.ExecuteNonQueryAsync(); else procedure.ExecuteNonQuery(); });
                Assert.Equal(ConnectionState.Open, connection.State); command.Dispose();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorOffsetExplicitPrepareDoesNotProbeAndReopeningDoesNotReuseVersionFacts(bool asynchronous)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            var first = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1);
                channel.BeginCommand(); await OracleVersionReply(channel, "2.2.5");
                await StatementCommand(channel, 23); await CursorMetadata(channel, 70);
                await StatementCommand(channel, 28); await CursorMetadata(channel, 6);
                await channel.WritePacketAsync(Eof(134)); await channel.WritePacketAsync(Ok());
                Assert.Equal(0, await channelStreamRead(channel).WaitAsync(TimeSpan.FromSeconds(10)));
            });
            var second = Server(async channel =>
            {
                await OracleVersionReply(channel, "2.2.4"); await Query(channel, "update allowed"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader()) Assert.False(reader.HasRows);
                connection.Close(); connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                using var allowed = connection.CreateCommand(); allowed.CommandText = "update allowed"; Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "empty-progress")]
        [InlineData(true, "empty-progress")]
        [InlineData(false, "excess")]
        [InlineData(true, "excess")]
        [InlineData(false, "lost")]
        [InlineData(true, "lost")]
        public async Task CursorOffsetStillRejectsInvalidPageProgress(bool sequential, string fault)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1);
                await StatementCommand(channel, 23); await CursorMetadata(channel, 70);
                await StatementCommand(channel, 28); await CursorMetadata(channel, 6);
                if (fault != "empty-progress") await channel.WritePacketAsync(CursorRow(42));
                if (fault == "excess") await channel.WritePacketAsync(CursorRow(43));
                await channel.WritePacketAsync(Eof(fault == "empty-progress" ? (byte)70 : fault == "lost" ? (byte)6 : (byte)134));
                await channel.WritePacketAsync(Ok(fault == "empty-progress" ? (byte)0 : fault == "lost" ? (byte)1 : (byte)2));
                Assert.Equal(0, await channelStreamRead(channel).WaitAsync(TimeSpan.FromSeconds(10)));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); command.Connection = connection;
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    while (await reader.ReadAsync()) { }
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorOffsetPublishesSessionReportsFromIntermediateAndFinalTails(bool sequential)
        {
            using var command = new OceanBaseCommand(); EnableCursorOffset(command); command.FetchSize = 1; command.CommandText = "select n";
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0", true); await StatementCommand(channel, 22, true); await Prepared(channel, 80, 0, 1, true);
                await StatementCommand(channel, 23, true); await CursorMetadata(channel, 70, true, true);
                for (var page = 0; page < 2; page++)
                {
                    await StatementCommand(channel, 28, true); await CursorMetadata(channel, 6, true, true);
                    await channel.WritePacketAsync(CursorRow(page + 42)); await channel.WritePacketAsync(CursorTerminator(page == 0 ? (byte)70 : (byte)134, true));
                    var tail = TimeZoneOk(page == 0 ? "Asia/Tokyo" : "Europe/Paris"); tail[1] = (byte)(page + 1); await channel.WritePacketAsync(tail);
                }
                await StatementCommand(channel, 25, true);
            }, true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession(); command.Connection = connection;
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync()); Assert.Equal(42L, reader.GetInt64(0));
                Assert.True(await reader.ReadAsync()); Assert.Equal(43L, reader.GetInt64(0)); Assert.Equal("Asia/Tokyo", session.SessionTimeZone);
                Assert.False(await reader.ReadAsync()); Assert.Equal("Europe/Paris", session.SessionTimeZone); Assert.Equal(ConnectionState.Open, connection.State);
                command.Dispose();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
