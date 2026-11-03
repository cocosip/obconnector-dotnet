using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> SequentialMultiSendCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var binaryFirst in new[] { false, true })
                    foreach (var ownership in new[] { 0, 1, 2 })
                        yield return new object[] { asynchronous, binaryFirst, ownership };
        }

        private static async Task SequentialBatchPrepare(PacketChannel channel, bool binary, bool first)
        {
            await StatementCommand(channel, 22);
            if (!binary) { await channel.WritePacketAsync(UnsupportedPrepare()); return; }
            await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, first ? (byte)2 : (byte)1, 0, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(Column("value", first ? (byte)252 : (byte)253, first ? (ushort)63 : (ushort)45));
            if (first) await channel.WritePacketAsync(Column("id", 8));
            await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendSequentialTruncatedFieldReleasesOperation(bool asynchronous, bool materialize)
        {
            var server = MySqlBehaviorServer(async channel =>
            {
                await SequentialBatchPrepare(channel, true, false);
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("value", 252, 63)); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
                await wire.WriteAsync(FrameHeader(8, 4)); await wire.WriteAsync(new byte[] { 0, 0, 5, 1, 2 });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var item = BatchItem(batch, "select bytes"); BatchItem(batch, "update t set id=2");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : batch.ExecuteReader(CommandBehavior.SequentialAccess);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    if (materialize) { if (asynchronous) await reader.GetFieldValueAsync<byte[]>(0); else reader.GetValue(0); }
                    else
                    {
                        using var stream = reader.GetStream(0); var buffer = new byte[5];
                        if (asynchronous) await stream.ReadExactlyAsync(buffer); else stream.ReadExactly(buffer);
                    }
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(item, error.BatchCommand);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(reader.IsClosed);
                batch.BatchCommands.Clear(); Assert.Empty(batch.BatchCommands);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "token")] [InlineData(true, "token")]
        [InlineData(false, "batch")] [InlineData(true, "batch")]
        [InlineData(false, "close")] [InlineData(true, "close")]
        public async Task BatchMultiSendSequentialBlockedFieldCancellationReleasesOperation(bool text, string origin)
        {
            var server = MySqlBehaviorServer(async channel =>
            {
                await SequentialBatchPrepare(channel, true, false);
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("value", text ? (byte)253 : (byte)252, text ? (ushort)45 : (ushort)63)); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
                await wire.WriteAsync(FrameHeader(6, 4)); await wire.WriteAsync(new byte[] { 0, 0, 3 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var item = BatchItem(batch, "select value"); BatchItem(batch, "update t set id=2");
                using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                using var chars = text ? reader.GetTextReader(0) : null;
                using var bytes = text ? null : reader.GetStream(0);
                if (origin == "close")
                {
                    await reader.CloseAsync();
                    if (text) Assert.Throws<InvalidOperationException>(() => chars!.Read());
                    else Assert.Throws<InvalidOperationException>(() => bytes!.ReadByte());
                }
                else
                {
                    var pending = text ? chars!.ReadAsync(new char[1].AsMemory(), cancellation.Token).AsTask()
                        : bytes!.ReadAsync(new byte[1], 0, 1, cancellation.Token);
                    Assert.False(pending.IsCompleted);
                    if (origin == "token") cancellation.Cancel(); else batch.Cancel();
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(item, error.BatchCommand);
                    if (origin == "token") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                }
                Assert.True(reader.IsClosed); Assert.Equal(ConnectionState.Broken, connection.State);
                batch.BatchCommands.Clear(); Assert.Empty(batch.BatchCommands);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [MemberData(nameof(SequentialMultiSendCases))]
        public async Task BatchMultiSendSequentialStreamsBeforeBodyAndDrainsAcrossGroups(bool asynchronous, bool binaryFirst, int ownership)
        {
            var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlBehaviorServer(async channel =>
            {
                await SequentialBatchPrepare(channel, binaryFirst, true);
                await SequentialBatchPrepare(channel, !binaryFirst, false);
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                if (binaryFirst) await StatementCommand(channel, 23); else { channel.BeginCommand(); await Query(channel, "select first"); }
                if (!binaryFirst) await StatementCommand(channel, 23); else { channel.BeginCommand(); await Query(channel, "select second"); }
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("value", 252, 63)); await channel.WritePacketAsync(Column("id", 8)); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
                var row = (binaryFirst ? new byte[] { 0, 0, 3, 65, 66, 67 }.Concat(BitConverter.GetBytes(42L))
                    : new byte[] { 3, 65, 66, 67, 2, 52, 50 }).ToArray();
                var prefix = binaryFirst ? 4 : 2;
                await wire.WriteAsync(FrameHeader(row.Length, 5)); await wire.WriteAsync(row.AsMemory(0, prefix));
                await releaseBody.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await wire.WriteAsync(row.AsMemory(prefix)); await wire.WriteAsync(FrameHeader(5, 6)); await wire.WriteAsync(Eof());
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("value", 253)); await channel.WritePacketAsync(Eof());
                var text = Encoding.UTF8.GetBytes("A中😀Z");
                await channel.WritePacketAsync((binaryFirst ? Array.Empty<byte>() : new byte[] { 0, 0 }).Concat(new[] { (byte)text.Length }).Concat(text).ToArray());
                await channel.WritePacketAsync(binaryFirst ? new byte[] { 251 } : new byte[] { 0, 4 }); await channel.WritePacketAsync(Eof());
                Assert.Equal(91, (await StatementCommand(channel, 23))[1]);
                channel.BeginResponse(); await channel.WritePacketAsync(Ok(5));
                if (ownership == 0)
                {
                    channel.BeginCommand(); await Query(channel, "after sequential"); await channel.WritePacketAsync(Ok(7));
                    await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                }
                else Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                if (ownership != 2) await connection.OpenAsync();
                using var batch = ownership == 2 ? new OceanBaseBatch(connection) : connection.CreateBatch();
                BatchItem(batch, "select first"); BatchItem(batch, "select second"); BatchItem(batch, "update t set id=2");
                var behavior = CommandBehavior.SequentialAccess | (ownership == 1 ? CommandBehavior.CloseConnection : 0);
                using var reader = await (asynchronous ? batch.ExecuteReaderAsync(behavior) : Task.Run(() => batch.ExecuteReader(behavior))).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(reader.HasRows); Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                using var bytes = reader.GetStream(0); var buffer = new byte[1];
                Assert.Equal(1, asynchronous ? await bytes.ReadAsync(buffer, 0, 1) : bytes.Read(buffer, 0, 1)); Assert.Equal(65, buffer[0]);
                Assert.False(releaseBody.Task.IsCompleted); releaseBody.SetResult();
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Throws<InvalidOperationException>(() => bytes.Read(buffer, 0, 1));
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                using var text = reader.GetTextReader(0); Assert.Equal('A', text.Read());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Throws<InvalidOperationException>(() => text.Read());
                Assert.True(asynchronous ? await reader.IsDBNullAsync(0) : reader.IsDBNull(0));
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(0, reader.FieldCount);
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(new[] { -1, -1, 5 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
                Assert.Equal(5, reader.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ownership == 0 ? ConnectionState.Open : ConnectionState.Closed, connection.State);
                if (ownership == 0)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after sequential"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
                }
            }
            finally { releaseBody.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
