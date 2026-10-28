using System;
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

        private static async Task EveryAssetMutatesLobLocatorsAndKeepsWriteOutcomeAtOb20BoundaryCore(string target, bool async, string kind, bool character, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard"; var data = Encoding.UTF8.GetBytes("患者😀a"); var type = character ? (byte)211 : (byte)210;
            var trimmed = character ? Encoding.UTF8.GetBytes("患者😀") : data.Take(5).ToArray();
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
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0", lobLocators: true, tls: tls, certificate: certificate);
                byte[] Column(byte code) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, code, 0, 0, 0, 0, 0 };
                byte[] Bytes(byte[] value) => new[] { (byte)value.Length }.Concat(value).ToArray();
                async Task Reply(uint id, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id + 2, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var query = await ReadRequest(stream, channel, ob20, 2, budget.Token); Assert.Equal("select lob_value", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                await Reply(0, Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(type))).Concat(Frame(3, Bytes(Locator(Array.Empty<byte>())))).Concat(Frame(4, eof)).ToArray());
                for (var trim = 0; trim < 2; trim++)
                {
                    var requestId = (uint)(1 + trim * 2); var parameters = trim == 0 ? 4 : 2;
                    var request = new ByteCursor(await ReadRequest(stream, channel, ob20, requestId + 2, budget.Token)); Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                    Assert.Equal(trim == 0 ? "BEGIN DBMS_LOB.WRITE(?, ?, ?, ?); END;" : "BEGIN DBMS_LOB.TRIM(?, ?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                    Assert.Equal((uint)parameters, request.ReadUInt32()); Assert.Equal(0, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8()); Assert.Equal((ushort)type, request.ReadUInt16());
                    if (trim == 0)
                    { Assert.Equal((ushort)3, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal((ushort)253, request.ReadUInt16()); }
                    else Assert.Equal((ushort)8, request.ReadUInt16());
                    Assert.Equal(Locator(trim == 0 ? Array.Empty<byte>() : data), request.ReadLengthEncodedBytes());
                    if (trim == 0)
                    { Assert.Equal(character ? 4u : (uint)data.Length, request.ReadUInt32()); Assert.Equal(1ul, request.ReadUInt64()); var buffer = request.ReadLengthEncodedBytes(); Assert.Equal(data, character ? buffer : Convert.FromHexString(Encoding.ASCII.GetString(buffer))); }
                    else Assert.Equal(character ? 3ul : 5ul, request.ReadUInt64());
                    var response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 1, 0, (byte)parameters, 0, 0, 0, 0, 4, 0, 0, 0, 1 });
                    for (var i = 0; i < parameters; i++) response = response.Concat(Frame((byte)(2 + i), Column(253))).ToArray();
                    response = response.Concat(Frame((byte)(2 + parameters), Column(type)))
                        .Concat(Frame((byte)(3 + parameters), new byte[] { 0, 0 }.Concat(Bytes(Locator(trim == 0 ? data : trimmed))).ToArray()))
                        .Concat(Frame((byte)(4 + parameters), eof)).Concat(Frame((byte)(5 + parameters), Report(false, 0))).ToArray();
                    if (kind == "trailing") response = response.Concat(Frame((byte)(6 + parameters), new byte[] { 0 })).ToArray();
                    await Reply(requestId, response);
                    if (kind == "trailing") break;
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, requestId + 3, budget.Token));
                    if (!ob20) channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("lob-mutation-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use Lob Locators=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = Assert.IsAssignableFrom<IDisposable>(async ? await command.ExecuteScalarAsync(budget.Token) : command.ExecuteScalar());
                async Task Call(string name, params object[] arguments)
                {
                    try { var result = lob.GetType().GetMethod(name)!.Invoke(lob, arguments); if (result is Task task) await task; }
                    catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); }
                }
                Task Write() => character ? Call(async ? "WriteTextAsync" : "WriteText", async ? new object[] { 0L, "患者😀a", budget.Token } : new object[] { 0L, "患者😀a" })
                    : Call(async ? "WriteBytesAsync" : "WriteBytes", async ? new object[] { 0L, data, 0, data.Length, budget.Token } : new object[] { 0L, data, 0, data.Length });
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Write); Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(0L, lob.GetType().GetProperty("ByteLength")!.GetValue(lob)); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    await Write(); Assert.Equal((long)data.Length, lob.GetType().GetProperty("ByteLength")!.GetValue(lob));
                    var length = character ? 3L : 5L;
                    await Call(async ? "TruncateAsync" : "Truncate", async ? new object[] { length, budget.Token } : new object[] { length });
                    Assert.Equal((long)trimmed.Length, lob.GetType().GetProperty("ByteLength")!.GetValue(lob));
                    if (character) { using var reader = (TextReader)lob.GetType().GetMethod("OpenTextReader")!.Invoke(lob, null)!; Assert.Equal("患者😀", reader.ReadToEnd()); }
                    else { using var source = (Stream)lob.GetType().GetMethod("OpenRead")!.Invoke(lob, null)!; using var destination = new MemoryStream(); source.CopyTo(destination); Assert.Equal(trimmed, destination.ToArray()); }
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
