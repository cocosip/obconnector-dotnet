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
        public static IEnumerable<object[]> TopologyAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var disconnect in new[] { false, true }) yield return new object[] { target, asynchronous, ob20, disconnect };
        }

        [Theory]
        [MemberData(nameof(TopologyAssets))]
        public async Task EveryAssetDiscoversAndRotatesWithoutReplayingSql(string target, bool asynchronous, bool ob20, bool disconnect)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
            using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
            async Task Serve(TcpListener listener, byte affected, bool fail)
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token);
                var query = await ReadRequest(stream, channel, ob20, 0, budget.Token);
                Assert.Equal(3, query[0]); Assert.Equal("selected", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                if (fail) return;
                var response = Frame(1, Report(false, affected));
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 0, 1, response) : response, budget.Token);
                await ObserveClose(stream, budget.Token);
            }
            var serving = Task.WhenAll(Task.Run(() => Serve(first, 1, false)), Task.Run(() => Serve(second, 2, disconnect)));
            var context = new AssemblyLoadContext("topology-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var helpers = context.LoadFromAssemblyPath(typeof(NetStandard.TopologyAddressResolver).Assembly.Location);
                var resolverType = helpers.GetType("OceanBaseConnector.ConsumerTests.NetStandard.TopologyAddressResolver")!;
                var addresses = $"127.0.0.1:{((IPEndPoint)first.LocalEndpoint).Port},127.0.0.1:{((IPEndPoint)second.LocalEndpoint).Port}";
                var resolver = Activator.CreateInstance(resolverType, addresses, asynchronous)!;
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "bootstrap.invalid",
                    ["User ID"] = "u",
                    ["TLS Mode"] = "Disabled",
                    ["Use OB20"] = ob20,
                    ["Load Balance"] = "RoundRobin",
                    ["Connection Timeout"] = 5,
                }.ConnectionString;
                connection.GetType().GetProperty("AddressResolver")!.SetValue(connection, resolver);
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "selected";
                    if (i == 1 && disconnect)
                    {
                        var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); });
                        Assert.NotNull(error); Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                        Assert.Equal(ConnectionState.Broken, connection.State);
                    }
                    else Assert.Equal(i + 1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    connection.Close();
                }
                Assert.Equal(2, resolverType.GetProperty("Calls")!.GetValue(resolver)); Assert.False(first.Pending()); Assert.False(second.Pending());
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); first.Stop(); second.Stop(); context.Unload(); }
        }
    }
}
