using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task Savepoint(DbTransaction transaction, string operation, string name, bool asynchronous, CancellationToken token = default)
        {
            if (asynchronous)
            {
                if (operation == "save") await transaction.SaveAsync(name, token);
                else if (operation == "rollback") await transaction.RollbackAsync(name, token);
                else await transaction.ReleaseAsync(name, token);
            }
            else
            {
                if (operation == "save") transaction.Save(name);
                else if (operation == "rollback") transaction.Rollback(name);
                else transaction.Release(name);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SavepointKeepsTheOriginalTransactionAndInvalidatesResourcesOnlyOnRollback(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel, mysql ? "REPEATABLE READ" : "READ COMMITTED");
                await XaReply(channel, mysql ? "SAVEPOINT `Point_1`" : "SAVEPOINT Point_1", Ok(status: 1));
                await XaReply(channel, mysql ? "ROLLBACK TO SAVEPOINT `Point_1`" : "ROLLBACK TO Point_1", Ok(status: 1));
                if (mysql) await XaReply(channel, "RELEASE SAVEPOINT `Point_1`", Ok(status: 1));
                await XaReply(channel, "update sample", Ok(1, 1));
                await TransactionEnd(channel, true);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                if (mysql) SetXaMySql(connection);
                using var transaction = await connection.BeginTransactionAsync();
                Assert.True(transaction.SupportsSavepoints);
                var epoch = connection.TransactionEpoch;
                await Savepoint(transaction, "save", "Point_1", asynchronous);
                Assert.Equal(epoch, connection.TransactionEpoch);
                await Savepoint(transaction, "rollback", "Point_1", asynchronous);
                Assert.Equal(epoch + 1, connection.TransactionEpoch);
                if (mysql) await Savepoint(transaction, "release", "Point_1", asynchronous);
                else await Assert.ThrowsAsync<NotSupportedException>(() => Savepoint(transaction, "release", "Point_1", asynchronous));
                Assert.Same(connection, transaction.Connection);
                using var command = connection.CreateCommand(); command.CommandText = "update sample";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                command.Transaction = transaction;
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                await transaction.CommitAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => Savepoint(transaction, "save", "Point_1", asynchronous));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SavepointRejectsUnsafeNamesCancellationAndAmbientTransactionsBeforeSending(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await XaReply(channel, "SAVEPOINT " + new string('a', 30), Ok(status: 1));
                await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();
                foreach (var operation in new[] { "save", "rollback", "release" })
                {
                    await Assert.ThrowsAsync<ArgumentNullException>(() => Savepoint(transaction, operation, null!, asynchronous));
                    foreach (var name in new[] { "", " ", "1a", "_a", "a b", "a; COMMIT", "a--", "a\0", "a`", "a\"", "é", new string('a', 31) })
                    {
                        var error = await Assert.ThrowsAsync<ArgumentException>(() => Savepoint(transaction, operation, name, asynchronous));
                        Assert.DoesNotContain("COMMIT", error.Message);
                    }
                }
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                if (asynchronous)
                {
                    foreach (var operation in new[] { "save", "rollback", "release" })
                    {
                        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Savepoint(transaction, operation, "point", true, canceled.Token));
                        Assert.Equal(canceled.Token, error.CancellationToken);
                    }
                }
                using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                    await Assert.ThrowsAsync<NotSupportedException>(() => Savepoint(transaction, "save", "point", asynchronous));
                await Savepoint(transaction, "save", new string('a', 30), asynchronous);
                await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "save")]
        [InlineData(true, "save")]
        [InlineData(false, "rollback")]
        [InlineData(true, "rollback")]
        [InlineData(false, "release")]
        [InlineData(true, "release")]
        public async Task SavepointTrustedErrorRetainsTheTransactionAndResourceEpoch(bool asynchronous, string operation)
        {
            var sql = operation == "save" ? "SAVEPOINT `point`" : operation == "rollback" ? "ROLLBACK TO SAVEPOINT `point`" : "RELEASE SAVEPOINT `point`";
            var server = Server(async channel =>
            {
                await TransactionBegin(channel, "REPEATABLE READ");
                await XaReply(channel, sql, Error());
                await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection);
                using var transaction = await connection.BeginTransactionAsync();
                var epoch = connection.TransactionEpoch;
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => Savepoint(transaction, operation, "point", asynchronous));
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(1064, error.ServerErrorCode);
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Same(connection, transaction.Connection);
                Assert.Equal(epoch, connection.TransactionEpoch);
                await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "disconnect")]
        [InlineData(true, "disconnect")]
        [InlineData(false, "boundary")]
        [InlineData(true, "boundary")]
        [InlineData(false, "autocommit")]
        [InlineData(true, "autocommit")]
        public async Task SavepointUntrustedResponseBreaksTheSessionWithoutReplay(bool asynchronous, string fault)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "ROLLBACK TO point");
                if (fault != "disconnect") await channel.WritePacketAsync(Ok(status: fault == "boundary" ? (byte)9 : (byte)2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => Savepoint(transaction, "rollback", "point", asynchronous));
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Null(transaction.Connection);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Savepoint(transaction, "save", "point", asynchronous));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("success")]
        [InlineData("cancel")]
        [InlineData("close")]
        [InlineData("timeout")]
        public async Task SavepointAsyncWaitsForNetworkAndProtectsTransactionOwnership(string outcome)
        {
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "SAVEPOINT point"); requested.SetResult();
                if (outcome == "success")
                {
                    await respond.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await channel.WritePacketAsync(Ok(status: 1)); channel.BeginCommand();
                    await TransactionEnd(channel, false);
                }
                else
                {
                    Assert.Equal(0, await channelStreamRead(channel).WaitAsync(TimeSpan.FromSeconds(10)));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=" + (outcome == "timeout" ? 1 : 5));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                using var cancel = new CancellationTokenSource();
                var saving = transaction.SaveAsync("point", cancel.Token);
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(saving.IsCompleted);
                Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.SaveAsync("other"));
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "forbidden";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                if (outcome == "success") { respond.SetResult(); await saving; await transaction.RollbackAsync(); }
                else
                {
                    if (outcome == "cancel") cancel.Cancel();
                    if (outcome == "close") connection.Close();
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => saving);
                    Assert.True(error.IsExecutionOutcomeUnknown);
                    if (outcome == "cancel") Assert.Equal(cancel.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                    if (outcome == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                    Assert.Null(transaction.Connection);
                }
            }
            finally { respond.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SavepointRollbackInvalidatesPreviouslyReturnedLobHandles(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 1 }));
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 2 }));
                await XaReply(channel, "SAVEPOINT point", Ok(status: 1));
                await XaReply(channel, "ROLLBACK TO point", Ok(status: 1));
                await TransactionEnd(channel, false);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "select lob_value";
                using var readable = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                await Savepoint(transaction, "save", "point", asynchronous);
                using (var stream = readable.OpenRead()) Assert.Equal(1, stream.ReadByte());
                await Savepoint(transaction, "rollback", "point", asynchronous);
                Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
                command.CommandText = "select :lob from dual";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Value = lob });
                Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
                await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SavepointRejectsBusyReaderAndClosedOrReopenedTransaction(bool asynchronous)
        {
            var first = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, "select pending");
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof(1)); await channel.WritePacketAsync(new byte[] { 1, 49 });
                await channel.WritePacketAsync(Eof(1)); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            var second = Server(async channel => { await TransactionBegin(channel); await TransactionEnd(channel, false); });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync(); using var old = await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.Transaction = old; command.CommandText = "select pending";
                using (var reader = await command.ExecuteReaderAsync())
                {
                    foreach (var operation in new[] { "save", "rollback" })
                        await Assert.ThrowsAsync<InvalidOperationException>(() => Savepoint(old, operation, "point", asynchronous));
                    Assert.True(await reader.ReadAsync()); Assert.False(await reader.ReadAsync());
                }
                connection.Close();
                await Assert.ThrowsAsync<InvalidOperationException>(() => Savepoint(old, "save", "point", asynchronous));
                connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                using var current = await connection.BeginTransactionAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => Savepoint(old, "rollback", "point", asynchronous));
                Assert.Same(connection, current.Connection); await current.RollbackAsync();
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SavepointLogsAfterReleasingOwnershipWithoutExposingNames(bool rejected)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await XaReply(channel, "SAVEPOINT PrivateName", rejected ? Error() : Ok(status: 1));
                await XaReply(channel, "update sample", Ok(1, 1)); await TransactionEnd(channel, false);
            });
            using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger);
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                var observed = false;
                logger.OnLog = id =>
                {
                    if (id.Name != "TransactionSAVEPOINT" + (rejected ? "Failed" : "Completed")) return;
                    using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "update sample";
                    observed = command.ExecuteNonQuery() == 1;
                };
                if (rejected) Assert.Throws<OceanBaseException>(() => transaction.Save("PrivateName"));
                else transaction.Save("PrivateName");
                Assert.True(observed);
                Assert.All(logger.Entries, entry => Assert.DoesNotContain("PrivateName", entry.Message));
                await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
