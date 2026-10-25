using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
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
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.1", false)]
        [InlineData("net8.0", false)]
        [InlineData("net9.0", false)]
        [InlineData("net10.0", false)]
        [InlineData("netstandard2.0", true)]
        [InlineData("netstandard2.1", true)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", true)]
        public Task EachAssetExposesReaderSchemaThroughStandardAdoNet(string target, bool wide)
            => ReadSchema(target, wide, unknownPrecision: false);

        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net8.0")]
        [InlineData("net9.0")]
        [InlineData("net10.0")]
        public Task EachAssetReadsUnknownPrecisionOracleNumbers(string target)
            => ReadSchema(target, wide: false, unknownPrecision: true);

        private static async Task ReadSchema(string target, bool wide, bool unknownPrecision)
        {
            const string large = "123456789012345678901234567890123456.12";
            var precision = unknownPrecision ? (byte)0 : wide ? (byte)38 : (byte)6;
            var metadata = new byte[] { 3, 100, 101, 102, 1, 116, 0, 0, 1, 110, 1, 110,
                12, 45, 0, (byte)(precision + 2), 0, 0, 0, 246, 0, 0, unknownPrecision ? (byte)255 : (byte)2, precision, 0 };
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
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token);
                channel.BeginCommand();
                Assert.Equal(3, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token);
                await channel.WritePacketAsync(metadata, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                if (wide) await channel.WritePacketAsync(new[] { (byte)large.Length }.Concat(Encoding.ASCII.GetBytes(large)).ToArray(), budget.Token);
                await channel.WritePacketAsync(new byte[] { 5, 49, 50, 46, 51, 52 }, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                if (wide)
                {
                    channel.BeginCommand();
                    Assert.Equal(22, (await SessionTimeZoneFixture.ReadCommand(channel, budget.Token))[0]);
                    await channel.WritePacketAsync(new byte[] { 0, 91, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                    await channel.WritePacketAsync(metadata, budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    channel.BeginCommand();
                    var execute = await channel.ReadPacketAsync(budget.Token);
                    Assert.Equal(23, execute[0]);
                    Assert.Equal(new byte[] { 0, 1, 246, 0 }, execute.AsSpan(10, 4).ToArray());
                    Assert.Equal(large, Encoding.ASCII.GetString(new ByteCursor(execute.AsSpan(14).ToArray()).ReadLengthEncodedBytes()));
                    await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
                    channel.BeginCommand();
                    Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]);
                }
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
                object? provider = null;
                var decimalType = assembly.GetType("OceanBaseConnector.OceanBaseDecimal")!;
                using (var reader = await command.ExecuteReaderAsync(wide ? CommandBehavior.Default : CommandBehavior.CloseConnection, budget.Token))
                {
                    Assert.IsAssignableFrom<IDbColumnSchemaGenerator>(reader);
                    var column = Assert.Single(reader.GetColumnSchema());
                    Assert.Equal(wide || unknownPrecision ? decimalType : typeof(decimal), column.DataType);
                    Assert.Equal("NUMBER", column.DataTypeName);
                    Assert.Equal(unknownPrecision ? (int?)null : precision, column.NumericPrecision);
                    Assert.Equal(unknownPrecision ? -1 : 2, column.NumericScale);
                    Assert.Equal("t", column.BaseSchemaName);
                    var providerType = Assert.IsAssignableFrom<Type>(column["ProviderSpecificDataType"]);
                    Assert.Equal(assembly, providerType.Assembly);
                    Assert.Equal(providerType, reader.GetSchemaTable()!.Rows[0][SchemaTableOptionalColumn.ProviderSpecificDataType]);
                    var identity = Assert.IsType<int>(column[SchemaTableColumn.ProviderType]);
                    Assert.Equal("Decimal", Enum.GetName(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, identity));
                    Assert.Equal(identity, reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ProviderType]);
                    Assert.True(await reader.ReadAsync(budget.Token));
                    if (wide)
                    {
                        provider = reader.GetValue(0);
                        Assert.Equal(decimalType, provider.GetType());
                        Assert.Equal(large, provider.ToString());
                        Assert.Equal(provider, reader.GetProviderSpecificValue(0));
                        Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
                        Assert.Throws<OverflowException>(() => reader.GetFieldValue<decimal>(0));
                        Assert.True(await reader.ReadAsync(budget.Token));
                        Assert.Equal(decimalType, reader.GetValue(0).GetType());
                    }
                    Assert.Equal(12.34m, reader.GetDecimal(0));
                    Assert.Equal(12.34m, reader.GetFieldValue<decimal>(0));
                    Assert.Equal(providerType, reader.GetProviderSpecificValue(0).GetType());
                    if (unknownPrecision)
                    {
                        Assert.Equal(decimalType, reader.GetValue(0).GetType());
                        Assert.Equal("12.34", reader.GetValue(0).ToString());
                    }
                    Assert.False(await reader.NextResultAsync(budget.Token));
                    Assert.Null(reader.GetSchemaTable());
                }
                if (wide)
                {
                    command.CommandText = "select :n";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "n"; parameter.Value = provider;
                    command.Parameters.Add(parameter);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                    command.Dispose(); connection.Close();
                }
                Assert.Equal(ConnectionState.Closed, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
