using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
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
        public static IEnumerable<object[]> ModernBatchMetadataAssets()
        {
            foreach (var target in new[] { "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var dataSource in new[] { false, true })
                        foreach (var mysql in new[] { false, true })
                            foreach (var behavior in new[] { CommandBehavior.SchemaOnly, CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, CommandBehavior.KeyInfo })
                                yield return new object[] { target, asynchronous, dataSource, mysql, behavior };
        }

        [Theory]
        [MemberData(nameof(ModernBatchMetadataAssets))]
        public async Task ModernAssetsConsumeBatchMetadataAndReleaseOwnedConnections(string target, bool asynchronous, bool dataSource, bool mysql, CommandBehavior behavior)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var keyInfo = (behavior & CommandBehavior.KeyInfo) != 0; var schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
            var sql = mysql ? "select l.payload from `Tenant`.ledger l" : "select l.payload from \"Tenant\".\"ledger\" l";
            var original = KeyConsumerColumn("payload", 253, "payload"); var hidden = KeyConsumerColumn("__ob_key_0", 8, "id", 1);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token, mysql: mysql);
                async Task<byte[]> Read() { channel.BeginCommand(); return await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false); }
                async Task Prepare(byte id, int parameters, params byte[][] columns)
                {
                    await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, (byte)columns.Length, 0, (byte)parameters, 0, 0, 0, 0 }, budget.Token);
                    for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(KeyConsumerColumn("p"), budget.Token);
                    if (parameters != 0) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    foreach (var column in columns) await channel.WritePacketAsync(column, budget.Token);
                    if (columns.Length != 0) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                }
                for (byte id = 71; id < 75; id += 2)
                {
                    var request = await Read(); Assert.Equal(22, request[0]); Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    await Prepare(id, 0, original);
                    if (!keyInfo) continue;
                    var directory = await Read(); Assert.Equal(22, directory[0]);
                    Assert.Contains(mysql ? "INFORMATION_SCHEMA.STATISTICS" : "ALL_INDEXES", Encoding.UTF8.GetString(directory, 1, directory.Length - 1));
                    await Prepare(92, 2);
                    Assert.Equal(PreparedParameters.Execute(92, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = "Tenant" }, new OceanBaseParameter { Value = "ledger" } }, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await Read());
                    await channel.WritePacketAsync(new byte[] { 8 }, budget.Token);
                    for (var i = 0; i < 8; i++) await channel.WritePacketAsync(KeyConsumerColumn("k" + i), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    using var row = new MemoryStream(); row.Write(new byte[] { 0, 0, 0 });
                    foreach (var value in new[] { "Tenant", "ledger", "PK", "P", "id", "1", "NO", "YES" })
                    { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
                    await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    Assert.Equal(new byte[] { 25, 92, 0, 0, 0 }, await Read());
                    var rewritten = await Read(); Assert.Equal(22, rewritten[0]); Assert.Contains("__ob_key_0", Encoding.UTF8.GetString(rewritten, 1, rewritten.Length - 1));
                    await Prepare((byte)(id + 1), 0, original, hidden);
                    if (!schemaOnly)
                    {
                        Assert.Equal(PreparedParameters.Execute((uint)(id + 1), Array.Empty<byte>()), await Read());
                        await channel.WritePacketAsync(new byte[] { 2 }, budget.Token); await channel.WritePacketAsync(original, budget.Token); await channel.WritePacketAsync(hidden, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 1, 120, id, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    }
                    // Source reader closure destroys the unpooled session, including its last pending statement.
                    if (id == 71 || !dataSource) Assert.Equal(new byte[] { 25, (byte)(id + 1), 0, 0, 0 }, await Read());
                }
                if (!dataSource)
                    for (byte id = 71; id < 75; id += 2) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await Read());
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("batch-metadata-" + target, isCollectible: true);
            Exception? clientError = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                using var connection = factory.CreateConnection()!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await using var source = factory.CreateDataSource(connection.ConnectionString);
                if (!dataSource) { if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open(); }
                using (var batch = dataSource ? source.CreateBatch() : connection.CreateBatch())
                {
                    for (var i = 0; i < 2; i++) { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
                    using (var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior | CommandBehavior.SequentialAccess, budget.Token) : batch.ExecuteReader(behavior | CommandBehavior.SequentialAccess))
                    {
                        for (var result = 0; result < 2; result++)
                        {
                            Assert.Equal(keyInfo ? 2 : 1, reader.FieldCount); Assert.Equal(1, reader.VisibleFieldCount);
                            var columns = reader.GetColumnSchema(); Assert.Equal(keyInfo ? 2 : 1, columns.Count);
                            if (keyInfo) { Assert.True(columns[1].IsKey); Assert.True(columns[1].IsHidden); using var table = reader.GetSchemaTable(); Assert.Equal(true, table!.Rows[1]["IsHidden"]); }
                            if (!schemaOnly) { Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal("x", reader.GetString(0)); Assert.Equal(71 + result * 2, reader.GetInt64(1)); }
                            Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                            Assert.Equal(result == 0, asynchronous ? await reader.NextResultAsync(budget.Token) : reader.NextResult());
                        }
                        Assert.Equal(-1, reader.RecordsAffected);
                    }
                    foreach (DbBatchCommand item in batch.BatchCommands) Assert.Equal(-1, item.RecordsAffected);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) { clientError = error; throw; }
            finally
            {
                budget.Cancel(); listener.Stop();
                try { await serving; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                catch (Exception serverError) { if (clientError != null) throw new AggregateException(clientError, serverError); throw; }
                finally { context.Unload(); }
            }
        }
    }
}
