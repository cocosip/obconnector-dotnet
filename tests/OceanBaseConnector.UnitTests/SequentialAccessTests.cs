using System;
using System.Data;
using System.Data.Common;
using System.IO;
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
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task SequentialFieldsKeepOffsetsNullsAndSessionReuse(bool asynchronous, bool binary, bool deprecated)
        {
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22, deprecated); await Prepared(channel, 90, 0, deprecated: deprecated); await StatementCommand(channel, 23, deprecated); }
                else await Query(channel, "sequential", deprecated);
                await channel.WritePacketAsync(new byte[] { 4 });
                await channel.WritePacketAsync(Column("n", 8));
                await channel.WritePacketAsync(Column("bytes", 252, 63));
                await channel.WritePacketAsync(Column("text", 253));
                await channel.WritePacketAsync(Column("nullable", 253));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                using var row = new MemoryStream();
                if (binary) { row.Write(new byte[] { 0, 32 }); row.Write(BitConverter.GetBytes(42L)); }
                else row.Write(new byte[] { 2, 52, 50 });
                row.Write(new byte[] { 5, 10, 11, 12, 13, 14 });
                var text = Encoding.UTF8.GetBytes("A中😀Z"); row.WriteByte((byte)text.Length); row.Write(text);
                if (!binary) row.WriteByte(251);
                await channel.WritePacketAsync(row.ToArray());
                await channel.WritePacketAsync(deprecated ? new byte[] { 254, 0, 0, 6, 0, 0, 0 } : Eof());
                channel.BeginCommand(); await Query(channel, "after", deprecated); await channel.WritePacketAsync(Ok(7));
                if (binary) await StatementCommand(channel, 25, deprecated);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "sequential";
                if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                using (var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    Assert.True(reader.HasRows); Assert.Equal(4, reader.GetColumnSchema().Count);
                    Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(42L, asynchronous ? await reader.GetFieldValueAsync<long>(0) : reader.GetInt64(0));
                    Assert.Equal(42L, reader.GetInt64(0));
                    Assert.Equal(5, reader.GetBytes(1, 0, null, 0, 0));
                    var buffer = new byte[2]; Assert.Equal(2, reader.GetBytes(1, 1, buffer, 0, 2)); Assert.Equal(new byte[] { 11, 12 }, buffer);
                    Assert.Throws<InvalidOperationException>(() => reader.GetBytes(1, 0, buffer, 0, 1));
                    Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
                    using var stream = reader.GetStream(1);
                    Assert.Equal(2, asynchronous ? await stream.ReadAsync(buffer, 0, 2) : stream.Read(buffer, 0, 2));
                    Assert.Equal(new byte[] { 13, 14 }, buffer);
                    Assert.Equal(0, asynchronous ? await stream.ReadAsync(buffer, 0, 2) : stream.Read(buffer, 0, 2));
                    Assert.Throws<InvalidOperationException>(() => reader.GetValue(1));
                    Assert.Throws<NotSupportedException>(() => reader.GetChars(2, 0, null, 0, 0));
                    var chars = new char[5]; Assert.Equal(2, reader.GetChars(2, 0, chars, 0, 2));
                    Assert.Equal(1, reader.GetChars(2, 2, chars, 2, 1)); Assert.Equal(2, reader.GetChars(2, 3, chars, 3, 2));
                    Assert.Equal("A中😀Z", new string(chars));
                    Assert.Throws<InvalidOperationException>(() => reader.GetChars(2, 0, chars, 0, 1));
                    Assert.True(asynchronous ? await reader.IsDBNullAsync(3) : reader.IsDBNull(3)); Assert.Equal(DBNull.Value, reader.GetValue(3));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Throws<InvalidOperationException>(() => stream.Read(buffer, 0, 1));
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task SequentialStreamReturnsBeforeBodyAndDoesNotAllocateWholeContinuationRow()
        {
            const int length = 2 * PacketChannel.MaxFramePayload + 17;
            var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var wire = client.GetStream(); var channel = new PacketChannel(wire);
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(TimeZoneOk("+00:00")); await ServerIdentity.ReplyAsync(channel); channel.BeginCommand(); await Query(channel, "large");
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("large", 252, 63)); await channel.WritePacketAsync(Eof());
                await wire.WriteAsync(FrameHeader(PacketChannel.MaxFramePayload, 4));
                var prefix = new byte[9]; prefix[0] = 254; BitConverter.GetBytes((long)length).CopyTo(prefix, 1); await wire.WriteAsync(prefix);
                await releaseBody.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var chunk = new byte[8192]; Array.Fill(chunk, (byte)42);
                var remaining = length; var frameRemaining = PacketChannel.MaxFramePayload - prefix.Length; var sequence = 4;
                while (remaining != 0)
                {
                    if (frameRemaining == 0) { frameRemaining = Math.Min(remaining, PacketChannel.MaxFramePayload); await wire.WriteAsync(FrameHeader(frameRemaining, ++sequence)); }
                    var size = Math.Min(chunk.Length, Math.Min(remaining, frameRemaining));
                    await wire.WriteAsync(chunk.AsMemory(0, size)); remaining -= size; frameRemaining -= size;
                }
                await wire.WriteAsync(FrameHeader(5, ++sequence)); await wire.WriteAsync(Eof());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "large";
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(reader.HasRows); Assert.True(await reader.ReadAsync());
                using var stream = reader.GetStream(0); var buffer = new byte[8192]; releaseBody.SetResult();
                var allocated = GC.GetAllocatedBytesForCurrentThread(); long count = 0; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) count += read;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.Equal(length, count); Assert.Equal(42, buffer[0]); Assert.InRange(allocated, 0, 1024 * 1024);
                Assert.False(await reader.ReadAsync());
            }
            finally { releaseBody.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        public async Task SequentialCachedFieldsStillObserveCommandLifetime(bool text, string origin)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cached";
                var transport = new ProtocolFaultStream(SequentialResponse(new byte[] { 3, 65, 66, 67 }, text));
                InstallFaultStream(connection, transport);
                using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess); Assert.True(reader.Read());
                using var chars = text ? reader.GetTextReader(0) : null;
                using var bytes = text ? null : reader.GetStream(0);
                if (text) Assert.Equal('A', chars!.Peek());
                else Assert.Equal(3, bytes!.Read(new byte[3], 0, 3));
                if (origin == "close") connection.Close(); else command.Cancel();
                var error = Assert.Throws<OceanBaseException>(() => { if (text) _ = chars!.Read(); else _ = bytes!.Read(new byte[1], 0, 1); });
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(origin == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SequentialFullyReceivedTerminatorWinsOverCancellation(bool asynchronous, bool serverError)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "terminal";
                var transport = new ProtocolFaultStream(SequentialResponse(new byte[] { 1, 65 }, true, serverError ? Error() : Eof()));
                transport.AfterInputConsumed = command.Cancel; InstallFaultStream(connection, transport);
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (serverError)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? reader.ReadAsync() : Task.FromResult(reader.Read()));
                    Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(1064, error.ServerErrorCode);
                }
                else Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SequentialAsyncFieldAccessUsesOnlyAsyncTransport(bool text)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "async";
                var transport = new ProtocolFaultStream(SequentialResponse(new byte[] { 3, 65, 66, 67 }, text)); InstallFaultStream(connection, transport);
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                if (text)
                {
                    using var chars = await reader.GetFieldValueAsync<TextReader>(0); var buffer = new char[3];
                    Assert.Equal(3, await chars.ReadAsync(buffer.AsMemory())); Assert.Equal("ABC", new string(buffer));
                }
                else
                {
                    using var stream = await reader.GetFieldValueAsync<Stream>(0); var buffer = new byte[3];
                    await stream.ReadExactlyAsync(buffer); Assert.Equal(new byte[] { 65, 66, 67 }, buffer);
                }
                Assert.False(await reader.ReadAsync()); Assert.Equal(0, transport.SyncCalls); Assert.True(transport.AsyncCalls > 0);
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
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        public async Task SequentialBlockedFieldReadDiscardsSessionWithoutReplay(bool text, string origin)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "blocked"); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("value", text ? (byte)253 : (byte)252, text ? (ushort)45 : (ushort)63)); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
                await wire.WriteAsync(FrameHeader(4, 4)); await wire.WriteAsync(new byte[] { 3 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var cancellation = new CancellationTokenSource(); using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "blocked"; command.CommandTimeout = origin == "timeout" ? 1 : 10;
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                using var chars = text ? await reader.GetFieldValueAsync<TextReader>(0) : null;
                using var bytes = text ? null : await reader.GetFieldValueAsync<Stream>(0);
                var pending = text ? chars!.ReadAsync(new char[1].AsMemory(), cancellation.Token).AsTask() : bytes!.ReadAsync(new byte[1], 0, 1, cancellation.Token);
                Assert.False(pending.IsCompleted);
                if (origin == "token") cancellation.Cancel(); else if (origin == "cancel") command.Cancel(); else if (origin == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                else if (origin != "close") Assert.IsType<OperationCanceledException>(error.InnerException);
                Assert.Equal(origin == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("column")]
        [InlineData("row")]
        [InlineData("result")]
        [InlineData("close")]
        [InlineData("closeConnection")]
        public async Task SequentialAdvanceDrainsUnreadDataAndInvalidatesStreams(string transition)
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Query(channel, "transitions"); await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("a", 253)); await channel.WritePacketAsync(Column("b", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 3, 65, 66, 67, 1, 68 });
                await channel.WritePacketAsync(new byte[] { 1, 69, 1, 70 }); await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("c", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 71 }); await channel.WritePacketAsync(Eof());
                sent.TrySetResult();
                try { Assert.Equal(0, await channelStreamRead(channel)); }
                catch (IOException) when (transition.StartsWith("close", StringComparison.Ordinal)) { }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "transitions";
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | (transition == "closeConnection" ? CommandBehavior.CloseConnection : 0));
                Assert.True(await reader.ReadAsync()); using var text = reader.GetTextReader(0); Assert.Equal('A', text.Read());
                if (transition == "column") Assert.Equal("D", reader.GetString(1));
                else if (transition == "row") { Assert.True(await reader.ReadAsync()); Assert.Equal("F", await reader.GetFieldValueAsync<string>(1)); }
                else if (transition == "result") { Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal("G", reader.GetString(0)); }
                else { await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)); await reader.CloseAsync(); }
                Assert.Throws<InvalidOperationException>(() => text.Read());
                if (transition.StartsWith("close", StringComparison.Ordinal))
                    Assert.Equal(transition == "closeConnection" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                else
                {
                    if (transition != "result") { Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); }
                    Assert.False(await reader.NextResultAsync()); Assert.Equal(ConnectionState.Open, connection.State);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(new byte[] { 252, 1 })]
        [InlineData(new byte[] { 253, 255, 255, 255 })]
        [InlineData(new byte[] { 254, 255, 255, 255, 255, 255, 255, 255, 255 })]
        [InlineData(new byte[] { 1, 65, 66 })]
        public async Task SequentialMalformedFieldCannotReturnSessionToService(byte[] field)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "malformed";
                InstallFaultStream(connection, new ProtocolFaultStream(SequentialResponse(field, true)));
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                    Assert.True(await reader.ReadAsync()); await reader.NextResultAsync();
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SequentialTextReaderPreservesBomSurrogatesAndStrictUtf8(bool malformed)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "utf8";
                var text = malformed ? new byte[] { 0xC0, 0x80 } : Encoding.UTF8.GetBytes("\uFEFFA中😀Z");
                InstallFaultStream(connection, new ProtocolFaultStream(SequentialResponse(WithLength(text), true)));
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                using var chars = await reader.GetFieldValueAsync<TextReader>(0);
                if (malformed) await Assert.ThrowsAsync<DecoderFallbackException>(() => chars.ReadToEndAsync());
                else Assert.Equal("\uFEFFA中😀Z", await chars.ReadToEndAsync());
                Assert.False(await reader.ReadAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SequentialCachedFieldAsyncReadHonorsItsOwnCanceledToken(bool text)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "field token";
                InstallFaultStream(connection, new ProtocolFaultStream(SequentialResponse(new byte[] { 3, 65, 66, 67 }, text)));
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                using var chars = text ? await reader.GetFieldValueAsync<TextReader>(0) : null;
                using var bytes = text ? null : await reader.GetFieldValueAsync<Stream>(0);
                if (text) Assert.Equal('A', chars!.Peek()); else await bytes!.ReadExactlyAsync(new byte[3]);
                cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => text
                    ? chars!.ReadAsync(new char[1].AsMemory(), cancellation.Token).AsTask()
                    : bytes!.ReadAsync(new byte[1], 0, 1, cancellation.Token));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static byte[] SequentialResponse(byte[] row, bool text, byte[]? terminal = null)
        {
            using var bytes = new MemoryStream();
            var packets = new[] { new byte[] { 1 }, Column("value", text ? (byte)253 : (byte)252, text ? (ushort)45 : (ushort)63), Eof(), row, terminal ?? Eof() };
            for (var i = 0; i < packets.Length; i++) bytes.Write(ResponseFrame(packets[i], (byte)(i + 1)));
            return bytes.ToArray();
        }

        private static byte[] FrameHeader(int length, int sequence) => new byte[] { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)sequence };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SequentialInvalidBinaryFieldCannotLeaveAReusablePartiallyParsedRow(bool nullType)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("bad", nullType ? (byte)6 : (byte)253, nullType ? (ushort)45 : (ushort)500));
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new byte[] { 0, 0, 0 });
                var closed = await Record.ExceptionAsync(() => channel.ReadPacketAsync());
                Assert.True(closed is ProtocolException || closed is IOException);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "bad"; await command.PrepareAsync();
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                var error = Assert.Throws<OceanBaseException>(() => reader.GetValue(0));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Throws<InvalidOperationException>(() => reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
