using System;
using System.Data;
using System.Data.Common;
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
        public async Task EveryAssetSchemaOnlyPreparesWithoutExecutionOrFallback(string target, bool asynchronous, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                async Task<byte[]> Read() { channel.BeginCommand(); return await channel.ReadPacketAsync(budget.Token); }
                var prepare = await Read(); Assert.Equal(22, prepare[0]);
                Assert.Equal("select ?", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 81, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0 }, budget.Token);
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 8, 0, 0, 0, 0, 0 };
                await channel.WritePacketAsync(column, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                await channel.WritePacketAsync(column, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                // Repeated SchemaOnly must reuse metadata; any EXECUTE/FETCH/QUERY fails this assertion.
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await Read());
                Assert.Equal(22, (await Read())[0]);
                await channel.WritePacketAsync(new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48 }, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("schema-only-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // Internal mode fixture; no real MySQL-mode server participates.
                    var session = type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = mysql ? "select ?" : "select :p";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = Guid.NewGuid(); command.Parameters.Add(parameter);
                    command.GetType().GetProperty("FetchSize")!.SetValue(command, 19);
                    command.GetType().GetProperty("AllowClientSidePrepareFallback")!.SetValue(command, true);
                    for (var i = 0; i < 2; i++)
                    {
                        using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, budget.Token) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                        Assert.Equal("n", reader.GetName(0)); Assert.Equal(typeof(long), reader.GetFieldType(0));
                        Assert.Single(reader.GetColumnSchema()); Assert.False(reader.HasRows);
                        Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    }
                }
                using (var unsupported = connection.CreateCommand())
                {
                    unsupported.CommandText = "show unsupported";
                    unsupported.GetType().GetProperty("AllowClientSidePrepareFallback")!.SetValue(unsupported, true);
                    var error = await Record.ExceptionAsync(async () =>
                    {
                        using var reader = asynchronous ? await unsupported.ExecuteReaderAsync(CommandBehavior.SchemaOnly, budget.Token) : unsupported.ExecuteReader(CommandBehavior.SchemaOnly);
                    });
                    Assert.NotNull(error); Assert.Equal(1295, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error));
                    Assert.Equal(false, error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
