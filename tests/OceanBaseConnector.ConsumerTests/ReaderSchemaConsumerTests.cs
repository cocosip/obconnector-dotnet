using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class ReaderSchemaConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net8.0")]
        [InlineData("net9.0")]
        [InlineData("net10.0")]
        public async Task EachAssetExposesReaderSchemaThroughStandardAdoNet(string target)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                channel.BeginCommand();
                Assert.Equal(3, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token);
                // def, schema=t, no table, label/base name=n; NUMBER(6,2), display length8.
                await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 1, 116, 0, 0, 1, 110, 1, 110,
                    12, 45, 0, 8, 0, 0, 0, 246, 0, 0, 2, 6, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 5, 49, 50, 46, 51, 52 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
            });
            var context = new AssemblyLoadContext("schema-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                using var command = connection.CreateCommand();
                command.CommandText = "select n";
                using (var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, budget.Token))
                {
                    Assert.IsAssignableFrom<IDbColumnSchemaGenerator>(reader);
                    var column = Assert.Single(reader.GetColumnSchema());
                    Assert.Equal(typeof(decimal), column.DataType);
                    Assert.Equal("NUMBER", column.DataTypeName);
                    Assert.Equal(6, column.NumericPrecision);
                    Assert.Equal(2, column.NumericScale);
                    Assert.Equal("t", column.BaseSchemaName);
                    var providerType = Assert.IsAssignableFrom<Type>(column["ProviderSpecificDataType"]);
                    Assert.Equal(assembly, providerType.Assembly);
                    Assert.Equal(providerType, reader.GetSchemaTable()!.Rows[0][SchemaTableOptionalColumn.ProviderSpecificDataType]);
                    Assert.True(await reader.ReadAsync(budget.Token));
                    Assert.Equal(12.34m, reader.GetDecimal(0));
                    Assert.Equal(providerType, reader.GetProviderSpecificValue(0).GetType());
                    Assert.False(await reader.NextResultAsync(budget.Token));
                    Assert.Null(reader.GetSchemaTable());
                }
                Assert.Equal(ConnectionState.Closed, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
