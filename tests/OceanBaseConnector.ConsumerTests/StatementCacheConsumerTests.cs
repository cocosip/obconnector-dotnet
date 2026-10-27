using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class StatementCacheConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true }) yield return new object[] { target, asynchronous, oracle };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReusesThenInvalidatesPreparedStatements(string target, bool asynchronous, bool oracle)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
                await channel.WritePacketAsync(fixture.RootElement.GetProperty("payloadBase64").GetBytesFromBase64(), budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                var ok = (byte[])SessionTimeZoneFixture.Report.Clone(); ok[3] = (byte)(oracle ? 6 : 2);
                await channel.WritePacketAsync(ok, budget.Token);
                await ServerIdentity.ReplyAsync(channel, oracle: oracle, token: budget.Token);
                async Task<byte[]> Read() { channel.BeginCommand(); return await channel.ReadPacketAsync(budget.Token); }
                for (byte id = 71; id < 73; id++)
                {
                    if (id == 72) Assert.Equal(new byte[] { 25, 71, 0, 0, 0 }, await Read());
                    Assert.Equal(OceanBaseCommand.SqlPacket("UPDATE t SET n=1", 22), await Read());
                    await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    for (var round = 0; round < (id == 71 ? 2 : 1); round++)
                    {
                        Assert.Equal(new byte[] { 23, id, 0, 0, 0, 0, 1, 0, 0, 0 }, await Read());
                        await channel.WritePacketAsync(ok, budget.Token);
                    }
                    if (id == 71)
                    {
                        Assert.Equal(OceanBaseCommand.SqlPacket("ALTER TABLE t ADD m INT", 3), await Read());
                        await channel.WritePacketAsync(ok, budget.Token);
                    }
                }
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], budget.Token));
            });
            var context = new AssemblyLoadContext("statement-cache-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var builder = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnectionStringBuilder")!)!;
                var size = builder.GetType().GetProperty("StatementCacheSize")!; Assert.Equal(0, size.GetValue(builder));
                size.SetValue(builder, 1); Assert.Equal(1, size.GetValue(builder));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Statement Cache Size=1";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                for (var i = 0; i < 3; i++)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE t SET n=1";
                        if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare();
                        Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    }
                    if (i == 1)
                    {
                        using var ddl = connection.CreateCommand(); ddl.CommandText = "ALTER TABLE t ADD m INT";
                        Assert.Equal(0, asynchronous ? await ddl.ExecuteNonQueryAsync(budget.Token) : ddl.ExecuteNonQuery());
                    }
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
