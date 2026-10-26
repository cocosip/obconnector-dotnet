using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> CloseCallbackAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var mysql in new[] { false, true })
                    foreach (var closeAsync in new[] { false, true })
                        foreach (var openAsync in new[] { false, true })
                            yield return new object[] { target, mysql, closeAsync, openAsync };
        }

        [Theory]
        [MemberData(nameof(CloseCallbackAssets))]
        public async Task EveryAssetReopensTheSameCapacityOnePoolInsideClosedCallback(string target, bool mysql, bool closeAsync, bool openAsync)
            => await VerifyCloseCallback(target, mysql, closeAsync, openAsync, transactionLogger: false);

        [Theory]
        [MemberData(nameof(CloseCallbackAssets))]
        public Task EveryAssetReopensTheSameCapacityOnePoolInsideClosingTransactionLogger(string target, bool mysql, bool closeAsync, bool openAsync)
            => VerifyCloseCallback(target, mysql, closeAsync, openAsync, transactionLogger: true);

        private static async Task VerifyCloseCallback(string target, bool mysql, bool closeAsync, bool openAsync, bool transactionLogger)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", stop.Token, mysql: mysql);
                if (transactionLogger)
                    foreach (var sql in new[] { "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", "set autocommit = 0", "ROLLBACK", "set autocommit = 1" })
                    {
                        channel.BeginCommand();
                        var request = mysql ? await channel.ReadPacketAsync(stop.Token) : await SessionTimeZoneFixture.ReadCommand(channel, stop.Token, expectProbe: false);
                        Assert.Equal(3, request[0]); Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, sql == "set autocommit = 0" || sql == "ROLLBACK" ? (byte)4 : (byte)2, 0, 0, 0 }, stop.Token);
                    }
                channel.BeginCommand(); Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(stop.Token));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                if (!mysql) await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                channel.BeginCommand(); Assert.Equal(new byte[] { 14 }, await channel.ReadPacketAsync(stop.Token));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                foreach (var sql in new[] { "inside callback", "after callback" })
                {
                    channel.BeginCommand(); var packet = mysql ? await channel.ReadPacketAsync(stop.Token) : await SessionTimeZoneFixture.ReadCommand(channel, stop.Token, expectProbe: false);
                    Assert.Equal(3, packet[0]); Assert.Equal(sql, Encoding.UTF8.GetString(packet, 1, packet.Length - 1));
                    await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, stop.Token);
                }
                await ObserveClose(stream, stop.Token);
            });
            var context = new AssemblyLoadContext("close-callback-" + target, isCollectible: true);
            Exception? failure = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                var logs = new RecordingLoggerFactory();
                using var connection = transactionLogger ? (DbConnection)Activator.CreateInstance(type, "", logs)! : (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1",
                    ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u",
                    ["TLS Mode"] = "Disabled",
                    ["Pooling"] = true,
                    ["Max Pool Size"] = 1,
                    ["Pool Wait Timeout"] = 0,
                }.ConnectionString;
                var reopen = true;
                Exception? logError = null;
                void Reopen()
                {
                    if (!reopen) return;
                    reopen = false;
                    if (openAsync) connection.OpenAsync(stop.Token).GetAwaiter().GetResult(); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "inside callback"; Assert.Equal(1, command.ExecuteNonQuery());
                }
                connection.StateChange += (_, args) =>
                {
                    if (!transactionLogger && args.CurrentState == ConnectionState.Closed) Reopen();
                };
                logs.OnLog = id => { if (id.Name == "TransactionRollbackCompleted") logError = Record.Exception(Reopen); };
                try
                {
                    await connection.OpenAsync(stop.Token);
                    using var transaction = transactionLogger ? connection.BeginTransaction(IsolationLevel.ReadCommitted) : null;
                    await Task.Run(async () => { if (closeAsync) await connection.CloseAsync(); else connection.Close(); }).WaitAsync(stop.Token);
                    Assert.Null(logError); Assert.False(reopen); if (transaction != null) Assert.Null(transaction.Connection);
                    Assert.Equal(ConnectionState.Open, connection.State);
                    using var after = connection.CreateCommand(); after.CommandText = "after callback"; Assert.Equal(1, after.ExecuteNonQuery());
                }
                finally
                {
                    reopen = false; type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection }); connection.Close();
                }
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                stop.Cancel(); listener.Stop(); context.Unload();
                try { await serving; }
                catch (OperationCanceledException) when (failure != null) { }
                catch (SocketException) when (failure != null) { }
                catch (ObjectDisposedException) when (failure != null) { }
            }
        }
    }
}
