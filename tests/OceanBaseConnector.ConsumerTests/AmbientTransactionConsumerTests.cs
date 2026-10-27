using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class AmbientTransactionConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        foreach (var lostReply in new[] { false, true }) yield return new object[] { target, asynchronous, oracle, lostReply };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetRejectsSecondResourceAndReportsTheCommitOutcome(string target, bool asynchronous, bool oracle, bool lostReply)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
            using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
            var serving = Task.WhenAll(Serve(first, oracle, lostReply, secondary: false, budget.Token), Serve(second, oracle, lostReply, secondary: true, budget.Token));
            var context = new AssemblyLoadContext("ambient-single-phase-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var owner = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                using var other = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                owner.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)first.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                other.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)second.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
                using (var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled))
                {
                    if (asynchronous) await owner.OpenAsync(budget.Token); else owner.Open();
                    await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? other.OpenAsync(budget.Token) : Open(other));
                    Assert.Equal(ConnectionState.Closed, other.State);
                    using var command = owner.CreateCommand(); command.CommandText = "UPDATE t SET n=1";
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    scope.Complete();
                }
                if (lostReply)
                {
                    var error = Assert.Throws<TransactionInDoubtException>(transaction.Commit);
                    var cause = Assert.IsAssignableFrom<DbException>(error.InnerException);
                    Assert.Equal("OceanBaseConnector.OceanBaseException", cause.GetType().FullName);
                    Assert.Equal(true, cause.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(cause));
                    Assert.Equal(TransactionStatus.InDoubt, transaction.TransactionInformation.Status);
                    Assert.Equal(ConnectionState.Broken, owner.State);
                    using var command = owner.CreateCommand(); command.CommandText = "must not replay";
                    Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                }
                else
                {
                    transaction.Commit(); Assert.Equal(TransactionStatus.Committed, transaction.TransactionInformation.Status);
                    Assert.Equal(ConnectionState.Open, owner.State);
                }
                owner.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); first.Stop(); second.Stop(); context.Unload(); }
        }

        private static Task Open(DbConnection connection) { connection.Open(); return Task.CompletedTask; }

        public static IEnumerable<object[]> CancellationAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var oracle in new[] { false, true }) yield return new object[] { target, oracle };
        }

        [Theory]
        [MemberData(nameof(CancellationAssets))]
        public async Task EveryAssetCancelsAmbientInitializationAndCanOpenAgainInTheSameScope(string target, bool oracle)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = ServeCancelledInitialization(listener, oracle, received, budget.Token);
            var context = new AssemblyLoadContext("ambient-cancellation-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Pool Wait Timeout=1;Command Timeout=0";
                using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
                using (var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled))
                {
                    Task<Exception?>? opening = null;
                    try
                    {
                        opening = Task.Run(() => Record.ExceptionAsync(() => connection.OpenAsync(cancellation.Token)));
                        await received.Task.WaitAsync(budget.Token); cancellation.Cancel();
                        var error = Assert.IsAssignableFrom<OperationCanceledException>(await opening.WaitAsync(TimeSpan.FromSeconds(3)));
                        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(ConnectionState.Closed, connection.State);
                        await connection.OpenAsync(budget.Token);
                        using var command = connection.CreateCommand(); command.CommandText = "after cancellation";
                        Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                        connection.Close(); scope.Complete();
                    }
                    finally
                    {
                        connection.Close();
                        if (opening != null) await opening.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                transaction.Rollback();
                assembly.GetType("OceanBaseConnector.OceanBaseConnection")!.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static async Task ServeCancelledInitialization(TcpListener listener, bool oracle, TaskCompletionSource received, CancellationToken token)
        {
            var autoOn = (byte)(oracle ? 6 : 2); var autoOff = (byte)(oracle ? 4 : 0);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var peer = await listener.AcceptTcpClientAsync(token); var stream = peer.GetStream();
                var channel = await Handshake(stream, oracle, token);
                async Task Expect(string sql, byte? status)
                {
                    channel.BeginCommand(); Assert.Equal(OceanBaseCommand.SqlPacket(sql, 3), await channel.ReadPacketAsync(token));
                    if (status.HasValue) await channel.WritePacketAsync(Ok(status.Value), token);
                }
                await Expect("SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", autoOn);
                await Expect("set autocommit = 0", attempt == 0 ? null : autoOff);
                if (attempt == 0) received.SetResult();
                else
                {
                    channel.BeginCommand(); Assert.Equal(OceanBaseCommand.SqlPacket("after cancellation", 3), await channel.ReadPacketAsync(token));
                    await channel.WritePacketAsync(Ok(autoOff, 1), token);
                    await Expect("ROLLBACK", autoOff); await Expect("set autocommit = 1", autoOn);
                    channel.BeginCommand(); Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(token));
                    await channel.WritePacketAsync(Ok(autoOn), token);
                    if (oracle) await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel, token);
                }
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            }
        }

        private static byte[] Ok(byte status, byte affected = 0)
        { var packet = Ob20ConsumerTests.Report(false, affected); packet[3] = status; return packet; }

        private static async Task<PacketChannel> Handshake(NetworkStream stream, bool oracle, CancellationToken token)
        {
            var channel = new PacketChannel(stream);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await channel.WritePacketAsync(fixture.RootElement.GetProperty("payloadBase64").GetBytesFromBase64(), token);
            await channel.ReadPacketAsync(token);
            await channel.WritePacketAsync(Ok((byte)(oracle ? 6 : 2)), token);
            await ServerIdentity.ReplyAsync(channel, oracle: oracle, token: token);
            return channel;
        }

        private static async Task Serve(TcpListener listener, bool oracle, bool lostReply, bool secondary, CancellationToken token)
        {
            using var peer = await listener.AcceptTcpClientAsync(token); var stream = peer.GetStream();
            var channel = await Handshake(stream, oracle, token);
            var autoOn = (byte)(oracle ? 6 : 2); var autoOff = (byte)(oracle ? 4 : 0);
            if (!secondary)
            {
                async Task Expect(string sql)
                {
                    channel.BeginCommand(); Assert.Equal(OceanBaseCommand.SqlPacket(sql, 3), await channel.ReadPacketAsync(token));
                }
                await Expect("SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"); await channel.WritePacketAsync(Ok(autoOn), token);
                await Expect("set autocommit = 0"); await channel.WritePacketAsync(Ok(autoOff), token);
                await Expect("UPDATE t SET n=1"); await channel.WritePacketAsync(Ok(autoOff, 1), token);
                await Expect("COMMIT"); if (lostReply) return;
                await channel.WritePacketAsync(Ok(autoOff), token);
                await Expect("set autocommit = 1"); await channel.WritePacketAsync(Ok(autoOn), token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        }
    }
}
