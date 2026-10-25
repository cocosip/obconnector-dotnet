using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task AmbientAutomaticOpenCancellationInterruptsInitializationAndReleasesTheResource(bool oracle, bool secondControl)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = CacheServer(oracle, async channel =>
            {
                if (secondControl)
                    await AmbientControl(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", (byte)(oracle ? 6 : 2));
                Assert.Equal(OceanBaseCommand.SqlPacket(secondControl ? "set autocommit = 0" : "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", 3), await StatementCommand(channel, 3));
                received.SetResult();
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=0;Pooling=true;Max Pool Size=1");
            using var cancellation = new CancellationTokenSource();
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            Task<Exception?>? opening = null;
            var opened = 0;
            connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Open) opened++; };
            using var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled);
            try
            {
                opening = Task.Run(async () =>
                {
                    try { await connection.OpenAsync(cancellation.Token); return (Exception?)null; }
                    catch (Exception error) { return error; }
                });
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
                var error = Assert.IsAssignableFrom<OperationCanceledException>(await opening.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(cancellation.Token, error.CancellationToken);
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(0, opened);
                Assert.True(transaction.EnlistPromotableSinglePhase(new OtherPromotableResource()));
                scope.Complete();
            }
            finally
            {
                connection.Close();
                if (opening != null) await opening.WaitAsync(TimeSpan.FromSeconds(10));
                OceanBaseConnection.ClearPool(connection); server.Stop();
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientCommitIsIndependentOfCancellationAfterOpenCompleted(bool oracle)
        {
            var server = CacheServer(oracle, async channel => { await AmbientBegin(channel, oracle); await AmbientEnd(channel, oracle, true); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                using (var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using var cancellation = new CancellationTokenSource();
                    await connection.OpenAsync(cancellation.Token); cancellation.Cancel(); scope.Complete();
                }
                transaction.Commit();
                Assert.Equal(TransactionStatus.Committed, transaction.TransactionInformation.Status);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientLostRollbackReplyAbortsAndDestroysTheSession(bool oracle)
        {
            var server = CacheServer(oracle, async channel =>
            {
                await AmbientBegin(channel, oracle);
                Assert.Equal(OceanBaseCommand.SqlPacket("ROLLBACK", 3), await StatementCommand(channel, 3));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction); transaction.Rollback();
                Assert.Equal(TransactionStatus.Aborted, transaction.TransactionInformation.Status);
                Assert.Equal(ConnectionState.Broken, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "must not replay";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task AmbientCompletionWithBusyReaderAbortsAndDiscardsUnreadSession(bool oracle, bool commit)
        {
            var server = CacheServer(oracle, async channel =>
            {
                await AmbientBegin(channel, oracle); await StatementCommand(channel, 3);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof((byte)(oracle ? 5 : 1))); await channel.WritePacketAsync(new byte[] { 1, 49 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                using var command = connection.CreateCommand(); command.CommandText = "SELECT n FROM t";
                using var reader = await command.ExecuteReaderAsync();
                if (commit) Assert.Throws<TransactionAbortedException>(transaction.Commit); else transaction.Rollback();
                Assert.Equal(TransactionStatus.Aborted, transaction.TransactionInformation.Status);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientInitializationCommandTimeoutDoesNotOccupyTheCoordinatorSlot(bool oracle)
        {
            var server = CacheServer(oracle, async channel => { await StatementCommand(channel, 3); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=1");
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync();
                var error = Assert.Throws<OceanBaseException>(() => connection.EnlistTransaction(transaction));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<TimeoutException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.True(transaction.EnlistPromotableSinglePhase(new OtherPromotableResource())); transaction.Rollback();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientScopeTimeoutDuringInitializationEventuallyRollsBackWithoutReplay(bool oracle)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<TransactionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = CacheServer(oracle, async channel =>
            {
                await AmbientControl(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", (byte)(oracle ? 6 : 2));
                Assert.Equal(OceanBaseCommand.SqlPacket("set autocommit = 0", 3), await StatementCommand(channel, 3));
                received.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 4 : 0)));
                await AmbientEnd(channel, oracle, false);
                await AmbientControl(channel, "after timeout", (byte)(oracle ? 6 : 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=5");
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted, Timeout = TimeSpan.FromSeconds(1) });
            transaction.TransactionCompleted += (_, args) => completed.TrySetResult(args.Transaction!.TransactionInformation.Status);
            Task? enlisting = null;
            try
            {
                await connection.OpenAsync(); enlisting = Task.Run(() => connection.EnlistTransaction(transaction));
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(TimeSpan.FromMilliseconds(2100)); Assert.False(enlisting.IsCompleted);
                release.SetResult(); await enlisting.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(TransactionStatus.Aborted, await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "after timeout";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
            }
            finally
            {
                release.TrySetResult();
                if (enlisting != null) await enlisting.WaitAsync(TimeSpan.FromSeconds(10));
                connection.Close(); server.Stop();
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
