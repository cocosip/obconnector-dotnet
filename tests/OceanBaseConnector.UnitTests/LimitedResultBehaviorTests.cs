using System;
using System.Collections.Generic;
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
        private static async Task LimitedResultPrefix(PacketChannel channel, int shape = 0)
        {
            if (shape == 2) await channel.WritePacketAsync(Ok(2, 14));
            else
            {
                if (shape == 0)
                {
                    await BatchTextResult(channel, "alpha", terminate: false);
                    await channel.WritePacketAsync(new byte[] { 5, 98, 114, 97, 118, 111 });
                }
                else
                {
                    await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("value", 253)); await channel.WritePacketAsync(Eof());
                }
                await channel.WritePacketAsync(Eof(14)); await channel.WritePacketAsync(Ok(2, 14));
            }
            await BatchTextResult(channel, "hidden");
        }

        public static IEnumerable<object[]> CommandSingleRowCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var mysql in new[] { false, true })
                    foreach (var shape in new[] { 0, 1, 2 })
                        yield return new object[] { asynchronous, mysql, shape };
        }

        [Theory]
        [MemberData(nameof(CommandSingleRowCases))]
        public async Task CommandSingleRowNeverExposesLaterResultsEvenWithEmptyFirstResult(bool asynchronous, bool mysql, int shape)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "limited"); await LimitedResultPrefix(channel, shape);
                channel.BeginCommand(); await Query(channel, "after limited"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "limited";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SingleRow | CommandBehavior.SequentialAccess)
                    : command.ExecuteReader(CommandBehavior.SingleRow | CommandBehavior.SequentialAccess);
                Assert.Equal(shape == 0, asynchronous ? await reader.ReadAsync() : reader.Read());
                if (shape == 0) { using var field = reader.GetTextReader(0); Assert.Equal('a', field.Read()); }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(2, reader.RecordsAffected); Assert.False(reader.Read()); Assert.False(await reader.NextResultAsync());
                using var after = connection.CreateCommand(); after.CommandText = "after limited"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> BatchLimitedResultCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var mysql in new[] { false, true })
                    foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                        yield return new object[] { asynchronous, mysql, behavior };
        }

        [Theory]
        [MemberData(nameof(BatchLimitedResultCases))]
        public async Task BatchLimitedReaderHidesAllLaterResultsButExecutesSuffixAndKeepsCounts(bool asynchronous, bool mysql, CommandBehavior behavior)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "prefix"); await LimitedResultPrefix(channel);
                channel.BeginCommand(); await Query(channel, "suffix"); await BatchTextResult(channel, "also hidden", 14); await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after batch"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var batch = connection.CreateBatch(); var prefix = BatchItem(batch, "prefix"); var suffix = BatchItem(batch, "suffix");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior | CommandBehavior.SequentialAccess) : batch.ExecuteReader(behavior | CommandBehavior.SequentialAccess);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                using (var field = reader.GetTextReader(0)) Assert.Equal('a', field.Read());
                if ((behavior & CommandBehavior.SingleRow) == 0)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("bravo", reader.GetString(0));
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(2, prefix.RecordsAffected); Assert.Equal(3, suffix.RecordsAffected); Assert.Equal(5, reader.RecordsAffected);
                Assert.False(reader.Read()); Assert.False(await reader.NextResultAsync());
                Assert.Throws<InvalidOperationException>(() => connection.GetSchema()); reader.Close();
                using var after = connection.CreateCommand(); after.CommandText = "after batch"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BatchSingleRowEmptyFirstResultNeverSearchesLaterEntries(bool asynchronous, bool count)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "prefix"); await LimitedResultPrefix(channel, count ? 2 : 1);
                channel.BeginCommand(); await Query(channel, "suffix"); await BatchTextResult(channel, "hidden suffix");
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "prefix"); BatchItem(batch, "suffix");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SingleRow) : batch.ExecuteReader(CommandBehavior.SingleRow);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(2, reader.RecordsAffected);
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
        public async Task BatchLimitedResultsPropagateHiddenErrorsAndNeverSendUnstartedSuffix(bool asynchronous, bool singleRow, bool sameCommand)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "prefix");
                if (sameCommand)
                {
                    await BatchTextResult(channel, "alpha", 14); await channel.WritePacketAsync(Ok(2, 14)); await channel.WritePacketAsync(Error());
                }
                else
                {
                    await LimitedResultPrefix(channel); channel.BeginCommand(); await Query(channel, "bad"); await channel.WritePacketAsync(Error());
                }
                channel.BeginCommand(); await Query(channel, "after failure"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var prefix = BatchItem(batch, "prefix");
                var bad = sameCommand ? prefix : BatchItem(batch, "bad"); var suffix = BatchItem(batch, "never");
                var behavior = singleRow ? CommandBehavior.SingleRow : CommandBehavior.SingleResult;
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                // Advancing without reading must still consume hidden rows and surface their errors.
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => reader.NextResultAsync())
                    : Assert.Throws<OceanBaseException>(() => reader.NextResult());
                Assert.Same(bad, error.BatchCommand); Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(2, prefix.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected); Assert.True(reader.IsClosed); batch.Timeout = 4;
                using var after = connection.CreateCommand(); after.CommandText = "after failure"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "token")]
        [InlineData(true, "token")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        public async Task BatchLimitedResultHiddenExecutionCancellationKeepsUnknownAttribution(bool singleRow, string origin)
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Query(channel, "prefix"); await LimitedResultPrefix(channel);
                channel.BeginCommand(); await Query(channel, "pending"); sent.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); batch.Timeout = origin == "timeout" ? 1 : 0;
                var prefix = BatchItem(batch, "prefix"); var pending = BatchItem(batch, "pending"); var suffix = BatchItem(batch, "never");
                using var reader = await batch.ExecuteReaderAsync(singleRow ? CommandBehavior.SingleRow : CommandBehavior.SingleResult);
                var next = reader.NextResultAsync(cancellation.Token); await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (origin == "token") cancellation.Cancel(); else if (origin == "cancel") batch.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Same(pending, error.BatchCommand); Assert.True(error.IsExecutionOutcomeUnknown);
                if (origin == "token") Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException).CancellationToken);
                if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                Assert.True(reader.IsClosed); Assert.Equal(2, prefix.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                Assert.Equal(ConnectionState.Broken, connection.State); batch.Timeout = 4;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [MemberData(nameof(BatchLimitedResultCases))]
        public async Task BatchLimitedReaderEarlyCloseSkipsUnstartedSuffix(bool asynchronous, bool mysql, CommandBehavior behavior)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "prefix"); await BatchTextResult(channel, "alpha");
                channel.BeginCommand(); await Query(channel, "after close"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var batch = connection.CreateBatch(); BatchItem(batch, "prefix"); var suffix = BatchItem(batch, "never");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(-1, suffix.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after close"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
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
        public async Task BatchLimitedSchemaMetadataPreparesHiddenEntriesWithoutExecuting(bool asynchronous, bool mysql, bool keyInfo)
        {
            var server = Server(async channel =>
            {
                for (byte id = 81; id < 83; id++)
                {
                    await StatementCommand(channel, 22); await KeyPrepare(channel, id, SchemaColumn("id", 8, 20, flags: 1, originalName: "id"));
                    if (keyInfo) await KeyDirectory(channel, mysql, new string?[] { "租户库", "ledger", "PK", "P", "id", "1", "NO", "YES" });
                }
                for (byte id = 81; id < 83; id++) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var batch = connection.CreateBatch();
                for (var i = 0; i < 2; i++) BatchItem(batch, mysql ? "select id from `租户库`.ledger" : "select id from \"租户库\".\"ledger\"");
                var behavior = CommandBehavior.SingleRow | CommandBehavior.SingleResult | CommandBehavior.SchemaOnly | CommandBehavior.SequentialAccess | (keyInfo ? CommandBehavior.KeyInfo : 0);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                Assert.Single(reader.GetColumnSchema()); if (keyInfo) Assert.True(reader.GetColumnSchema()[0].IsKey);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(-1, reader.RecordsAffected); Assert.Throws<InvalidOperationException>(() => connection.GetSchema()); reader.Close();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
