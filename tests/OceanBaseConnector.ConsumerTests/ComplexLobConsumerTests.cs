using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
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
        public static IEnumerable<object[]> ComplexLobAssets() => LobAssets().Where(row => (string)row[2] != "trailing");

        [Theory]
        [MemberData(nameof(ComplexLobAssets))]
        public async Task EveryAssetBindsLobArraysAndRechecksDisposedHandles(string target, bool asynchronous, string kind, bool character)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind == "ob20"; var code = character ? (byte)211 : (byte)210;
            var locator = new byte[92]; new byte[] { 1, 132, 1, 0, 2, 1, 0, 0, 1, 129, 0, 0 }.CopyTo(locator, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(locator.AsSpan(28), 90000);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0", lobLocators: true);
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                async Task Reply(uint requestId, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var source = await ReadRequest(stream, channel, ob20, 2, budget.Token);
                Assert.Equal("select lob_value", Encoding.UTF8.GetString(source, 1, source.Length - 1));
                await Reply(2, Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(code)))
                    .Concat(Frame(3, new[] { (byte)locator.Length }.Concat(locator).ToArray())).Concat(Frame(4, eof)).ToArray());
                var request = new ByteCursor(await ReadRequest(stream, channel, ob20, 3, budget.Token));
                Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                Assert.Equal("BEGIN pkg.p(?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                Assert.Equal(1u, request.ReadUInt32());
                var expected = new byte[] { 0, 1, 160, 0, 0, 0, 0, code, 2, 2, (byte)locator.Length }.Concat(locator).ToArray();
                Assert.Equal(expected, request.ReadBytes(expected.Length));
                await Reply(3, Frame(1, new byte[] { 0, 42, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
                    .Concat(Frame(2, Column(253))).Concat(Frame(3, Report(false, 1))).ToArray());
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 4, budget.Token));
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("complex-lob-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute=true;Use Lob Locators=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var lob = Assert.IsAssignableFrom<IDisposable>(asynchronous ? await source.ExecuteScalarAsync(budget.Token) : source.ExecuteScalar());
                var factory = connectionType.GetMethod("CreateArrayType", new[] { typeof(string), assembly.GetType("OceanBaseConnector.OceanBaseTransaction")! })!;
                var type = factory.Invoke(connection, new object?[] { character ? "CLOB" : "BLOB", null })!;
                var create = type.GetType().GetMethod("CreateValue")!;
                var value = create.Invoke(type, new object[] { new object?[] { lob, null } });
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = value; command.Parameters.Add(parameter);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                Assert.NotNull(create.Invoke(type, new object[] { new object?[] { lob } }));
                lob.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => asynchronous ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(ConnectionState.Open, connection.State); command.Dispose(); connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
