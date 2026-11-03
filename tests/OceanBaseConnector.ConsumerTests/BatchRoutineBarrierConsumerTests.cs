using System;
using System.Data.Common;
using System.IO;
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
    public sealed class BatchRoutineBarrierConsumerTests
    {
        [Theory]
        [InlineData("net8.0", false)] [InlineData("net8.0", true)]
        [InlineData("net9.0", false)] [InlineData("net9.0", true)]
        [InlineData("net10.0", false)] [InlineData("net10.0", true)]
        public async Task ModernAssetsKeepPipelinedSegmentsAroundRoutineBarriers(string target, bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token);
                async Task<byte[]> Read(byte code)
                {
                    channel.BeginCommand(); var packet = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false); Assert.Equal(code, packet[0]); return packet;
                }
                for (byte segment = 0; segment < 2; segment++)
                {
                    for (byte i = 0; i < 2; i++)
                    {
                        var prepare = await Read(22);
                        Assert.Equal("update t set id=" + (segment * 2 + i + 1), Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                        await channel.WritePacketAsync(new byte[] { 0, (byte)(90 + segment * 2 + i), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    }
                    Assert.Equal(90 + segment * 2, (await Read(23))[1]); Assert.Equal(91 + segment * 2, (await Read(23))[1]);
                    for (var i = 0; i < 2; i++) { channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token); }
                    if (segment == 0)
                    {
                        var block = await Read(3); Assert.Equal("BEGIN NULL; END;", Encoding.UTF8.GetString(block, 1, block.Length - 1));
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, budget.Token);
                    }
                }
                for (byte id = 90; id < 94; id++) Assert.Equal(id, (await Read(25))[1]);
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], budget.Token));
            });
            var context = new AssemblyLoadContext("batch-routine-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                using var connection = factory.CreateConnection()!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=secret;TLS Mode=Disabled;Use Batch Multi Send=true;Batch Multi Send Number=2";
                await connection.OpenAsync(budget.Token);
                using (var batch = connection.CreateBatch())
                {
                    foreach (var sql in new[] { "update t set id=1", "update t set id=2", "BEGIN NULL; END;", "update t set id=3", "update t set id=4" })
                    { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
                    Assert.Equal(4, asynchronous ? await batch.ExecuteNonQueryAsync(budget.Token) : batch.ExecuteNonQuery());
                    for (var i = 0; i < 5; i++) Assert.Equal(i == 2 ? 0 : 1, batch.BatchCommands[i].RecordsAffected);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
