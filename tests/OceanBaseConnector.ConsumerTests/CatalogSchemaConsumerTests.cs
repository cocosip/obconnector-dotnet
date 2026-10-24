using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> CatalogAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { false } : new[] { false, true })
                    foreach (var mysql in new[] { false, true }) yield return new object[] { target, asynchronous, mysql };
        }

        [Theory]
        [MemberData(nameof(CatalogAssets))]
        public async Task CatalogEveryAssetExposesModeAwareDirectories(string target, bool asynchronous, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var collections = CatalogSamples.Names.Where(x => !mysql || x != "Types" && x != "Synonyms").ToArray();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                foreach (var name in collections)
                {
                    var request = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false); Assert.Equal(3, request[0]);
                    Assert.Contains(CatalogSamples.Source(name, mysql), Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    var (names, values) = CatalogSamples.Row(name, mysql);
                    await channel.WritePacketAsync(new[] { (byte)names.Length }, budget.Token);
                    foreach (var columnName in names)
                    {
                        using var column = new MemoryStream();
                        foreach (var field in new[] { "def", "", "", "", columnName, columnName })
                        { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
                        column.Write(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                        await channel.WritePacketAsync(column.ToArray(), budget.Token);
                    }
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                    using var row = new MemoryStream();
                    foreach (var value in values)
                    {
                        if (value == null) { row.WriteByte(251); continue; }
                        var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes);
                    }
                    await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                    channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("catalog-schema-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // Authenticated-mode fixture, matching existing routine consumer tests; not real server evidence.
                    var session = type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using var meta = connection.GetSchema(); using var restrictions = connection.GetSchema("Restrictions");
                Assert.Equal(mysql ? 17 : 19, meta.Rows.Count); Assert.True(restrictions.Rows.Count > 0);
                foreach (var collection in collections)
                {
                    using var table = asynchronous ? await connection.GetSchemaAsync(collection, budget.Token) : connection.GetSchema(collection);
                    CatalogSamples.AssertRow(table, collection, mysql);
                }
                Assert.Equal(ConnectionState.Open, connection.State); connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
