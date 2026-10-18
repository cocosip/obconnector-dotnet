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
    public sealed class TimestampConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net8.0")]
        [InlineData("net9.0")]
        [InlineData("net10.0")]
        public async Task EachAssetPreservesNanosecondsAndWritesProviderTimestampBack(string target)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var metadata = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 116, 1, 116, 12, 45, 0, 30, 0, 0, 0, 202, 0, 0, 9, 0, 0 };
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
                await channel.WritePacketAsync(metadata, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 12, 20, 24, 2, 29, 13, 14, 15, 21, 205, 91, 7, 9 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 12, 20, 24, 2, 29, 13, 14, 15, 100, 0, 0, 0, 9 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 251 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                channel.BeginCommand();
                Assert.Equal(22, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 94, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(metadata, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                channel.BeginCommand();
                var execute = await channel.ReadPacketAsync(budget.Token);
                Assert.Equal(23, execute[0]);
                Assert.Equal(new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 21, 205, 91, 7, 9 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
                channel.BeginCommand();
                Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]);
            });
            var context = new AssemblyLoadContext("timestamp-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var timestampType = assembly.GetType("OceanBaseConnector.OceanBaseTimestamp")!;
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select t";
                    object provider;
                    using (var reader = await command.ExecuteReaderAsync(budget.Token))
                    {
                        var column = Assert.Single(reader.GetColumnSchema());
                        Assert.Equal(timestampType, column.DataType);
                        Assert.Equal(timestampType, column["ProviderSpecificDataType"]);
                        Assert.Equal(timestampType, reader.GetProviderSpecificFieldType(0));
                        Assert.Equal("TIMESTAMP", column.DataTypeName);
                        var identity = Assert.IsType<int>(column[SchemaTableColumn.ProviderType]);
                        Assert.Equal("Timestamp", Enum.GetName(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, identity));
                        Assert.Equal(timestampType, reader.GetSchemaTable()!.Rows[0]["DataType"]);
                        Assert.True(await reader.ReadAsync(budget.Token));
                        provider = reader.GetProviderSpecificValue(0);
                        Assert.Equal(timestampType, provider.GetType());
                        Assert.Equal("2024-02-29 13:14:15.123456789", provider.ToString());
                        Assert.Equal(provider, reader.GetValue(0));
                        Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
                        Assert.Throws<OverflowException>(() => reader.GetFieldValue<DateTime>(0));
                        Assert.True(await reader.ReadAsync(budget.Token));
                        Assert.Equal(timestampType, reader.GetValue(0).GetType());
                        Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1), await reader.GetFieldValueAsync<DateTime>(0, budget.Token));
                        Assert.True(await reader.ReadAsync(budget.Token));
                        Assert.True(reader.IsDBNull(0));
                        Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                        Assert.Equal(timestampType, reader.GetFieldType(0));
                        Assert.False(await reader.ReadAsync(budget.Token));
                    }
                    command.CommandText = "select :t";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "t"; parameter.Value = provider;
                    Assert.Equal(DbType.DateTime2, parameter.DbType);
                    command.Parameters.Add(parameter);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
