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
    public sealed class JsonConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false, false)]
        [InlineData("netstandard2.0", true, false)]
        [InlineData("netstandard2.1", false, false)]
        [InlineData("netstandard2.1", true, false)]
        [InlineData("net10.0", false, false)]
        [InlineData("net10.0", true, false)]
        [InlineData("netstandard2.0", false, true)]
        [InlineData("netstandard2.0", true, true)]
        [InlineData("netstandard2.1", false, true)]
        [InlineData("netstandard2.1", true, true)]
        [InlineData("net10.0", false, true)]
        [InlineData("net10.0", true, true)]
        public async Task EveryAssetReadsAndBindsJsonTextThroughPublicApis(string target, bool binary, bool sequential)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var metadata = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 106, 1, 106, 12, 63, 0, 255, 255, 255, 255, 245, 0, 0, 31, 0, 0 };
            var eof = new byte[] { 254, 0, 0, 2, 0 };
            const string json = " {\"text\":\"中😀\\u0000\",\"n\":1234567890123456789012345678901234567890.123456789,\"e\":1e300} ";
            var bytes = Encoding.UTF8.GetBytes(json);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                Assert.Equal(binary ? 22 : 3, (await channel.ReadPacketAsync(budget.Token))[0]);
                if (binary)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 124, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                    Assert.Equal(23, (await channel.ReadPacketAsync(budget.Token))[0]);
                }
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(metadata, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                foreach (var document in new[] { Encoding.UTF8.GetBytes("null"), bytes, bytes })
                    await channel.WritePacketAsync((binary ? new byte[] { 0, 0 } : Array.Empty<byte>()).Concat(new[] { (byte)document.Length }).Concat(document).ToArray(), budget.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 }, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                if (binary) { Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]); channel.BeginCommand(); }
                Assert.Equal(22, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 125, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(metadata, budget.Token); await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                var execute = await channel.ReadPacketAsync(budget.Token);
                Assert.Equal(23, execute[0]);
                Assert.Equal(new byte[] { 0, 1, 253, 0, (byte)bytes.Length }.Concat(bytes), execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(25, (await channel.ReadPacketAsync(budget.Token))[0]);
            });
            var context = new AssemblyLoadContext("json-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                // Only the fixture supplies the post-auth MySQL fact; the public A05 gate remains closed.
                var session = connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var infoField = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = infoField.GetValue(session)!; var infoType = info.GetType();
                object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                infoField.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"),
                    Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "json"; if (binary) await command.PrepareAsync(budget.Token);
                    using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token);
                    Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal(typeof(string), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal("JSON", reader.GetDataTypeName(0));
                    var schema = Assert.Single(reader.GetColumnSchema());
                    Assert.Null(schema.ColumnSize); Assert.Null(schema.NumericPrecision); Assert.Null(schema.NumericScale);
                    Assert.True(schema.IsLong); Assert.Equal(7, schema["ProviderType"]);
                    Assert.Equal(DBNull.Value, reader.GetSchemaTable()!.Rows[0]["ColumnSize"]);
                    foreach (var document in new[] { "null", json })
                    {
                        Assert.True(await reader.ReadAsync(budget.Token)); Assert.False(reader.IsDBNull(0));
                        Assert.Equal(document, await reader.GetFieldValueAsync<string>(0, budget.Token));
                        Assert.Equal(document, reader.GetValue(0)); Assert.Equal(document, reader.GetProviderSpecificValue(0));
                    }
                    Assert.True(await reader.ReadAsync(budget.Token));
                    using (var text = reader.GetTextReader(0)) Assert.Equal(json, await text.ReadToEndAsync());
                    Assert.True(await reader.ReadAsync(budget.Token)); Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.False(await reader.ReadAsync(budget.Token));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select ?"; var parameter = command.CreateParameter(); parameter.Value = json;
                    parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter,
                        Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "Json"));
                    Assert.Equal(DbType.String, parameter.DbType); command.Parameters.Add(parameter);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
