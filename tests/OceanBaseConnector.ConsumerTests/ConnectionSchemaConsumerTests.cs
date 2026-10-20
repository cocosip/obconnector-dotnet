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
    public sealed class ConnectionSchemaConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.1", false)]
        [InlineData("net8.0", false)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", false)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", false)]
        [InlineData("net10.0", true)]
        public async Task EachAssetExposesSchemaCollectionsAndReadsOracleDirectory(string target, bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token); channel.BeginCommand();
                var request = await channel.ReadPacketAsync(budget.Token); Assert.Equal(3, request[0]);
                Assert.Contains("ALL_OBJECTS", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 4 }, budget.Token);
                foreach (var name in new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE" })
                {
                    using var column = new MemoryStream();
                    foreach (var field in new[] { "def", "", "", "", name, name })
                    { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
                    column.Write(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(column.ToArray(), budget.Token);
                }
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                using var row = new MemoryStream(); row.WriteByte(251);
                foreach (var value in new[] { "Owner目标", "Mixed_Table", "TABLE" })
                { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
                await channel.WritePacketAsync(row.ToArray(), budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
            });
            var context = new AssemblyLoadContext("schema-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                Assert.Throws<InvalidOperationException>(() => connection.GetSchema()); await connection.OpenAsync(budget.Token);
                Assert.Equal(target.StartsWith("netstandard", StringComparison.Ordinal) ? 0 : 3,
                    connection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Count(x => x.Name == "GetSchemaAsync"));
                Assert.Equal(6, (asynchronous ? await connection.GetSchemaAsync(budget.Token) : connection.GetSchema()).Rows.Count);
                var information = asynchronous ? await connection.GetSchemaAsync("DataSourceInformation", budget.Token) : connection.GetSchema("DataSourceInformation");
                Assert.Equal(17, information.Columns.Count); Assert.Equal("OceanBase", information.Rows[0]["DataSourceProductName"]);
                var table = asynchronous ? await connection.GetSchemaAsync("Tables", null!, budget.Token) : connection.GetSchema("Tables", null!);
                var row = Assert.Single(table.Rows.Cast<DataRow>()); Assert.Equal(DBNull.Value, row[0]); Assert.Equal("Owner目标", row[1]);
                Assert.Equal("Mixed_Table", row[2]); Assert.Equal("TABLE", row[3]); Assert.Equal(ConnectionState.Open, connection.State);
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
