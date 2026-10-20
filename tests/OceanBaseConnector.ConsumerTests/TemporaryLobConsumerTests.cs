using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> TemporaryLobAssets()
        {
            foreach (var args in LobAssets())
            {
                if ((string)args[2] != "trailing") { yield return args; continue; }
                args[2] = "create-trailing"; yield return args;
                var free = (object[])args.Clone(); free[2] = "free-trailing"; yield return free;
            }
        }

        [Theory]
        [MemberData(nameof(TemporaryLobAssets))]
        public Task EveryAssetCreatesMutatesAndExplicitlyFreesTemporaryLobs(string target, bool async, string kind, bool character)
            => EveryAssetCreatesMutatesAndExplicitlyFreesTemporaryLobsCore(target, async, kind, character, encrypted: false);

        [Theory]
        [MemberData(nameof(TemporaryLobAssets))]
        public Task EveryAssetCreatesMutatesAndExplicitlyFreesTemporaryLobsOverTls(string target, bool async, string kind, bool character)
            => EveryAssetCreatesMutatesAndExplicitlyFreesTemporaryLobsCore(target, async, kind, character, encrypted: true);

        private static async Task EveryAssetCreatesMutatesAndExplicitlyFreesTemporaryLobsCore(string target, bool async, string kind, bool character, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard"; var type = character ? (byte)211 : (byte)210; var data = Encoding.UTF8.GetBytes("患者😀a");
            byte[] Locator(byte[] bytes)
            {
                var result = new byte[40 + bytes.Length]; new byte[] { 33, 66, 79, 76, 1 }.CopyTo(result, 0); result[28] = 1;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36), (uint)bytes.Length); bytes.CopyTo(result, 40); return result;
            }
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, lobLocators: true, tls: tls, certificate: certificate);
                byte[] Column(byte code) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, code, 0, 0, 0, 0, 0 };
                byte[] Bytes(byte[] value) => new[] { (byte)value.Length }.Concat(value).ToArray();
                async Task Reply(uint id, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var version = await ReadRequest(stream, channel, ob20, 0, budget.Token); Assert.Equal("select ob_version() from dual", Encoding.UTF8.GetString(version, 1, version.Length - 1));
                await Reply(0, Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(253))).Concat(Frame(3, Bytes(Encoding.ASCII.GetBytes("4.3.5.0")))).Concat(Frame(4, eof)).ToArray());
                for (var operation = 0; operation < 3; operation++)
                {
                    var id = (uint)(1 + operation * 2); var parameters = operation == 0 ? 1 : operation == 1 ? 4 : 2;
                    var request = new ByteCursor(await ReadRequest(stream, channel, ob20, id, budget.Token)); Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                    var expected = operation == 0 ? "DECLARE lob_value " + (character ? "CLOB" : "BLOB") + "; BEGIN DBMS_LOB.CREATETEMPORARY(lob_value, FALSE, DBMS_LOB.SESSION); ? := lob_value; END;"
                        : operation == 1 ? "BEGIN DBMS_LOB.WRITE(?, ?, ?, ?); END;" : "DECLARE lob_value " + (character ? "CLOB" : "BLOB") + " := ?; BEGIN DBMS_LOB.FREETEMPORARY(lob_value); ? := '1'; END;";
                    Assert.Equal(expected, Encoding.UTF8.GetString(request.ReadLengthEncodedBytes())); Assert.Equal((uint)parameters, request.ReadUInt32());
                    Assert.Equal(operation == 0 ? 1 : operation == 1 ? 0 : 2, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                    if (operation == 0) Assert.Equal((ushort)6, request.ReadUInt16());
                    else
                    {
                        Assert.Equal((ushort)type, request.ReadUInt16());
                        if (operation == 1)
                        { Assert.Equal((ushort)3, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal(character ? (ushort)253 : (ushort)203, request.ReadUInt16()); }
                        else Assert.Equal((ushort)6, request.ReadUInt16());
                        Assert.Equal(Locator(operation == 1 ? Array.Empty<byte>() : data), request.ReadLengthEncodedBytes());
                        if (operation == 1)
                        { Assert.Equal(character ? 4u : (uint)data.Length, request.ReadUInt32()); Assert.Equal(1ul, request.ReadUInt64()); Assert.Equal(data, request.ReadLengthEncodedBytes()); }
                    }
                    request.Skip(16); Assert.False(request.HasRemaining);
                    var response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 1, 0, (byte)parameters, 0, 0, 0, 0, 4, 0, 0, 0, 1 });
                    for (var i = 0; i < parameters; i++) response = response.Concat(Frame((byte)(2 + i), Column(253))).ToArray();
                    response = response.Concat(Frame((byte)(2 + parameters), Column(operation == 2 ? (byte)253 : type)))
                        .Concat(Frame((byte)(3 + parameters), new byte[] { 0, 0 }.Concat(Bytes(operation == 2 ? Encoding.ASCII.GetBytes("1") : Locator(operation == 0 ? Array.Empty<byte>() : data))).ToArray()))
                        .Concat(Frame((byte)(4 + parameters), eof)).Concat(Frame((byte)(5 + parameters), Report(false, 0))).ToArray();
                    var trailing = kind == "create-trailing" && operation == 0 || kind == "free-trailing" && operation == 2;
                    if (trailing) response = response.Concat(Frame((byte)(6 + parameters), new byte[] { 0 })).ToArray();
                    await Reply(id, response); if (trailing) break;
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, id + 1, budget.Token));
                    if (!ob20) channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("temporary-lob-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use Lob Locators=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                async Task<object?> Call(object targetObject, string name, params object?[] arguments)
                {
                    try
                    {
                        var result = targetObject.GetType().GetMethod(name)!.Invoke(targetObject, arguments);
                        if (!(result is Task task)) return result;
                        await task.WaitAsync(budget.Token); return task.GetType().GetProperty("Result")?.GetValue(task);
                    }
                    catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
                }
                var lobType = Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, character ? "Clob" : "Blob");
                Task<object?> Create() => Call(connection, async ? "CreateTemporaryLobAsync" : "CreateTemporaryLob",
                    async ? new object?[] { lobType, null, budget.Token } : new object?[] { lobType, null });
                if (kind == "create-trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Create); Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    using var lob = Assert.IsAssignableFrom<IDisposable>(await Create());
                    if (character) await Call(lob, async ? "WriteTextAsync" : "WriteText", async ? new object[] { 0L, "患者😀a", budget.Token } : new object[] { 0L, "患者😀a" });
                    else await Call(lob, async ? "WriteBytesAsync" : "WriteBytes", async ? new object[] { 0L, data, 0, data.Length, budget.Token } : new object[] { 0L, data, 0, data.Length });
                    if (character) { using var reader = (TextReader)(await Call(lob, "OpenTextReader"))!; Assert.Equal("患者😀a", reader.ReadToEnd()); }
                    else { using var source = (Stream)(await Call(lob, "OpenRead"))!; using var destination = new MemoryStream(); source.CopyTo(destination); Assert.Equal(data, destination.ToArray()); }
                    Task<object?> Free() => Call(lob, async ? "FreeTemporaryAsync" : "FreeTemporary", async ? new object[] { budget.Token } : Array.Empty<object>());
                    if (kind == "free-trailing")
                    {
                        var error = await Assert.ThrowsAnyAsync<DbException>(Free); Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                        Assert.Equal(ConnectionState.Broken, connection.State);
                    }
                    else await Free();
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => Call(lob, "FreeTemporary"));
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
