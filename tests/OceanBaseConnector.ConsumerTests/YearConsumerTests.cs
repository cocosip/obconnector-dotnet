using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class YearConsumerTests
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
        public async Task EveryAssetReadsAndBindsYearThroughPublicApis(string target, bool binary)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var metadata = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 121, 1, 121, 12, 63, 0, 4, 0, 0, 0, 13, 32, 0, 0, 0, 0 };
            var eof = new byte[] { 254, 0, 0, 2, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(binary ? 22 : 3, (await channel.ReadPacketAsync(budget.Token))[0]);
                if (binary)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 114, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                    Assert.Equal(23, (await channel.ReadPacketAsync(budget.Token))[0]);
                }
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(metadata, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 0, 0 } : new byte[] { 4, 48, 48, 48, 48 }, budget.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 107, 8 } : new byte[] { 4, 50, 49, 53, 53 }, budget.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 }, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                if (binary) { Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]); channel.BeginCommand(); }
                Assert.Equal(22, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 115, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(metadata, budget.Token); await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                var execute = await channel.ReadPacketAsync(budget.Token);
                Assert.Equal(23, execute[0]); Assert.Equal(new byte[] { 0, 1, 2, 128, 107, 8 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]);
            });
            var context = new AssemblyLoadContext("year-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                // Supply the post-auth mode fact only in the fixture; public A05 authentication stays closed.
                var session = connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var infoField = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = infoField.GetValue(session)!; var infoType = info.GetType();
                object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                infoField.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"),
                    Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "years"; if (binary) await command.PrepareAsync(budget.Token);
                    using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token);
                    Assert.Equal(typeof(ushort), reader.GetFieldType(0)); Assert.Equal(typeof(ushort), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal("YEAR", reader.GetDataTypeName(0));
                    var schema = Assert.Single(reader.GetColumnSchema());
                    Assert.Equal(4, schema.NumericPrecision); Assert.Equal(0, schema.NumericScale); Assert.Equal(9, schema["ProviderType"]);
                    Assert.Equal(typeof(ushort), reader.GetSchemaTable()!.Rows[0]["DataType"]);
                    foreach (var year in new ushort[] { 0, 2155 })
                    {
                        Assert.True(await reader.ReadAsync(budget.Token));
                        Assert.Equal(year, reader.GetValue(0)); Assert.Equal(year, reader.GetProviderSpecificValue(0));
                        Assert.Equal(year, await reader.GetFieldValueAsync<ushort>(0, budget.Token));
                    }
                    Assert.True(await reader.ReadAsync(budget.Token)); Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0));
                    Assert.False(await reader.ReadAsync(budget.Token));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :y";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "y";
                    parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter,
                        Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "Year"));
                    parameter.Value = (ushort)2155; Assert.Equal(DbType.UInt16, parameter.DbType); command.Parameters.Add(parameter);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
