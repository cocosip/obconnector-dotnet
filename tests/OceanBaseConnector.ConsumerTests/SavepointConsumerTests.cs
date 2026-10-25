using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.1", false)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", true)]
        public void SavepointApisOnlyOverrideAvailablePlatformContracts(string target, bool modern)
        {
            var context = new AssemblyLoadContext("savepoint-surface-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseTransaction")!;
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var name in new[] { "Save", "SaveAsync", "Release", "ReleaseAsync", "get_SupportsSavepoints" })
                    Assert.Equal(modern, methods.Any(method => method.Name == name));
                Assert.Equal(modern, methods.Any(method => method.Name == "Rollback" && method.GetParameters().Length == 1));
                foreach (var method in methods.Where(method => method.Name == "Save" || method.Name == "SaveAsync"))
                    Assert.Equal(typeof(DbTransaction), method.GetBaseDefinition().DeclaringType);
            }
            finally { context.Unload(); }
        }

        public static IEnumerable<object[]> SavepointAssets()
        {
            foreach (var target in new[] { "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var mysql in new[] { false, true })
                        foreach (var ob20 in new[] { false, true })
                            foreach (var outcome in new[] { "success", "error", "disconnect", "boundary" })
                                yield return new object[] { target, asynchronous, mysql, ob20, outcome };
        }

        [Theory]
        [MemberData(nameof(SavepointAssets))]
        public async Task ModernAssetsControlSavepointsWithoutEndingOrReplayingTransactions(string target, bool asynchronous, bool mysql, bool ob20, string outcome)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token);
                var commands = new List<string>
                {
                    "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE", "set autocommit = 0",
                    mysql ? "SAVEPOINT `Point_1`" : "SAVEPOINT Point_1",
                    mysql ? "ROLLBACK TO SAVEPOINT `Point_1`" : "ROLLBACK TO Point_1",
                };
                if (mysql && outcome == "success") commands.Add("RELEASE SAVEPOINT `Point_1`");
                commands.AddRange(new[] { "ROLLBACK", "set autocommit = 1" });
                for (uint i = 0; i < commands.Count; i++)
                {
                    var query = await ReadRequest(stream, channel, ob20, i + 2, budget.Token);
                    Assert.Equal(3, query[0]); Assert.Equal(commands[(int)i], Encoding.UTF8.GetString(query, 1, query.Length - 1));
                    if (asynchronous && i == 2) { requested.SetResult(); await respond.Task.WaitAsync(budget.Token); }
                    if (i == 3 && outcome == "disconnect") return;
                    var packet = i == 3 && outcome == "error" ? new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48 }
                        : new byte[] { 0, 0, 0, (byte)(i == commands.Count - 1 ? 2 : i < 2 || commands[(int)i] == "ROLLBACK" ? 0 : 1), 0, 0, 0 };
                    if (i == 3 && outcome == "boundary" && !ob20) packet[3] = 9;
                    var response = Frame(1, packet);
                    if (i == 3 && outcome == "boundary" && ob20) response = response.Concat(Frame(2, new byte[] { 0 })).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, i + 2, 1, response) : response, budget.Token);
                    if (i == 3 && outcome == "boundary") break;
                    if (!ob20) channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("savepoint-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // This is an explicit post-authentication fixture, not A05 MySQL identification.
                    var session = connectionType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType();
                    object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                        Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"),
                        Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using var transaction = asynchronous ? await connection.BeginTransactionAsync(IsolationLevel.Serializable, budget.Token) : connection.BeginTransaction(IsolationLevel.Serializable);
                Assert.True(transaction.SupportsSavepoints);
                if (asynchronous)
                {
                    var saving = transaction.SaveAsync("Point_1", budget.Token);
                    await requested.Task.WaitAsync(budget.Token); Assert.False(saving.IsCompleted);
                    respond.SetResult(); await saving;
                }
                else transaction.Save("Point_1");
                async Task RollbackTo()
                { if (asynchronous) await transaction.RollbackAsync("Point_1", budget.Token); else transaction.Rollback("Point_1"); }
                if (outcome == "disconnect" || outcome == "boundary")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(RollbackTo);
                    Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
                }
                else
                {
                    if (outcome == "error")
                    {
                        var error = await Assert.ThrowsAnyAsync<DbException>(RollbackTo);
                        Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    }
                    else
                    {
                        await RollbackTo();
                        if (mysql) { if (asynchronous) await transaction.ReleaseAsync("Point_1", budget.Token); else transaction.Release("Point_1"); }
                        else if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => transaction.ReleaseAsync("Point_1", budget.Token));
                        else Assert.Throws<NotSupportedException>(() => transaction.Release("Point_1"));
                    }
                    Assert.Same(connection, transaction.Connection);
                    if (asynchronous) await transaction.RollbackAsync(budget.Token); else transaction.Rollback();
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { respond.TrySetResult(); budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
