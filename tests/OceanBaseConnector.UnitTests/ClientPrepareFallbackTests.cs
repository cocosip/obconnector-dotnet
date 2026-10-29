using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static byte[] UnsupportedPrepare(int code = 1295, string state = "HY000")
            => new byte[] { 255, (byte)code, (byte)(code >> 8), 35, (byte)state[0], (byte)state[1], (byte)state[2], (byte)state[3], (byte)state[4], 115 };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientPrepareFallbackUsesTextRowsAndRetriesPrepareForEachExecution(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 22);
                    await channel.WritePacketAsync(UnsupportedPrepare());
                    channel.BeginCommand();
                    await Query(channel, "select (REPLACE( 'a''; -- \\中',NULL,NULL)), q'[?]', (" + (i + 1) + ")");
                    await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(Column("n", 3));
                    await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 2, 52, 50 });
                    await channel.WritePacketAsync(Eof());
                }
                channel.BeginCommand(); await Query(channel, "select next"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand();
                Assert.False(command.AllowClientSidePrepareFallback);
                command.AllowClientSidePrepareFallback = true;
                command.CommandText = "select :p, q'[?]', :n";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = "a'; -- \\中" });
                var number = new OceanBaseParameter { ParameterName = "n", Value = 1 }; command.Parameters.Add(number);
                for (var i = 0; i < 2; i++)
                {
                    number.Value = i + 1;
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(42m, reader.GetDecimal(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                command.Parameters.Clear(); command.CommandText = "select next";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 1295, "HY000", false, 0)]
        [InlineData(true, 1064, "HY000", false, 0)]
        [InlineData(true, 1295, "42000", false, 0)]
        [InlineData(true, 1295, "HY000", true, 0)]
        [InlineData(true, 1295, "HY000", false, 1)]
        public async Task ClientPrepareFallbackRequiresOptInExactPrepareErrorAndOrdinaryExecution(bool enabled, int code, string state, bool prepare, int fetch)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare(code, state));
                channel.BeginCommand(); await Query(channel, "select next"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "select :p"; command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 1 });
                command.AllowClientSidePrepareFallback = enabled; command.FetchSize = fetch;
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (prepare) await command.PrepareAsync(); else await command.ExecuteNonQueryAsync(); });
                Assert.Equal(code, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State);
                using var next = connection.CreateCommand(); next.CommandText = "select next"; Assert.Equal(1, await next.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientPrepareFallbackDoesNotCatchExecuteErrors(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 81, 1);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(UnsupportedPrepare());
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.AllowClientSidePrepareFallback = true;
                command.CommandText = "select :p"; command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 1 });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(1295, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ClientPrepareFallbackFreezesMutableParametersAndKeepsTheTransaction()
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await StatementCommand(channel, 22); received.SetResult(); await release.Task;
                await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand();
                await Query(channel, "update t set x=(HEXTORAW('0127'))"); await channel.WritePacketAsync(Ok(1, 5));
                channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.AllowClientSidePrepareFallback = true;
                command.CommandText = "update t set x=:p"; var bytes = new byte[] { 1, 39 };
                var parameter = new OceanBaseParameter { ParameterName = "p", Value = bytes }; command.Parameters.Add(parameter);
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
                command.Transaction = transaction; var execution = command.ExecuteNonQueryAsync();
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bytes[0] = 99; parameter.Value = "malicious'; --"; parameter.DbType = DbType.String;
                command.AllowClientSidePrepareFallback = false; command.CommandText = "changed"; release.SetResult();
                Assert.Equal(1, await execution); Assert.Same(connection, transaction.Connection);
                await transaction.RollbackAsync();
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ClientPrepareFallbackDoesNotReplayOnDisconnect(bool asynchronous, bool afterFallback)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                if (afterFallback)
                {
                    await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand(); await Query(channel, "select (1)");
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "select :p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 1 });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(afterFallback, error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ClientPrepareFallbackCancellationKeepsUnknownOutcomeBoundary(bool timeout, bool afterFallback)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                if (afterFallback)
                {
                    await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand(); await Query(channel, "select (1)");
                }
                received.SetResult(); await Assert.ThrowsAsync<ProtocolException>(() => ReadClientCommand(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "select :p"; command.CommandTimeout = timeout ? 1 : 0;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 1 });
                var execution = command.ExecuteNonQueryAsync(cancellation.Token); await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!timeout) cancellation.Cancel();
                var error = await Record.ExceptionAsync(() => execution);
                if (afterFallback)
                {
                    var unknown = Assert.IsType<OceanBaseException>(error); Assert.True(unknown.IsExecutionOutcomeUnknown); error = unknown.InnerException;
                }
                if (timeout) Assert.IsType<TimeoutException>(error); else Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientPrepareFallbackNeverConsumesMetadataErrorsOrMissingSqlState(bool metadata)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                if (metadata) await channel.WritePacketAsync(new byte[] { 0, 81, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 });
                await channel.WritePacketAsync(metadata ? UnsupportedPrepare() : new byte[] { 255, 15, 5, 115 });
                await StatementCommand(channel, metadata ? (byte)25 : (byte)3);
                if (metadata) { channel.BeginCommand(); await Query(channel, "select next"); }
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.AllowClientSidePrepareFallback = true;
                command.CommandText = "select :p"; command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 1 });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(1295, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State);
                using var next = connection.CreateCommand(); next.CommandText = "select next"; Assert.Equal(1, await next.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
