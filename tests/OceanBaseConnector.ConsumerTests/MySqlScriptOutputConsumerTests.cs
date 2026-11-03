using System;
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
        [Theory]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.0", true)]
        [InlineData("netstandard2.1", false)]
        [InlineData("netstandard2.1", true)]
        [InlineData("net8.0", false)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", false)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", false)]
        [InlineData("net10.0", true)]
        public async Task PublicAssetsPublishMySqlScriptOutputs(string target, bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, wire, "unrequested", budget.Token, mysql: true);
                async Task<byte[]> Read(byte code)
                { channel.BeginCommand(); var packet = await channel.ReadPacketAsync(budget.Token); Assert.Equal(code, packet[0]); return packet; }
                var prepare = await Read(22); var initialization = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
                Assert.Matches("^SET @obc_[0-9a-f]{32}_0 = \\?$", initialization);
                var variable = initialization.Substring(4, initialization.Length - 8);
                await channel.WritePacketAsync(new byte[] { 0, 80, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, 253, 0, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                var execute = await Read(23); Assert.Equal(80, execute[1]); Assert.Equal(55, execute[execute.Length - 1]);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, budget.Token);
                Assert.Equal(80, (await Read(25))[1]);
                var script = await Read(3); Assert.Equal("SET " + variable + " = 42; SELECT 7", Encoding.UTF8.GetString(script, 1, script.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 10, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token);
                var output = await Read(3); Assert.Equal("SELECT " + variable, Encoding.UTF8.GetString(output, 1, output.Length - 1));
                await wire.WriteAsync(TextResult("42"), budget.Token);
                await ObserveClose(wire, budget.Token);
            });
            var context = new AssemblyLoadContext("mysql-script-output-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                using var connection = factory.CreateConnection()!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "SET ? = 42; SELECT 7";
                var parameter = command.CreateParameter(); parameter.Direction = ParameterDirection.InputOutput; parameter.DbType = DbType.String; parameter.Value = "7"; command.Parameters.Add(parameter);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery()); Assert.Equal("42", parameter.Value);
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch { if (serving.IsFaulted) await serving; throw; }
            finally { budget.Cancel(); listener.Stop(); try { await serving; } catch { } context.Unload(); }
        }
    }
}
