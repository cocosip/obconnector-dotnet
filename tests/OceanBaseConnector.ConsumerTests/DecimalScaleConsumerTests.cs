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
        public static IEnumerable<object[]> DecimalScaleAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var mysql in new[] { false, true }) yield return new object[] { target, asynchronous, mysql };
        }

        [Theory]
        [MemberData(nameof(DecimalScaleAssets))]
        public async Task DecimalScaleEveryAssetConstrainsWireAndPreservesTheSessionAfterRejection(string target, bool asynchronous, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                async Task<byte[]> Read() { channel.BeginCommand(); return await channel.ReadPacketAsync(budget.Token); }
                var prepare = await Read(); Assert.Equal(22, prepare[0]);
                Assert.Equal("insert into t values (?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 71, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                for (var round = 0; round < 2; round++)
                {
                    var text = mysql ? round == 0 ? "123.00" : "123" : "12300";
                    var expected = new byte[] { 23, 71, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 246, 0, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text));
                    Assert.Equal(expected, await Read());
                    // Preserve tracked session state so the next execution needs no unrelated probes.
                    await channel.WritePacketAsync(Report(false, 1), budget.Token);
                }
                Assert.Equal(new byte[] { 25, 71, 0, 0, 0 }, await Read());
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("decimal-scale-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // Internal authenticated-mode fixture; this is local transport evidence, not real-server acceptance.
                    var session = type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = mysql ? "insert into t values (?)" : "insert into t values (:p)";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.DbType = DbType.Decimal;
                    parameter.Value = mysql ? 123m : 12300m; parameter.Precision = mysql ? (byte)5 : (byte)3;
                    var scale = parameter.GetType().GetProperty("DecimalScale")!; Assert.Equal(typeof(int?), scale.PropertyType);
                    scale.SetValue(parameter, mysql ? 2 : -2); command.Parameters.Add(parameter);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    if (mysql)
                    {
                        scale.SetValue(parameter, -2);
                        if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync(budget.Token));
                        else Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                    }
                    else
                    {
                        parameter.Value = 12345m;
                        if (asynchronous) await Assert.ThrowsAsync<OverflowException>(() => command.ExecuteNonQueryAsync(budget.Token));
                        else Assert.Throws<OverflowException>(() => command.ExecuteNonQuery());
                    }
                    Assert.Equal(ConnectionState.Open, connection.State);
                    parameter.Value = mysql ? 123m : 12300m; parameter.Precision = 0; scale.SetValue(parameter, null);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
