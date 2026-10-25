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
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> OracleAutocommitAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var reset in new[] { false, true })
                        yield return new object[] { target, asynchronous, reset };
        }

        [Theory]
        [MemberData(nameof(OracleAutocommitAssets))]
        public async Task EveryAssetDiscardsUnconfirmedOracleAutocommitAndRecoversPoolCapacity(string target, bool asynchronous, bool reset)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync(budget.Token))
                {
                    var wire = first.GetStream(); var channel = new PacketChannel(wire);
                    await Handshake(channel, wire, "unrequested", budget.Token, initializeAutocommit: reset);
                    channel.BeginCommand();
                    if (reset)
                    {
                        Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(budget.Token));
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0 }, budget.Token);
                        channel.BeginCommand();
                    }
                    var setup = await channel.ReadPacketAsync(budget.Token);
                    Assert.Equal(3, setup[0]);
                    Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(setup, 1, setup.Length - 1));
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    await ObserveClose(wire, budget.Token);
                }
                using var second = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = second.GetStream(); var next = new PacketChannel(stream);
                await Handshake(next, stream, "unrequested", budget.Token);
                var command = await next.ReadPacketAsync(budget.Token);
                Assert.Equal(3, command[0]);
                Assert.Equal("after initialization failure", Encoding.ASCII.GetString(command, 1, command.Length - 1));
                await next.WritePacketAsync(Report(false, 1), budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("oracle-autocommit-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1",
                    ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u",
                    ["TLS Mode"] = "Disabled",
                    ["Connection Timeout"] = 3,
                    ["Pooling"] = true,
                    ["Max Pool Size"] = 1,
                    ["Pool Wait Timeout"] = 1,
                }.ConnectionString;
                if (reset)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                else
                {
                    var error = asynchronous ? await Record.ExceptionAsync(() => connection.OpenAsync(budget.Token)) : Record.Exception(connection.Open);
                    Assert.IsAssignableFrom<DbException>(error);
                    Assert.Equal(ConnectionState.Closed, connection.State);
                }
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "after initialization failure";
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
