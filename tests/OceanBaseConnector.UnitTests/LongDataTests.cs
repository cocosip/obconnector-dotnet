using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 20000)]
        [InlineData(true, 20000)]
        public async Task LongDataStreamsUseBoundedPacketsWithoutInlineValues(bool asynchronous, int size)
        {
            var source = new LongDataSource(size, asynchronous);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 43, 3);
                var received = 0;
                do
                {
                    var packet = await StatementCommand(channel, 24);
                    Assert.InRange(packet.Length, 7, 8199);
                    Assert.Equal(new byte[] { 24, 43, 0, 0, 0, 1, 0 }, packet.Take(7));
                    for (var i = 7; i < packet.Length; i++) Assert.Equal((byte)((received + i - 7) % 251), packet[i]);
                    received += packet.Length - 7;
                } while (received < size);
                Assert.Equal(size, received);
                Assert.Equal(new byte[] { 23, 43, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3, 0, 252, 0, 253, 0, 42, 0, 0, 0, 1, 120 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (?, ?, ?)";
                command.Parameters.Add(new OceanBaseParameter { Value = 42 });
                var upload = new OceanBaseParameter { Value = source }; command.Parameters.Add(upload);
                command.Parameters.Add(new OceanBaseParameter { Value = "x" });
                source.BeforeRead = _ => { upload.Value = new byte[] { 99 }; upload.DbType = DbType.String; };
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.False(source.WasDisposed); Assert.Equal(size, source.BytesRead);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LongDataInvalidDeclarationsFailBeforeReadingOrSending()
        {
            var server = Server(async channel => { await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var source = new LongDataSource(100, false);
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "select :p";
                var value = new OceanBaseParameter { ParameterName = "p", Value = source, DbType = DbType.String }; command.Parameters.Add(value);
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                UseInternalMySqlFixture(connection); command.CommandText = "select ?";
                foreach (var invalid in new Action[] { () => value.DbType = DbType.String, () => value.Size = 1,
                    () => value.Precision = 1, () => value.Scale = 1, () => value.Direction = ParameterDirection.Output,
                    () => command.AllowClientSidePrepareFallback = true, () => source.Dispose() })
                {
                    value.ResetDbType(); value.Size = 0; value.Precision = value.Scale = 0; value.Direction = ParameterDirection.Input;
                    command.AllowClientSidePrepareFallback = false; invalid();
                    Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                }
                Assert.Equal(0, source.BytesRead);
                command.Parameters.Clear(); command.CommandText = "after";
                Assert.Equal(1, await command.ExecuteNonQueryAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LongDataPrepareDoesNotReadAndReuseStartsAtTheCallersCurrentPosition(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 44, 1);
                Assert.Equal(new byte[] { 24, 44, 0, 0, 0, 0, 0, 12, 13 }, await StatementCommand(channel, 24));
                Assert.Equal(new byte[] { 23, 44, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 252, 0 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 24, 44, 0, 0, 0, 0, 0 }, await StatementCommand(channel, 24));
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 23, 44, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 6, 0 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var source = new MemoryStream(new byte[] { 11, 12, 13 });
            source.Position = 1;
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (?)";
                var parameter = new OceanBaseParameter { DbType = DbType.Binary, Value = source }; command.Parameters.Add(parameter);
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(1, source.Position);
                for (var i = 0; i < 2; i++) Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                parameter.Value = DBNull.Value;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.True(source.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task LongDataSourceFailureOnlyDiscardsSessionsWithStagedData(bool asynchronous, bool uploaded)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 45, 1);
                if (uploaded) { await StatementCommand(channel, 24); Assert.Equal(0, await channelStreamRead(channel)); }
                else
                {
                    await StatementCommand(channel, 25); await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            var source = new LongDataSource(20000, asynchronous) { BeforeRead = read => { if (!uploaded || read > 0) throw new IOException("Source failed."); } };
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "insert into t values (?)"; command.Parameters.Add(new OceanBaseParameter { Value = source });
                    var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                    Assert.IsType<IOException>(error); Assert.False(source.WasDisposed);
                    Assert.Equal(uploaded ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                }
                if (!uploaded) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LongDataCancellationBreaksStagedSessionEvenWhileSourceIgnoresToken()
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            var source = new LongDataSource(20000, true);
            // Pause the second read, after one no-response upload has ended.
            var reads = 0;
            source.BeforeReadAsync = async token => { if (++reads == 2) { reading.SetResult(); await release.Task; } };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 46, 1); await StatementCommand(channel, 24);
                Assert.Equal(0, await channelStreamRead(channel)); closed.SetResult();
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (?)"; command.Parameters.Add(new OceanBaseParameter { Value = source });
                var pending = command.ExecuteNonQueryAsync(caller.Token);
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                using var busy = connection.CreateCommand(); busy.CommandText = "after";
                Assert.Throws<InvalidOperationException>(() => busy.ExecuteNonQuery());
                caller.Cancel(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                release.SetResult();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(source.WasDisposed);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LongDataExecutionErrorRetainsCodesAndDisconnectIsUnknown(bool disconnect)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 47, 1); await StatementCommand(channel, 24); await StatementCommand(channel, 23);
                if (!disconnect)
                {
                    await channel.WritePacketAsync(Error()); await StatementCommand(channel, 25);
                    await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var source = new MemoryStream(new byte[] { 1, 2, 3 });
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "insert into t values (?)"; command.Parameters.Add(new OceanBaseParameter { Value = source });
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                    Assert.Equal(disconnect, error.IsExecutionOutcomeUnknown);
                    Assert.Equal(disconnect ? 0 : 1064, error.ServerErrorCode);
                    Assert.Equal(disconnect ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                }
                if (!disconnect) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LongDataAggregateBudgetRejectsOverflowWithoutExecutingOrBufferingTheWholeSource()
        {
            var received = 0;
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 48, 1);
                // Each read is one bounded data packet; no EXECUTE may follow the overflow.
                while (received < 64 * 1024 * 1024 - 8192)
                {
                    var packet = await StatementCommand(channel, 24); Assert.Equal(8199, packet.Length); received += 8192;
                }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            var source = new LongDataSource(64 * 1024 * 1024 - 13, true) { ChunkSize = 8192 };
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (?)"; command.Parameters.Add(new OceanBaseParameter { Value = source });
                await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(source.WasDisposed);
                Assert.Equal(64 * 1024 * 1024 - 13, source.BytesRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }

        private sealed class LongDataSource : Stream
        {
            private readonly int _length;
            private readonly bool _asynchronous;
            internal LongDataSource(int length, bool asynchronous) { _length = length; _asynchronous = asynchronous; }
            internal int BytesRead { get; private set; }
            internal bool WasDisposed { get; private set; }
            internal Action<int>? BeforeRead { get; set; }
            internal Func<CancellationToken, Task>? BeforeReadAsync { get; set; }
            internal int ChunkSize { get; set; } = 3011;
            public override bool CanRead => !WasDisposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new InvalidOperationException("Length must not be read.");
            public override long Position { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }
            private int Fill(byte[] buffer, int offset, int count)
            {
                BeforeRead?.Invoke(BytesRead);
                Assert.InRange(count, 1, 8192);
                var size = Math.Min(Math.Min(count, ChunkSize), _length - BytesRead);
                for (var i = 0; i < size; i++) buffer[offset + i] = (byte)((BytesRead + i) % 251);
                BytesRead += size; return size;
            }
            public override int Read(byte[] buffer, int offset, int count) { Assert.False(_asynchronous); return Fill(buffer, offset, count); }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            { Assert.True(_asynchronous); if (BeforeReadAsync != null) await BeforeReadAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); return Fill(buffer, offset, count); }
            protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
