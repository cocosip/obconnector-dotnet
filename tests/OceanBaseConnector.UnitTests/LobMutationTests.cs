using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task LobReadSnapshotsInlineBytesOnlyAfterReservingTheSession(bool character)
        {
            var type = character ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, type, InlineLob(Encoding.UTF8.GetBytes("old")));
                await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 0xa1);
                await LobMutationReply(channel, type, InlineLob(Encoding.UTF8.GetBytes("new"))); await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            Thread? opening = null;
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                var result = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var gate = typeof(OceanBaseConnection).GetField("_gate", flags)!.GetValue(connection)!;
                var opened = typeof(OceanBaseLob).GetField("_opened", flags)!;
                await Task.Run(() =>
                {
                    lock (gate)
                    {
                        opening = new Thread(() =>
                        {
                            try { result.TrySetResult(character ? (IDisposable)lob.OpenTextReader() : lob.OpenRead()); }
                            catch (Exception error) { result.TrySetException(error); }
                        }) { IsBackground = true };
                        opening.Start();
                        // Hold admission while the read waits, then overwrite on this reentrant owner thread.
                        Assert.True(SpinWait.SpinUntil(() => (int)opened.GetValue(lob)! == 1 &&
                            (opening.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                        if (character) lob.WriteText(0, "new");
                        else lob.WriteBytes(0, Encoding.UTF8.GetBytes("new"), 0, 3);
                    }
                }).WaitAsync(TimeSpan.FromSeconds(10));
                using var source = await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (character) Assert.Equal("new", ((TextReader)source).ReadToEnd());
                else { using var destination = new MemoryStream(); ((Stream)source).CopyTo(destination); Assert.Equal("new", Encoding.UTF8.GetString(destination.ToArray())); }
            }
            finally { connection.Close(); opening?.Join(TimeSpan.FromSeconds(5)); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task LobMutationReply(PacketChannel channel, byte type, byte[]? locator, bool error = false, int parameters = 4)
        {
            await channel.WritePacketAsync(OracleExecuteHeader((ushort)parameters, 1, 4));
            for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(Column("p" + i, 253));
            await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Column("lob", type)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(locator == null ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(WithLength(locator)).ToArray());
            await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(error ? Error() : Ok());
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task LobMutationReservationPersistsBetweenCompletedChunks(bool async)
        {
            var phase = 0; Exception? probeError = null; OceanBaseLob? lob = null; OceanBaseConnection? connection = null;
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(0)); await OracleVersionReply(channel, "4.3.5.0");
                await StatementCommand(channel, 0xa1); Volatile.Write(ref phase, 1);
                await LobMutationReply(channel, 210, ExternalLob(32767)); await StatementCommand(channel, 25); channel.BeginCommand();
                await StatementCommand(channel, 0xa1); await LobMutationReply(channel, 210, ExternalLob(40000));
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            var log = new RecordingLoggerFactory { OnLog = id =>
            {
                if (id.Id != 1100 || Interlocked.CompareExchange(ref phase, 2, 1) != 1) return;
                try
                {
                    Assert.Throws<InvalidOperationException>(() => lob!.Truncate(0));
                    Assert.Throws<InvalidOperationException>(() => lob!.OpenRead());
                    using var other = connection!.CreateCommand(); other.CommandText = "must not execute";
                    Assert.Throws<InvalidOperationException>(() => other.ExecuteNonQuery());
                    Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                }
                catch (Exception error) { probeError = error; }
            } };
            using (connection = new OceanBaseConnection(LobConfiguration(server.Port), log))
            {
                try
                {
                    await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                    using (lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!)
                    {
                        if (async) await lob.WriteBytesAsync(0, new byte[40000], 0, 40000); else lob.WriteBytes(0, new byte[40000], 0, 40000);
                        Assert.Equal(2, Volatile.Read(ref phase)); Assert.Null(probeError); Assert.Equal(40000, lob.ByteLength);
                    }
                    using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
                }
                finally { connection.Close(); server.Stop(); }
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task LobWriteChunksUseReturnedLocatorAndByteOrScalarOffsets(bool async, bool character)
        {
            var text = string.Concat(Enumerable.Repeat("患者😀a", 9000));
            var data = character ? Encoding.UTF8.GetBytes(text) : Enumerable.Range(0, 80000).Select(i => (byte)i).ToArray();
            var initial = ExternalLob(5); var type = character ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, type, initial); await OracleVersionReply(channel, "4.3.5.0");
                var sent = 0; long offset = 3; var locator = initial;
                while (sent < data.Length)
                {
                    var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.Skip(10);
                    Assert.Equal("BEGIN DBMS_LOB.WRITE(?, ?, ?, ?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                    Assert.Equal(4u, request.ReadUInt32()); Assert.Equal(0, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                    Assert.Equal((ushort)type, request.ReadUInt16()); Assert.Equal((ushort)3, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal((ushort)253, request.ReadUInt16());
                    Assert.Equal(locator, request.ReadLengthEncodedBytes()); var amount = request.ReadUInt32(); Assert.Equal((ulong)offset, request.ReadUInt64());
                    Assert.InRange(amount, 1u, character ? 8191u : 32767u); var chunk = request.ReadLengthEncodedBytes();
                    Assert.Equal(data.AsSpan(sent, chunk.Length).ToArray(), chunk);
                    Assert.Equal((long)amount, character ? Encoding.UTF8.GetString(chunk).EnumerateRunes().LongCount() : chunk.LongLength);
                    sent += chunk.Length; offset += amount; locator = ExternalLob(2 + sent); locator[16] = (byte)(sent % 251);
                    await LobMutationReply(channel, type, locator); await StatementCommand(channel, 25); channel.BeginCommand();
                }
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                if (character) { if (async) await lob.WriteTextAsync(2, text); else lob.WriteText(2, text); }
                else { if (async) await lob.WriteBytesAsync(2, data, 0, data.Length); else lob.WriteBytes(2, data, 0, data.Length); }
                Assert.Equal(2L + data.Length, lob.ByteLength);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("error")] [InlineData("null")] [InlineData("disconnect")]
        [InlineData("wrong-type")] [InlineData("malformed")]
        public async Task LobFailedWriteDoesNotReuseAStaleLocatorOrReplayCompletedPrefix(string fault)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(0)); await OracleVersionReply(channel, "4.3.5.0");
                await StatementCommand(channel, 0xa1); await LobMutationReply(channel, 210, ExternalLob(32767));
                await StatementCommand(channel, 25); channel.BeginCommand(); await StatementCommand(channel, 0xa1);
                if (fault == "disconnect") return;
                await LobMutationReply(channel, fault == "wrong-type" ? (byte)211 : (byte)210,
                    fault == "null" ? null : fault == "malformed" ? new byte[] { 1, 2, 3 } : ExternalLob(40000), fault == "error");
                if (fault == "error")
                {
                    await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
                }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => lob.WriteBytesAsync(0, new byte[40000], 0, 40000));
                Assert.Equal(fault == "disconnect" || fault == "wrong-type" || fault == "malformed", error.IsExecutionOutcomeUnknown); Assert.Equal(32767, lob.ByteLength);
                Assert.Throws<ObjectDisposedException>(() => lob.OpenRead());
                if (fault == "error") { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
                else Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task LobTruncateRefreshesInlineContentAndPreservesReadOwnership(bool async, bool character)
        {
            var data = Encoding.UTF8.GetBytes("患者😀a"); var result = character ? Encoding.UTF8.GetBytes("患者😀") : data.Take(5).ToArray();
            var type = character ? (byte)211 : (byte)210; var length = character ? 3L : 5L;
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, type, InlineLob(data)); await OracleVersionReply(channel, "4.3.5.0");
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.Skip(10);
                Assert.Equal("BEGIN DBMS_LOB.TRIM(?, ?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                Assert.Equal(2u, request.ReadUInt32()); Assert.Equal(0, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                Assert.Equal((ushort)type, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16());
                Assert.Equal(InlineLob(data), request.ReadLengthEncodedBytes()); Assert.Equal((ulong)length, request.ReadUInt64());
                await LobMutationReply(channel, type, InlineLob(result), parameters: 2); await StatementCommand(channel, 25); channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); using var lob = reader.GetFieldValue<OceanBaseLob>(0);
                Assert.Throws<InvalidOperationException>(() => lob.Truncate(0)); Assert.False(await reader.ReadAsync());
                if (async) await lob.TruncateAsync(length); else lob.Truncate(length); Assert.Equal(result.Length, lob.ByteLength);
                using (var source = character ? (IDisposable)lob.OpenTextReader() : lob.OpenRead())
                {
                    Assert.Throws<InvalidOperationException>(() => lob.Truncate(0));
                    if (character) Assert.Equal("患者😀", ((TextReader)source).ReadToEnd());
                    else { using var target = new MemoryStream(); ((Stream)source).CopyTo(target); Assert.Equal(result, target.ToArray()); }
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task LobMutationValidatesAllInputBeforeSendingAnyChunk(bool character)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, InlineLob(Array.Empty<byte>()));
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                Assert.Throws<ArgumentOutOfRangeException>(() => lob.Truncate(-1));
                if (character)
                {
                    Assert.Throws<EncoderFallbackException>(() => lob.WriteText(0, new string('x', 10000) + "\ud800"));
                    Assert.Throws<ArgumentOutOfRangeException>(() => lob.WriteText(long.MaxValue, "a"));
                    Assert.Throws<ArgumentNullException>(() => lob.WriteText(0, null!));
                    Assert.Throws<InvalidOperationException>(() => lob.WriteBytes(0, Array.Empty<byte>(), 0, 0));
                    await lob.WriteTextAsync(long.MaxValue, "");
                }
                else
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => lob.WriteBytes(long.MaxValue, new byte[1], 0, 1));
                    Assert.Throws<ArgumentOutOfRangeException>(() => lob.WriteBytes(0, new byte[1], 0, 2));
                    Assert.Throws<ArgumentOutOfRangeException>(() => lob.WriteBytes(-1, new byte[1], 0, 1));
                    Assert.Throws<ArgumentNullException>(() => lob.WriteBytes(0, null!, 0, 0));
                    Assert.Throws<InvalidOperationException>(() => lob.WriteText(0, "")); await lob.WriteBytesAsync(long.MaxValue, Array.Empty<byte>(), 0, 0);
                }
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lob.TruncateAsync(0, canceled.Token)); Assert.Equal(canceled.Token, error.CancellationToken);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "dispose")] [InlineData(true, "dispose")] [InlineData(true, "cancel")]
        [InlineData(false, "dispose-async")] [InlineData(true, "dispose-async")]
        [InlineData(false, "timeout")] [InlineData(true, "timeout")]
        public async Task LobMutationInterruptionRetainsWriteOutcomeAndInvalidatesHandle(bool async, string cause)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(0)); await OracleVersionReply(channel, "4.3.5.0");
                await StatementCommand(channel, 0xa1); started.TrySetResult(); await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value"; command.CommandTimeout = cause == "timeout" ? 1 : 30;
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; using var caller = new CancellationTokenSource();
                var executing = async ? lob.WriteBytesAsync(0, new byte[1], 0, 1, caller.Token) : Task.Run(() => lob.WriteBytes(0, new byte[1], 0, 1));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (cause == "dispose") lob.Dispose(); else if (cause == "dispose-async") await lob.DisposeAsync(); else if (cause == "cancel") caller.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (cause == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                else Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                if (cause == "cancel") Assert.Equal(caller.Token, ((OperationCanceledException)error.InnerException!).CancellationToken);
                Assert.Throws<ObjectDisposedException>(() => lob.OpenRead()); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(2147483648L)] [InlineData(long.MaxValue - 1)]
        public async Task LobWriteKeepsTheFullInt64Offset(long offset)
        {
            var locator = ExternalLob(0); System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(locator.AsSpan(28), offset + 1);
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(0)); await OracleVersionReply(channel, "4.3.5.0");
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.Skip(10); request.ReadLengthEncodedBytes(); request.Skip(14); request.ReadLengthEncodedBytes();
                Assert.Equal(1u, request.ReadUInt32()); Assert.Equal((ulong)offset + 1, request.ReadUInt64());
                await LobMutationReply(channel, 210, locator); await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; await lob.WriteBytesAsync(offset, new byte[1], 0, 1); Assert.Equal(offset + 1, lob.ByteLength);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
