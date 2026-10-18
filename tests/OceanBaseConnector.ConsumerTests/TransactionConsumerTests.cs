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
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.1", false)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", true)]
        public async Task EachAssetSupportsPublicLocalTransactionsAndModernAsyncOverrides(string target, bool modern)
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
                foreach (var sql in new[] { "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE", "set autocommit = 0", "update sample", "ROLLBACK", "set autocommit = 1" })
                {
                    var request = await channel.ReadPacketAsync(budget.Token);
                    Assert.Equal(3, request[0]);
                    Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    if (modern && sql == "set autocommit = 0") { beginRequested.SetResult(); await allowBegin.Task.WaitAsync(budget.Token); }
                    if (modern && sql == "ROLLBACK") { endRequested.SetResult(); await allowEnd.Task.WaitAsync(budget.Token); }
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
                Assert.Equal(modern, connectionType.GetMethod("BeginDbTransactionAsync", flags) != null);
                Assert.Equal(modern, connectionType.GetMethod("CloseAsync", flags) != null);
                Assert.Equal(modern, transactionType.GetMethod("CommitAsync", flags) != null);
                Assert.Equal(modern, transactionType.GetMethod("RollbackAsync", flags) != null);
                Assert.Equal(modern, transactionType.GetMethod("DisposeAsync", flags) != null);
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                DbTransaction transaction;
                if (modern)
                {
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
                    if (modern)
                    {
                        var pending = transaction.RollbackAsync(budget.Token);
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
    }
}
