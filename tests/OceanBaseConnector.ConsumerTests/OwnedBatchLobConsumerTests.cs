using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class OwnedBatchLobConsumerTests : Ob20TestSupport
    {
        [Theory]
        [InlineData("net8.0", false)] [InlineData("net8.0", true)]
        [InlineData("net9.0", false)] [InlineData("net9.0", true)]
        [InlineData("net10.0", false)] [InlineData("net10.0", true)]
        public async Task ModernAssetsRetainOwnedBatchScalarLobsUntilDisposal(string target, bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var locator = new byte[41];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x4c4f4221);
            locator[4] = 1; locator[28] = 1; locator[36] = 1; locator[40] = 7;
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                peer.NoDelay = true; var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, peer.GetStream(), "unrequested", budget.Token, lobLocators: true);
                async Task Read(byte code)
                {
                    channel.BeginCommand(); Assert.Equal(code, (await channel.ReadPacketAsync(budget.Token))[0]);
                }
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 63, 0, 20, 0, 0, 0, 210, 0, 0, 0, 0, 0 };
                var eof = new byte[] { 254, 0, 0, 2, 0 };
                await Read(22);
                await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(column, budget.Token); await channel.WritePacketAsync(eof, budget.Token);
                await Read(23);
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(column, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, (byte)locator.Length }.Concat(locator).ToArray(), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                await ObserveClose(peer.GetStream(), budget.Token);
            });
            var context = new AssemblyLoadContext("owned-batch-lob-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                await using var source = factory.CreateDataSource($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute=true;Use Lob Locators=true;Use Batch Multi Send=true");
                using var batch = source.CreateBatch(); var item = batch.CreateBatchCommand();
                item.CommandText = "select lob_value"; batch.BatchCommands.Add(item);
                using var lob = Assert.IsAssignableFrom<IDisposable>(asynchronous ? await batch.ExecuteScalarAsync(budget.Token) : batch.ExecuteScalar());
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteScalarAsync(budget.Token) : Task.FromResult(batch.ExecuteScalar()));
                using var stream = (Stream)lob.GetType().GetMethod("OpenRead")!.Invoke(lob, null)!;
                Assert.Equal(7, stream.ReadByte());
                if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                Assert.Throws<InvalidOperationException>(() => stream.ReadByte());
                await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
