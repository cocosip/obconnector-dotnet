using System;
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
        public async Task EveryAssetKeyInfoAppendsHiddenKeyWithoutReplacingOriginalPreparation(string target, bool asynchronous, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var sql = mysql ? "select l.payload from `Tenant`.ledger l" : "select l.payload from \"Tenant\".\"ledger\" l";
            var original = KeyConsumerColumn("payload", 253, "payload");
            var hidden = KeyConsumerColumn("__ob_key_0", 8, "id", 1);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                async Task<byte[]> Read() { channel.BeginCommand(); return await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false); }
                async Task Prepare(byte id, ushort parameters, params byte[][] columns)
                {
                    await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, (byte)columns.Length, 0, (byte)parameters, 0, 0, 0, 0 }, budget.Token);
                    for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(KeyConsumerColumn("p"), budget.Token);
                    if (parameters != 0) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    foreach (var column in columns) await channel.WritePacketAsync(column, budget.Token);
                    if (columns.Length != 0) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                }
                var prepare = await Read(); Assert.Equal(22, prepare[0]); Assert.Equal(sql, Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepare(91, 0, original);
                for (var round = 0; round < 2; round++)
                {
                    if (round != 0) Assert.Equal(new byte[] { 25, 93, 0, 0, 0 }, await Read());
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
                    Assert.Equal(22, (await Read())[0]); await Prepare(93, 0, original, hidden);
                    if (round == 0) continue;
                    Assert.Equal(PreparedParameters.Execute(93, Array.Empty<byte>()), await Read());
                    await channel.WritePacketAsync(new byte[] { 2 }, budget.Token); await channel.WritePacketAsync(original, budget.Token); await channel.WritePacketAsync(hidden, budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    await channel.WritePacketAsync(new byte[] { 0, 0, 1, 120, 7, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                }
                Assert.Equal(new byte[] { 25, 93, 0, 0, 0 }, await Read()); Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await Read());
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("key-info-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // Internal dual-mode fixture, not a real MySQL-mode server.
                    var session = type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    for (var round = 0; round < 2; round++)
                    {
                        var behavior = CommandBehavior.KeyInfo | CommandBehavior.SequentialAccess | (round == 0 ? CommandBehavior.SchemaOnly : CommandBehavior.Default);
                        using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior, budget.Token) : command.ExecuteReader(behavior);
                        Assert.Equal(2, reader.FieldCount); Assert.Equal(1, reader.VisibleFieldCount);
                        var columns = reader.GetColumnSchema(); Assert.False(columns[0].IsKey); Assert.False(columns[0].IsHidden); Assert.True(columns[1].IsKey); Assert.True(columns[1].IsHidden); Assert.True(columns[1].IsUnique);
                        using var table = reader.GetSchemaTable(); Assert.Equal(true, table!.Rows[1]["IsHidden"]);
                        if (round != 0) { Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal("x", reader.GetString(0)); Assert.Equal(7, reader.GetInt64(1)); }
                        Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    }
                    if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare();
                    Assert.Equal(sql, command.CommandText);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static byte[] KeyConsumerColumn(string name, byte type = 253, string source = "", ushort flags = 0)
        {
            using var column = new MemoryStream();
            foreach (var field in new[] { "def", source.Length == 0 ? "" : "Tenant", source.Length == 0 ? "" : "l", source.Length == 0 ? "" : "ledger", name, source })
            { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
            column.Write(new byte[] { 12, 45, 0, 80, 0, 0, 0, type, (byte)flags, (byte)(flags >> 8), 0, 0, 0 }); return column.ToArray();
        }
    }
}
