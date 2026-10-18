using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class TransactionConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false, false)]
        [InlineData("netstandard2.1", true, false)]
        [InlineData("net8.0", true, false)]
        [InlineData("net9.0", true, false)]
        [InlineData("net10.0", true, false)]
        [InlineData("netstandard2.1", true, true)]
        [InlineData("net10.0", true, true)]
        public async Task EachAssetSupportsPublicLocalTransactionsAndAvailableAsyncOverrides(string target, bool asynchronousApis, bool commit)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var beginRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var endRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                channel.BeginCommand();
                foreach (var sql in new[] { "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE", "set autocommit = 0", "update sample", commit ? "COMMIT" : "ROLLBACK", "set autocommit = 1" })
                {
                    var request = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
                    Assert.Equal(3, request[0]);
                    Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    if (asynchronousApis && sql == "set autocommit = 0") { beginRequested.SetResult(); await allowBegin.Task.WaitAsync(budget.Token); }
                    if (asynchronousApis && (sql == "ROLLBACK" || sql == "COMMIT")) { endRequested.SetResult(); await allowEnd.Task.WaitAsync(budget.Token); }
                    var status = sql == "set autocommit = 1" || sql.StartsWith("SET SESSION", StringComparison.Ordinal) ? (byte)6 : (byte)4;
                    if (sql == "update sample") status = 5;
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, status, 0, 0, 0 }, budget.Token);
                    channel.BeginCommand();
                }
            });
            var context = new AssemblyLoadContext("transaction-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                var transactionType = assembly.GetType("OceanBaseConnector.OceanBaseTransaction")!;
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                Assert.Equal(asynchronousApis, connectionType.GetMethod("BeginDbTransactionAsync", flags) != null);
                Assert.Equal(asynchronousApis, connectionType.GetMethod("CloseAsync", flags) != null);
                Assert.Equal(asynchronousApis, connectionType.GetMethod("DisposeAsync", flags) != null);
                Assert.Equal(asynchronousApis, transactionType.GetMethod("CommitAsync", flags) != null);
                Assert.Equal(asynchronousApis, transactionType.GetMethod("RollbackAsync", flags) != null);
                Assert.Equal(asynchronousApis, transactionType.GetMethod("DisposeAsync", flags) != null);
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                DbTransaction transaction;
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                if (asynchronousApis)
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.BeginTransactionAsync(IsolationLevel.Serializable, canceled.Token).AsTask());
                    Assert.Equal(canceled.Token, error.CancellationToken);
                    var pending = connection.BeginTransactionAsync(IsolationLevel.Serializable, budget.Token);
                    await beginRequested.Task.WaitAsync(budget.Token);
                    Assert.False(pending.IsCompleted);
                    allowBegin.SetResult();
                    transaction = await pending;
                }
                else transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                using (transaction)
                {
                    Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction; command.CommandText = "update sample";
                    Assert.Equal(0, await command.ExecuteNonQueryAsync(budget.Token));
                    if (asynchronousApis)
                    {
                        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit ? transaction.CommitAsync(canceled.Token) : transaction.RollbackAsync(canceled.Token));
                        Assert.Equal(canceled.Token, error.CancellationToken);
                        Assert.Equal(connection, transaction.Connection);
                        var pending = commit ? transaction.CommitAsync(budget.Token) : transaction.RollbackAsync(budget.Token);
                        await endRequested.Task.WaitAsync(budget.Token);
                        Assert.False(pending.IsCompleted);
                        allowEnd.SetResult();
                        await pending;
                    }
                    else transaction.Rollback();
                    Assert.Null(transaction.Connection);
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        [Theory]
        [InlineData("netstandard2.1", "connection-close")]
        [InlineData("netstandard2.1", "connection-dispose")]
        [InlineData("netstandard2.1", "transaction-dispose")]
        [InlineData("netstandard2.1", "reader-close")]
        [InlineData("netstandard2.1", "reader-dispose")]
        [InlineData("net10.0", "connection-close")]
        [InlineData("net10.0", "connection-dispose")]
        [InlineData("net10.0", "transaction-dispose")]
        [InlineData("net10.0", "reader-close")]
        [InlineData("net10.0", "reader-dispose")]
        public async Task AsyncCleanupReturnsBeforeDelayedRollbackAndWaitsForRestoration(string target, string cleanup)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var rollbackRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                channel.BeginCommand();
                foreach (var sql in new[] { "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE", "set autocommit = 0", "update sample", "ROLLBACK", "set autocommit = 1" })
                {
                    var request = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
                    Assert.Equal(3, request[0]);
                    Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    if (sql == "ROLLBACK") { rollbackRequested.SetResult(); await allowRollback.Task.WaitAsync(budget.Token); }
                    var status = sql == "set autocommit = 1" || sql.StartsWith("SET SESSION", StringComparison.Ordinal) ? (byte)6 : (byte)4;
                    if (sql == "update sample") status = 5;
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, status, 0, 0, 0 }, budget.Token);
                    channel.BeginCommand();
                }
            });
            var context = new AssemblyLoadContext("cleanup-" + target + cleanup, isCollectible: true);
            Task? closing = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                using var command = connection.CreateCommand();
                command.Transaction = transaction; command.CommandText = "update sample";
                using var reader = await command.ExecuteReaderAsync(cleanup.StartsWith("reader", StringComparison.Ordinal) ? CommandBehavior.CloseConnection : CommandBehavior.Default, budget.Token);
                closing = Task.Run(async () =>
                {
                    var pending = cleanup switch
                    {
                        "connection-close" => connection.CloseAsync(),
                        "connection-dispose" => connection.DisposeAsync().AsTask(),
                        "transaction-dispose" => transaction.DisposeAsync().AsTask(),
                        "reader-close" => reader.CloseAsync(),
                        _ => reader.DisposeAsync().AsTask()
                    };
                    returned.SetResult();
                    await pending;
                });
                try
                {
                    await rollbackRequested.Task.WaitAsync(budget.Token);
                    await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.False(closing.IsCompleted);
                }
                finally { allowRollback.TrySetResult(); await closing; }
                Assert.Null(transaction.Connection);
                Assert.Equal(cleanup == "transaction-dispose" ? ConnectionState.Open : ConnectionState.Closed, connection.State);
                if (cleanup.StartsWith("reader", StringComparison.Ordinal)) Assert.True(reader.IsClosed);
                await serving.WaitAsync(budget.Token);
            }
            finally
            {
                allowRollback.TrySetResult();
                budget.Cancel(); listener.Stop(); context.Unload();
            }
        }
    }
}
