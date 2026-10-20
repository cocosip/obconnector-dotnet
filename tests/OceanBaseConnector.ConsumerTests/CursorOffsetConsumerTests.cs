using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
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
        public static IEnumerable<object[]> CursorOffsetAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var outcome in new[] { "success", "error", "missing", "sequence", "boundary" })
                            yield return new object[] { target, asynchronous, ob20, outcome };
        }

        [Theory]
        [MemberData(nameof(CursorOffsetAssets))]
        public async Task EveryAssetConsumesOffsetFetchTailsBeforeReleasingTheConnection(string target, bool asynchronous, bool ob20, string outcome)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, type, 0, 0, 0, 0, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true);
                var version = await ReadRequest(stream, channel, ob20, 0, budget.Token);
                Assert.Equal(3, version[0]); Assert.Equal("select ob_version() from dual", Encoding.UTF8.GetString(version, 1, version.Length - 1));
                var versionEnd = Report(false, 0); versionEnd[0] = 254;
                var response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(253)))
                    .Concat(Frame(3, new byte[] { 7, 50, 46, 50, 46, 53, 46, 48 })).Concat(Frame(4, versionEnd)).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 0, 1, response) : response, budget.Token);
                if (!ob20) channel.BeginCommand();
                Assert.Equal(22, (await ReadRequest(stream, channel, ob20, 1, budget.Token))[0]);
                response = Frame(1, new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }).Concat(Frame(2, Column(8))).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 1, 1, response) : response, budget.Token);
                if (!ob20) channel.BeginCommand();
                Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 1, 1, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 2, budget.Token));
                response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(8))).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 2, 1, response) : response, budget.Token);
                for (uint page = 0; page < 2; page++)
                {
                    if (!ob20) channel.BeginCommand();
                    Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, page + 3, budget.Token));
                    var row = new byte[10]; row[2] = (byte)(page + 42);
                    response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(8))).Concat(Frame(3, row))
                        .Concat(Frame(4, new byte[] { 254, 0, 0, page == 0 ? (byte)78 : (byte)142, 0, 0, 0 })).ToArray();
                    if (page == 1 && outcome == "missing")
                    {
                        await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, page + 3, 1, response) : response, budget.Token); return;
                    }
                    var tail = page == 1 && outcome == "error" ? new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48 }
                        : new byte[] { 0, (byte)(page + 1), 0, 6, 0, 0, 0 };
                    if (page == 1 && outcome == "boundary" && !ob20) tail[3] |= 8;
                    response = response.Concat(Frame(page == 1 && outcome == "sequence" ? (byte)9 : (byte)5, tail)).ToArray();
                    if (page == 1 && outcome == "boundary" && ob20) response = response.Concat(Frame(6, new byte[] { 0 })).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, page + 3, 1, response) : response, budget.Token);
                }
                if (outcome == "success")
                {
                    if (!ob20) channel.BeginCommand();
                    Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 5, budget.Token));
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("offset-fetch-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select n"; command.GetType().GetProperty("FetchSize")!.SetValue(command, 1);
                    var option = command.GetType().GetProperty("UseCursorOffset"); Assert.NotNull(option); Assert.Equal(false, option.GetValue(command)); option.SetValue(command, true);
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token) : command.ExecuteReader(CommandBehavior.Default);
                    for (var value = 42; value < 44; value++)
                    { Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(value, reader.GetInt64(0)); }
                    async Task Complete() { Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); }
                    if (outcome == "success") { await Complete(); Assert.Equal(-1, reader.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State); }
                    else
                    {
                        var error = await Assert.ThrowsAnyAsync<DbException>(Complete);
                        Assert.Equal(outcome != "error", error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                        Assert.Equal(ConnectionState.Broken, connection.State);
                        Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                    }
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
