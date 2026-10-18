using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
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
        public async Task EachAssetPreservesPreciseTimestampsAndWritesProviderValuesBack(string target, bool zoned)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var metadata = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 116, 1, 116, 12, 45, 0, 30, 0, 0, 0, 202, 0, 0, 9, 0, 0 };
            metadata[18] = zoned ? (byte)200 : (byte)202;
            var payload = new byte[] { 20, 24, 2, 29, 13, 14, 15, 21, 205, 91, 7, 9 };
            if (zoned) payload = payload.Concat(new byte[] { 5, 30, 12 }).Concat(Encoding.UTF8.GetBytes("Asia/Kolkata"))
                .Concat(new byte[] { 3 }).Concat(Encoding.UTF8.GetBytes("IST")).ToArray();
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
                await channel.WritePacketAsync(new[] { (byte)payload.Length }.Concat(payload).ToArray(), budget.Token);
                var exact = (byte[])payload.Clone(); exact[7] = 100; exact[8] = exact[9] = exact[10] = 0;
                await channel.WritePacketAsync(new[] { (byte)exact.Length }.Concat(exact).ToArray(), budget.Token);
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
                Assert.Equal(new byte[] { 0, 1, zoned ? (byte)200 : (byte)202, 0, (byte)payload.Length }.Concat(payload).ToArray(), execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
                channel.BeginCommand();
                Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]);
            });
            var context = new AssemblyLoadContext("timestamp-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var timestampType = assembly.GetType(zoned ? "OceanBaseConnector.OceanBaseTimestampWithTimeZone" : "OceanBaseConnector.OceanBaseTimestamp")!;
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
                        Assert.Equal(zoned ? "TIMESTAMP WITH TIME ZONE" : "TIMESTAMP", column.DataTypeName);
                        var identity = Assert.IsType<int>(column[SchemaTableColumn.ProviderType]);
                        Assert.Equal(zoned ? "TimestampWithTimeZone" : "Timestamp", Enum.GetName(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, identity));
                        Assert.Equal(timestampType, reader.GetSchemaTable()!.Rows[0]["DataType"]);
                        Assert.True(await reader.ReadAsync(budget.Token));
                        provider = reader.GetProviderSpecificValue(0);
                        Assert.Equal(timestampType, provider.GetType());
                        Assert.Equal(zoned ? "2024-02-29 13:14:15.123456789 +05:30 [Asia/Kolkata] [IST]" : "2024-02-29 13:14:15.123456789", provider.ToString());
                        Assert.Equal(provider, reader.GetValue(0));
                        if (zoned)
                        {
                            Assert.Throws<OverflowException>(() => reader.GetFieldValue<DateTimeOffset>(0));
                            Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
                        }
                        else
                        {
                            Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
                            Assert.Throws<OverflowException>(() => reader.GetFieldValue<DateTime>(0));
                        }
                        Assert.True(await reader.ReadAsync(budget.Token));
                        Assert.Equal(timestampType, reader.GetValue(0).GetType());
                        if (zoned) Assert.Equal(new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.FromMinutes(330)).AddTicks(1), await reader.GetFieldValueAsync<DateTimeOffset>(0, budget.Token));
                        else Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1), await reader.GetFieldValueAsync<DateTime>(0, budget.Token));
                        Assert.True(await reader.ReadAsync(budget.Token));
                        Assert.True(reader.IsDBNull(0));
                        Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                        Assert.Equal(timestampType, reader.GetFieldType(0));
                        Assert.False(await reader.ReadAsync(budget.Token));
                    }
                    command.CommandText = "select :t";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "t"; parameter.Value = provider;
                    Assert.Equal(zoned ? DbType.DateTimeOffset : DbType.DateTime2, parameter.DbType);
                    command.Parameters.Add(parameter);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
