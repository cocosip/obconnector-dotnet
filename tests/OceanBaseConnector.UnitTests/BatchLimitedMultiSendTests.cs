using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> LimitedMultiSendCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var limit in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                    foreach (var sequential in new[] { false, true })
                        yield return new object[] { asynchronous, limit, sequential, 2 };
            foreach (var asynchronous in new[] { false, true })
                foreach (var advance in new[] { 0, 1 })
                    yield return new object[] { asynchronous, CommandBehavior.SingleRow, true, advance };
        }

        private static async Task LimitedMultiSendPrepare(PacketChannel channel)
        {
            await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
            await StatementCommand(channel, 22); await Prepared(channel, 90, 0, columns: 1);
            await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
        }

        private static async Task LimitedMultiSendGroup(PacketChannel channel)
        {
            channel.BeginCommand(); await Query(channel, "select prefix");
            Assert.Equal(90, (await StatementCommand(channel, 23))[1]);
        }

        private static async Task LimitedMultiSendHiddenReply(PacketChannel channel)
        {
            channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(Column("hidden", 253)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(new byte[] { 0, 0, 1, 120 }); await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        [InlineData(false, true, true)] [InlineData(true, true, true)]
        public async Task BatchMultiSendLimitsKeepLobWindowAndSkipHiddenHandles(bool asynchronous, bool character, bool owned)
        {
            var code = character ? (byte)211 : (byte)210;
            var data = Encoding.UTF8.GetBytes("患者🙂");
            var server = Server(async channel =>
            {
                await BatchLobPrepared(channel, 90, code); await BatchLobPrepared(channel, 91, code);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("lob", code)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(WithLength(InlineLob(data))).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 4 }); await channel.WritePacketAsync(Eof());
                await BatchLobReply(channel, code, InlineLob(data));
                await BatchReturningRequest(channel, 1); await ReturningResourceReply(channel, 92, code, WithLength(InlineLob(data)));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            var handles = new List<OceanBaseLob>();
            try
            {
                if (!owned) await connection.OpenAsync();
                using var batch = owned ? new OceanBaseBatch(connection) : connection.CreateBatch();
                BatchItem(batch, "select first"); BatchItem(batch, "select hidden");
                var output = BatchReturningItem(batch, 1); output.ResetDbType(); output.OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                var behavior = CommandBehavior.SingleRow | CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection;
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); handles.Add(reader.GetFieldValue<OceanBaseLob>(0));
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                var session = connection.GetOpenSession();
                Assert.Equal(1, reader.RecordsAffected); handles.Add(Assert.IsType<OceanBaseLob>(output.Value));
                foreach (var lob in handles)
                {
                    if (character) { using var text = lob.OpenTextReader(); Assert.Equal(Encoding.UTF8.GetString(data), await text.ReadToEndAsync()); }
                    else { using var source = lob.OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target); Assert.Equal(data, target.ToArray()); }
                }
                var context = handles[0].Context; var locator = handles[0].BindingBytes();
                foreach (var lob in handles) lob.Dispose();
                Assert.False(session.RequiresDiscardOnReturn);
                using var stale = new OceanBaseLob(context, locator, character);
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Throws<InvalidOperationException>(() => { if (character) stale.OpenTextReader(); else stale.OpenRead(); });
            }
            finally { foreach (var lob in handles) lob.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendDefaultExposesMultipleResultsWithinOneRequest(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await LimitedMultiSendPrepare(channel); await LimitedMultiSendGroup(channel);
                channel.BeginResponse(); await LimitedResultPrefix(channel); await LimitedMultiSendHiddenReply(channel);
                await StatementCommand(channel, 23); channel.BeginResponse(); await channel.WritePacketAsync(Ok(3));
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                BatchItem(batch, "select prefix"); BatchItem(batch, "select hidden"); BatchItem(batch, "update t set id=2");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("alpha", reader.GetString(0));
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(0, reader.FieldCount);
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("hidden", reader.GetString(0));
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("x", reader.GetString(0));
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(0, reader.FieldCount);
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(5, reader.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendLimitsRejectReturningTailWithAdditionalResultsBeforePublishing(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await BatchReturningRequest(channel, 1); await BatchReturningReply(channel, 90, 7, tail: false);
                await channel.WritePacketAsync(Ok(1, 14));
                try { await channel.WritePacketAsync(Error()); Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var output = BatchReturningItem(batch, 1);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SingleResult) : batch.ExecuteReader(CommandBehavior.SingleResult);
                    if (asynchronous) await reader.NextResultAsync(); else reader.NextResult();
                });
                Assert.Equal(-1L, output.Value); Assert.Same(batch.BatchCommands[0], error.BatchCommand);
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [MemberData(nameof(LimitedMultiSendCases))]
        public async Task BatchMultiSendLimitsHideLaterResultsAndKeepTerminalCounts(bool asynchronous, CommandBehavior limit, bool sequential, int advance)
        {
            var server = Server(async channel =>
            {
                await LimitedMultiSendPrepare(channel); await LimitedMultiSendGroup(channel);
                channel.BeginResponse(); await LimitedResultPrefix(channel); await LimitedMultiSendHiddenReply(channel);
                Assert.Equal(91, (await StatementCommand(channel, 23))[1]); channel.BeginResponse(); await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after limited pipeline"); await channel.WritePacketAsync(Ok(7));
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                BatchItem(batch, "select prefix"); BatchItem(batch, "select hidden"); BatchItem(batch, "update t set id=2");
                var behavior = limit | (sequential ? CommandBehavior.SequentialAccess : 0);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                if (advance != 0)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    using (var text = reader.GetTextReader(0)) Assert.Equal('a', text.Read());
                    if (advance == 2)
                    {
                        if ((limit & CommandBehavior.SingleRow) == 0)
                        {
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("bravo", reader.GetString(0));
                        }
                        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    }
                }
                Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected);
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(new[] { 2, -1, 3 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
                Assert.Equal(5, reader.RecordsAffected); Assert.Equal(0, reader.FieldCount);
                Assert.False(reader.Read()); Assert.False(await reader.NextResultAsync());
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                using var after = connection.CreateCommand(); after.CommandText = "after limited pipeline"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendLimitsPreserveHiddenErrorAttributionAndSuccessfulCounts(bool asynchronous, bool sameCommand)
        {
            var server = Server(async channel =>
            {
                await LimitedMultiSendPrepare(channel); await LimitedMultiSendGroup(channel);
                channel.BeginResponse();
                if (sameCommand)
                {
                    await BatchTextResult(channel, "alpha", 14); await channel.WritePacketAsync(Ok(2, 14)); await channel.WritePacketAsync(Error());
                }
                else { await LimitedResultPrefix(channel); channel.BeginResponse(); await channel.WritePacketAsync(Error()); }
                if (!sameCommand) { await StatementCommand(channel, 25); await StatementCommand(channel, 25); }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var prefix = BatchItem(batch, "select prefix"); var hidden = BatchItem(batch, "select hidden"); var suffix = BatchItem(batch, "update t set id=2");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SingleResult) : batch.ExecuteReader(CommandBehavior.SingleResult);
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => reader.NextResultAsync()) : Assert.Throws<OceanBaseException>(() => reader.NextResult());
                Assert.Same(sameCommand ? prefix : hidden, error.BatchCommand); Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(2, prefix.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected); Assert.True(reader.IsClosed); batch.Timeout = 4;
                Assert.Equal(sameCommand ? ConnectionState.Broken : ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendLimitsHiddenCancellationKeepsNextResultTokenAndPrefix(bool nextGroup, bool batchCancel)
        {
            var server = Server(async channel =>
            {
                await LimitedMultiSendPrepare(channel); await StatementCommand(channel, 22); await Prepared(channel, 92, 0);
                await LimitedMultiSendGroup(channel); channel.BeginResponse(); await LimitedResultPrefix(channel);
                if (nextGroup)
                {
                    await LimitedMultiSendHiddenReply(channel); await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                }
                channel.BeginResponse(); await channel.WritePacketAsync(Ok(0, 14));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var prefix = BatchItem(batch, "select prefix"); var hidden = BatchItem(batch, "select hidden");
                var third = BatchItem(batch, "select pending"); var suffix = BatchItem(batch, "update t set id=2");
                var pending = nextGroup ? third : hidden;
                using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SingleRow | CommandBehavior.SingleResult | CommandBehavior.SequentialAccess);
                var next = reader.NextResultAsync(cancellation.Token);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (pending.RecordsAffected != 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal(0, pending.RecordsAffected); Assert.Equal(2, prefix.RecordsAffected);
                if (batchCancel) batch.Cancel(); else cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => next.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Same(pending, error.BatchCommand); Assert.True(error.IsExecutionOutcomeUnknown); Assert.True(reader.IsClosed);
                if (!batchCancel) Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(-1, suffix.RecordsAffected); Assert.Equal(ConnectionState.Broken, connection.State);
                batch.Timeout = 4;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendLimitsDoNotSearchBeyondEmptyOrCountFirstResult(bool asynchronous, bool count)
        {
            var server = Server(async channel =>
            {
                await LimitedMultiSendPrepare(channel); await LimitedMultiSendGroup(channel);
                channel.BeginResponse(); await LimitedResultPrefix(channel, count ? 2 : 1); await LimitedMultiSendHiddenReply(channel);
                Assert.Equal(91, (await StatementCommand(channel, 23))[1]); channel.BeginResponse(); await channel.WritePacketAsync(Ok(3));
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                BatchItem(batch, "select prefix"); BatchItem(batch, "select hidden"); BatchItem(batch, "update t set id=2");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SingleRow) : batch.ExecuteReader(CommandBehavior.SingleRow);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(5, reader.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
