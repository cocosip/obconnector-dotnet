using System;
using System.Data;
using System.Data.Common;
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
    public sealed partial class Ob20ConsumerTests
    {
        [Theory]
        [InlineData("net8.0", false)] [InlineData("net8.0", true)]
        [InlineData("net9.0", false)] [InlineData("net9.0", true)]
        [InlineData("net10.0", false)] [InlineData("net10.0", true)]
        public async Task ModernAssetsRetainOwnedRoutineBarrierCursors(string target, bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); peer.NoDelay = true;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, peer.GetStream(), "unrequested", budget.Token, deprecated: true, version: "4.4.2.2");
                async Task<byte[]> Read(byte code)
                { channel.BeginCommand(); var packet = await channel.ReadPacketAsync(budget.Token); Assert.Equal(code, packet[0]); return packet; }
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                await Read(0xa1);
                await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 4, 0, 0, 0, 1 }, budget.Token);
                await channel.WritePacketAsync(Column(163), budget.Token); await channel.WritePacketAsync(Column(163), budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 51, 0, 0, 0 }, budget.Token);
                var eof = Report(false, 0); eof[0] = 254;
                await channel.WritePacketAsync(eof, budget.Token); await channel.WritePacketAsync(Report(false, 0), budget.Token);
                for (byte id = 90; id < 92; id++)
                {
                    await Read(22); await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                }
                Assert.Equal(90, (await Read(23))[1]); Assert.Equal(91, (await Read(23))[1]);
                for (var i = 0; i < 2; i++) { channel.BeginResponse(); await channel.WritePacketAsync(Report(false, 1), budget.Token); }
                Assert.Equal(51, (await Read(28))[1]);
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(Column(8), budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(7L)).ToArray(), budget.Token);
                var last = Report(false, 0); last[0] = 254; last[3] |= 128;
                await channel.WritePacketAsync(last, budget.Token);
                Assert.Equal(51, (await Read(25))[1]); Assert.Equal(42, (await Read(25))[1]);
                for (byte id = 90; id < 92; id++) Assert.Equal(id, (await Read(25))[1]);
                await ObserveClose(peer.GetStream(), budget.Token);
            });
            var context = new AssemblyLoadContext("batch-routine-cursor-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                await using var source = factory.CreateDataSource($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
                using var batch = source.CreateBatch();
                var routine = batch.CreateBatchCommand(); routine.CommandText = "pkg.open_cursor"; routine.CommandType = CommandType.StoredProcedure;
                var output = factory.CreateParameter()!; output.ParameterName = "c"; output.Direction = ParameterDirection.Output;
                output.GetType().GetProperty("OceanBaseDbType")!.SetValue(output, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "RefCursor"));
                routine.Parameters.Add(output); batch.BatchCommands.Add(routine);
                foreach (var sql in new[] { "update t set id=1", "update t set id=2" })
                { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
                Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync(budget.Token) : batch.ExecuteNonQuery());
                using var cursor = Assert.IsAssignableFrom<IDisposable>(output.Value);
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteScalarAsync(budget.Token) : Task.FromResult(batch.ExecuteScalar()));
                DbDataReader reader;
                if (asynchronous)
                {
                    var method = cursor.GetType().GetMethods().Single(candidate => candidate.Name == "GetDataReaderAsync" && candidate.GetParameters().Length == 3);
                    var opening = (Task)method.Invoke(cursor, new object[] { 2, CommandBehavior.Default, budget.Token })!;
                    await opening; reader = (DbDataReader)opening.GetType().GetProperty("Result")!.GetValue(opening)!;
                }
                else
                {
                    var method = cursor.GetType().GetMethods().Single(candidate => candidate.Name == "GetDataReader" && candidate.GetParameters().Length == 2);
                    reader = (DbDataReader)method.Invoke(cursor, new object[] { 2, CommandBehavior.Default })!;
                }
                using (reader)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(7L, reader.GetInt64(0));
                    Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.False(reader.NextResult());
                }
                cursor.Dispose(); if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
