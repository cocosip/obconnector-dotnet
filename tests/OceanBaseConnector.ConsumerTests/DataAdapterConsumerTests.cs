using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
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
        [Theory]
        [MemberData(nameof(DecimalScaleAssets))]
        public async Task EveryAssetAdapterFillsMappedDataSetAndGeneratesConcurrencyUpdate(string target, bool keySchema, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var sql = mysql ? "select id, payload from `Tenant`.ledger" : "select id, payload from \"Tenant\".\"ledger\"";
            var columns = new[] { KeyConsumerColumn("id", 8, "id", 1), KeyConsumerColumn("payload", 253, "payload") };
            var mode = mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                async Task<byte[]> Read() { channel.BeginCommand(); return await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false); }
                async Task Prepare(byte id, ushort parameters, params byte[][] metadata)
                {
                    await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, (byte)metadata.Length, 0, (byte)parameters, 0, 0, 0, 0 }, budget.Token);
                    for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(KeyConsumerColumn("p" + i), budget.Token);
                    if (parameters != 0) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    foreach (var column in metadata) await channel.WritePacketAsync(column, budget.Token);
                    if (metadata.Length != 0) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                }
                async Task Rows(bool binary)
                {
                    await channel.WritePacketAsync(new byte[] { 2 }, budget.Token);
                    foreach (var column in columns) await channel.WritePacketAsync(column, budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 120 } : new byte[] { 1, 49, 1, 120 }, budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                }
                if (!keySchema)
                {
                    var query = await Read(); Assert.Equal(3, query[0]); Assert.Equal(sql, Encoding.UTF8.GetString(query, 1, query.Length - 1));
                    await Rows(false);
                }
                var prepare = await Read(); Assert.Equal(22, prepare[0]); Assert.Equal(sql, Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepare(91, 0, columns);
                for (var round = 0; round < (keySchema ? 2 : 1); round++)
                {
                    var directory = await Read(); Assert.Equal(22, directory[0]);
                    Assert.Contains(mysql ? "INFORMATION_SCHEMA.STATISTICS" : "ALL_CONS_COLUMNS", Encoding.UTF8.GetString(directory, 1, directory.Length - 1));
                    await Prepare(92, 2);
                    Assert.Equal(PreparedParameters.Execute(92, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = "Tenant" }, new OceanBaseParameter { Value = "ledger" } }, mode)), await Read());
                    await channel.WritePacketAsync(new byte[] { 8 }, budget.Token);
                    for (var i = 0; i < 8; i++) await channel.WritePacketAsync(KeyConsumerColumn("k" + i), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    using var row = new MemoryStream(); row.Write(new byte[] { 0, 0, 0 });
                    foreach (var value in new[] { "Tenant", "ledger", "PK", "P", "id", "1", "NO", "YES" })
                    { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
                    await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    Assert.Equal(new byte[] { 25, 92, 0, 0, 0 }, await Read());
                    if (keySchema && round == 0) { Assert.Equal(PreparedParameters.Execute(91, Array.Empty<byte>()), await Read()); await Rows(true); }
                }
                var update = await Read(); Assert.Equal(22, update[0]);
                var text = Encoding.UTF8.GetString(update, 1, update.Length - 1); var quote = mysql ? "`" : "\"";
                Assert.StartsWith("UPDATE " + quote + "Tenant" + quote + "." + quote + "ledger" + quote, text);
                Assert.Contains(" SET " + quote + "payload" + quote + " = ?", text); Assert.Contains(" WHERE ", text); Assert.DoesNotContain(":p", text);
                await Prepare(81, 4);
                Assert.Equal(PreparedParameters.Execute(81, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = "y" }, new OceanBaseParameter { Value = 1L }, new OceanBaseParameter { Value = 0 }, new OceanBaseParameter { Value = "x" } }, mode)), await Read());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
                var closed = new HashSet<byte>();
                for (var i = 0; i < 2; i++) { var close = await Read(); Assert.Equal(25, close[0]); closed.Add(close[1]); }
                Assert.True(closed.SetEquals(new byte[] { 91, 81 })); await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("adapter-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                using var connection = factory.CreateConnection()!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                connection.Open();
                if (mysql)
                {
                    // Internal dual-mode fixture, not real MySQL-mode interoperability.
                    var session = connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using (var select = connection.CreateCommand())
                using (var adapter = factory.CreateDataAdapter()!)
                using (var builder = factory.CreateCommandBuilder()!)
                using (var data = new DataSet())
                {
                    select.CommandText = sql; adapter.SelectCommand = select; builder.DataAdapter = adapter;
                    adapter.MissingSchemaAction = keySchema ? MissingSchemaAction.AddWithKey : MissingSchemaAction.Add;
                    adapter.TableMappings.Add("Table", "client").ColumnMappings.Add("payload", "label");
                    Assert.Equal(1, adapter.Fill(data, "Table")); var table = data.Tables["client"]!;
                    Assert.Equal("x", table.Rows[0]["label"]); Assert.Equal(keySchema ? 1 : 0, table.PrimaryKey.Length);
                    Assert.Contains(mysql ? "?" : ":p1", builder.GetUpdateCommand().CommandText);
                    table.Rows[0]["label"] = "y"; Assert.Equal(1, adapter.Update(data, "Table"));
                    Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState); Assert.Equal("y", table.Rows[0]["label", DataRowVersion.Original]);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
