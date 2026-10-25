using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("line")]
        [InlineData("end")]
        [InlineData("block")]
        public async Task LobTextAsyncEntrypointsCancelTheActualNetworkRead(string operation)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 211, ExternalLob(4));
                await StatementCommand(channel, 0xa1); started.TrySetResult();
                await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; using var source = lob.OpenTextReader();
                using var cancellation = new CancellationTokenSource();
                var pending = operation == "line" ? (Task)source.ReadLineAsync(cancellation.Token).AsTask() :
                    operation == "end" ? source.ReadToEndAsync(cancellation.Token) : source.ReadBlockAsync(new char[8].AsMemory(), cancellation.Token).AsTask();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("4", 9, true)]
        [InlineData("4", 38, true)]
        [InlineData("4.000000000000000000000000000001", 38, false)]
        [InlineData("99999999999999999999999999999999999999", 38, false)]
        public async Task LobReadAcceptsExactNumberAmountsWithoutRounding(string amount, byte precision, bool valid)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(4)); await StatementCommand(channel, 0xa1);
                await LobReadReply(channel, 4, new byte[] { 1, 2, 3, 4 }, false, amount, precision);
                if (valid) { await StatementCommand(channel, 25); channel.BeginCommand(); }
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; using var source = lob.OpenRead(); var bytes = new byte[8];
                if (valid) { Assert.Equal(4, await source.ReadAsync(bytes, 0, bytes.Length)); Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes.AsSpan(0, 4).ToArray()); }
                else { var error = await Assert.ThrowsAsync<OceanBaseException>(() => source.ReadAsync(bytes, 0, bytes.Length)); Assert.Equal("The LOB read returned an invalid chunk or amount.", error.Message); Assert.Equal(ConnectionState.Broken, connection.State); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LobChunkTimeoutUsesOriginalCommandBudgetWithoutUnknownExecution(bool async)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(4)); await StatementCommand(channel, 0xa1);
                await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value"; command.CommandTimeout = 1;
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; command.CommandTimeout = 30; using var source = lob.OpenRead();
                var reading = async ? source.ReadAsync(new byte[8], 0, 8) : Task.Run(() => source.Read(new byte[8], 0, 8));
                await Assert.ThrowsAsync<TimeoutException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LobCachedAndExhaustedReadsCheckCancellationAndConnectionLifetime(bool character)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, InlineLob(new byte[] { 97, 98, 99 }));
                await Task.Delay(100);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                if (character)
                {
                    using var source = lob.OpenTextReader(); Assert.Equal('a', source.Read());
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ReadAsync(new char[1].AsMemory(), cancellation.Token));
                    Assert.Equal('b', source.Read()); connection.Close(); Assert.Throws<InvalidOperationException>(() => source.Read());
                }
                else
                {
                    using var source = lob.OpenRead(); Assert.Equal('a', source.ReadByte());
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(new byte[1], 0, 1, cancellation.Token));
                    Assert.Equal('b', source.ReadByte()); Assert.Equal('c', source.ReadByte()); Assert.Equal(-1, source.ReadByte());
                    connection.Close(); Assert.Throws<InvalidOperationException>(() => source.ReadByte());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LobContextExpiresAfterOriginalOrLaterTransaction(bool insideTransaction)
        {
            var server = Server(async channel =>
            {
                if (insideTransaction) await TransactionBegin(channel);
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 1 }));
                if (!insideTransaction) await TransactionBegin(channel);
                await TransactionEnd(channel, false); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var initial = insideTransaction ? await connection.BeginTransactionAsync() : null;
                using var command = connection.CreateCommand(); command.Transaction = initial; command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                using var later = insideTransaction ? null : await connection.BeginTransactionAsync();
                await (initial ?? later!).RollbackAsync();
                Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
                Assert.Throws<InvalidOperationException>(() => lob.WriteBytes(0, new byte[] { 2 }, 0, 1));
                await Assert.ThrowsAsync<InvalidOperationException>(() => lob.TruncateAsync(0));
                using var bound = connection.CreateCommand(); bound.CommandText = "select :lob from dual"; bound.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Value = lob });
                Assert.Throws<InvalidOperationException>(() => bound.ExecuteScalar());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LobReservationBlocksOtherCommandsAndHandleDisposalReleasesIt(bool character)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, InlineLob(new byte[] { 97, 98 }));
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                using var source = character ? (IDisposable)lob.OpenTextReader() : lob.OpenRead();
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Throws<InvalidOperationException>(() => after.ExecuteNonQuery());
                lob.Dispose(); Assert.Throws<ObjectDisposedException>(() => { if (character) ((TextReader)source).Read(); else ((Stream)source).ReadByte(); });
                Assert.Equal(1, after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("zero")]
        [InlineData("oversize")]
        [InlineData("wrong-amount")]
        [InlineData("wrong-buffer")]
        public async Task LobMalformedChunkCannotEscapeOrLeaveAUsableSession(string fault)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(4)); await StatementCommand(channel, 0xa1);
                await LobReadReply(channel, fault == "zero" ? 0 : fault == "wrong-amount" ? 2 : 4,
                    fault == "zero" ? Array.Empty<byte>() : fault == "oversize" ? new byte[5] : new byte[4], fault == "wrong-buffer");
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; using var source = lob.OpenRead();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => source.ReadAsync(new byte[8], 0, 8));
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LobInFlightCancellationDoesNotReclassifyOriginalSql(bool dispose)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, ExternalLob(4));
                await StatementCommand(channel, 0xa1); started.TrySetResult();
                await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!; using var source = lob.OpenRead();
                using var cancellation = new CancellationTokenSource();
                var pending = source.ReadAsync(new byte[8], 0, 8, cancellation.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (dispose) lob.Dispose(); else cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsNotType<TimeoutException>(error);
                if (!dispose) Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                Assert.False(error is OceanBaseException ob && ob.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
