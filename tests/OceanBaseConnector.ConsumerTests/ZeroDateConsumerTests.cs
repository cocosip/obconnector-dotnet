using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
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
    public sealed class ZeroDateConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", true)]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.1", true)]
        [InlineData("netstandard2.1", false)]
        [InlineData("net10.0", true)]
        [InlineData("net10.0", false)]
        public async Task EachAssetInternalMySqlFixturePreservesZeroDates(string target, bool dateOnly)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var code = dateOnly ? (byte)10 : (byte)12;
            var metadata = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 100, 1, 100, 12, 45, 0, 30, 0, 0, 0, code, 0, 0, dateOnly ? (byte)0 : (byte)6, 0, 0 };
            var normal = dateOnly ? "2024-02-29" : "2024-02-29 01:02:03.123456";
            var partial = dateOnly ? "2024-00-12" : "2024-00-12 01:02:03.123456";
            var zero = dateOnly ? "0000-00-00" : "0000-00-00 00:00:00.000000";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                var query = await channel.ReadPacketAsync(budget.Token);
                Assert.Equal(3, query[0]); Assert.Equal("mixed", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(metadata, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                foreach (var text in new[] { normal, partial, zero })
                {
                    var bytes = Encoding.ASCII.GetBytes(text);
                    await channel.WritePacketAsync(new[] { (byte)bytes.Length }.Concat(bytes).ToArray(), budget.Token);
                }
                await channel.WritePacketAsync(new byte[] { 251 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(22, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 111, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(metadata, budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token); channel.BeginCommand();
                var execute = await channel.ReadPacketAsync(budget.Token);
                Assert.Equal(23, execute[0]); Assert.Equal(new byte[] { 0, 1, code, 0, 0 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]);
            });
            var context = new AssemblyLoadContext("zero-date-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                // A05 authentication remains unchanged; only this fixture supplies the post-auth MySQL fact.
                var session = connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var infoField = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = infoField.GetValue(session)!; var infoType = info.GetType();
                object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                infoField.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"),
                    Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                var providerType = assembly.GetType(dateOnly ? "OceanBaseConnector.OceanBaseDate" : "OceanBaseConnector.OceanBaseDateTime")!;
                using var command = connection.CreateCommand(); command.CommandText = "mixed"; object zeroValue;
                using (var reader = await command.ExecuteReaderAsync(budget.Token))
                {
                    Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Equal(providerType, reader.GetProviderSpecificFieldType(0));
                    Assert.Equal(providerType, Assert.Single(reader.GetColumnSchema())["ProviderSpecificDataType"]);
                    Assert.Equal(providerType, reader.GetSchemaTable()!.Rows[0]["ProviderSpecificDataType"]);
                    Assert.True(await reader.ReadAsync(budget.Token)); Assert.IsType<DateTime>(reader.GetValue(0));
                    Assert.Equal(normal, reader.GetProviderSpecificValue(0).ToString());
                    Assert.True(await reader.ReadAsync(budget.Token)); Assert.False(reader.IsDBNull(0));
                    Assert.Throws<InvalidCastException>(() => reader.GetValue(0)); Assert.Equal(partial, reader.GetProviderSpecificValue(0).ToString());
                    Assert.True(await reader.ReadAsync(budget.Token)); zeroValue = reader.GetProviderSpecificValue(0);
                    Assert.Equal(providerType, zeroValue.GetType()); Assert.Equal(zero, zeroValue.ToString());
                    Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<DateTime>(0));
                    var getter = reader.GetType().GetMethod("GetFieldValue")!.MakeGenericMethod(providerType);
                    Assert.Equal(zeroValue, getter.Invoke(reader, new object[] { 0 }));
                    Assert.True(await reader.ReadAsync(budget.Token)); Assert.True(reader.IsDBNull(0));
                    Assert.Equal(DBNull.Value, reader.GetValue(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.False(await reader.ReadAsync(budget.Token));
                }
                command.CommandText = "select :d"; var parameter = command.CreateParameter(); parameter.ParameterName = "d"; parameter.Value = zeroValue;
                Assert.Equal(dateOnly ? DbType.Date : DbType.DateTime, parameter.DbType); command.Parameters.Add(parameter);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                command.Dispose(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
