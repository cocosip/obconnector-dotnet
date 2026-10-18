using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task LocalTransactionPartialBeginFailureDestroysChangedSession(int variant)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED");
                await channel.WritePacketAsync(Ok()); channel.BeginCommand();
                await Query(channel, "set autocommit = 0");
                if (variant == 2) return;
                await channel.WritePacketAsync(variant == 0 ? Error() : new byte[] { 1 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => await connection.BeginTransactionAsync());
                Assert.Equal(variant != 0, error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "must not execute";
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalTransactionSuccessfulDisposeRestoresAutocommit(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await TransactionEnd(channel, false);
                await Query(channel, "outside"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var transaction = await connection.BeginTransactionAsync();
                using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                { if (asynchronous) await transaction.DisposeAsync(); else transaction.Dispose(); }
                Assert.Null(transaction.Connection);
                using var command = connection.CreateCommand(); command.CommandText = "outside";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                await transaction.DisposeAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalTransactionAsyncCloseCleansOnlyCapturedSessionAcrossReopen()
        {
            var rollbackReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, "ROLLBACK");
                rollbackReceived.SetResult(); await allowRollback.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await channel.WritePacketAsync(Ok(status: 4)); channel.BeginCommand();
                await Query(channel, "set autocommit = 1"); await channel.WritePacketAsync(Ok());
            });
            var second = Server(async channel => { await Query(channel, "new session"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            var transitions = new List<(ConnectionState Event, ConnectionState Observed)>();
            connection.StateChange += (_, args) => transitions.Add((args.CurrentState, connection.State));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                var closing = connection.CloseAsync();
                await rollbackReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(closing.IsCompleted);
                connection.ConnectionString = Configuration(second.Port);
                await connection.OpenAsync();
                allowRollback.SetResult(); await closing;
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(new[] { (ConnectionState.Open, ConnectionState.Open), (ConnectionState.Closed, ConnectionState.Closed), (ConnectionState.Open, ConnectionState.Open) }, transitions);
                Assert.Null(transaction.Connection);
                using var command = connection.CreateCommand(); command.CommandText = "new session";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { allowRollback.TrySetResult(); connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task LocalTransactionCancellationAfterEndSendDestroysSession(bool commit)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, commit ? "COMMIT" : "ROLLBACK");
                received.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                using var cancel = new CancellationTokenSource();
                var pending = commit ? transaction.CommitAsync(cancel.Token) : transaction.RollbackAsync(cancel.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted);
                cancel.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending);
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
