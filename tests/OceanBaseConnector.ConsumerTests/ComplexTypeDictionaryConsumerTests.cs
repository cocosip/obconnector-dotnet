using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        public static IEnumerable<object[]> ComplexDictionaryAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var async in new[] { false, true })
                    foreach (var ob20 in new[] { false, true }) yield return new object[] { target, async, ob20 };
        }

        [Theory]
        [MemberData(nameof(ComplexDictionaryAssets))]
        public async Task EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValues(string target, bool async, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true);
                byte[] Column(string name)
                {
                    var bytes = Encoding.UTF8.GetBytes(name);
                    return new byte[] { 3, 100, 101, 102, 0, 0, 0, (byte)bytes.Length }.Concat(bytes).Concat(new[] { (byte)bytes.Length }).Concat(bytes)
                        .Concat(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }).ToArray();
                }
                byte[] Row(params string?[] values)
                {
                    using var memory = new MemoryStream(); memory.WriteByte(0); var nulls = new byte[(values.Length + 9) / 8];
                    for (var i = 0; i < values.Length; i++) if (values[i] == null) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                    memory.Write(nulls);
                    foreach (var value in values) if (value != null) { var bytes = Encoding.UTF8.GetBytes(value); memory.WriteByte((byte)bytes.Length); memory.Write(bytes); }
                    return memory.ToArray();
                }
                byte[] Result(bool members)
                {
                    var names = members ? new[] { "MEMBER_NAME", "MEMBER_POSITION", "MEMBER_SCHEMA", "MEMBER_TYPE", "MEMBER_KIND" }
                        : new[] { "TYPE_SCHEMA", "TYPE_NAME", "TYPE_KIND", "MEMBER_COUNT" };
                    var packets = new List<byte[]> { new[] { (byte)names.Length } }; packets.AddRange(names.Select(Column));
                    if (members) { packets.Add(Row("ID", "1", null, "NUMBER", "NUMBER")); packets.Add(Row("DATA", "2", null, "RAW", "RAW")); }
                    else packets.Add(Row("Owner目标", "Mixed_Type", "OBJECT", "2"));
                    var eof = Report(false, 0); eof[0] = 254; packets.Add(eof);
                    return packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray();
                }
                for (uint requestId = 0; requestId < 6; requestId++)
                {
                    var request = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                    var members = requestId >= 3; var id = members ? (byte)10 : (byte)9; byte[] response;
                    if (requestId % 3 == 0)
                    {
                        Assert.Equal(22, request[0]); var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                        Assert.Contains(members ? "ALL_TYPE_ATTRS" : "ALL_TYPES", sql); Assert.DoesNotContain("Owner目标", sql); Assert.DoesNotContain("Mixed_Type", sql);
                        response = Frame(1, new byte[] { 0, id, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 })
                            .Concat(Frame(2, Column("p"))).Concat(Frame(3, Column("p"))).ToArray();
                    }
                    else if (requestId % 3 == 1)
                    {
                        Assert.Equal(23, request[0]); Assert.Equal(id, request[1]);
                        var cursor = new ByteCursor(request); cursor.Skip(16);
                        Assert.Equal("Owner目标", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal("Mixed_Type", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes())); Assert.False(cursor.HasRemaining);
                        response = Result(members);
                    }
                    else { Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, request); if (!ob20) channel.BeginCommand(); continue; }
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, response) : response, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("complex-dictionary-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                async Task<object?> Call(object receiver, string name, object?[] arguments)
                {
                    try
                    {
                        var result = receiver.GetType().GetMethod(name)!.Invoke(receiver, arguments);
                        if (!(result is Task task)) return result;
                        await task.WaitAsync(budget.Token); return task.GetType().GetProperty("Result")!.GetValue(task);
                    }
                    catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
                }
                var arguments = async ? new object?[] { "Owner目标", "Mixed_Type", null, budget.Token } : new object?[] { "Owner目标", "Mixed_Type", null };
                var type = (await Call(connection, async ? "GetComplexTypeAsync" : "GetComplexType", arguments))!;
                Assert.Equal("Owner目标", type.GetType().GetProperty("SchemaName")!.GetValue(type));
                Assert.Same(type, await Call(connection, async ? "GetComplexTypeAsync" : "GetComplexType", arguments));
                var bytes = new byte[] { 1, 2 }; object?[] values = { 31, bytes };
                var value = (await Call(type, "CreateValue", new object?[] { values }))!; bytes[0] = 9; values[0] = 99;
                var first = (object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!;
                Assert.Equal(31, first[0]); Assert.Equal(new byte[] { 1, 2 }, first[1]); ((byte[])first[1]!)[0] = 8;
                Assert.Equal(new byte[] { 1, 2 }, ((object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!)[1]);
                await Call(connection, "ClearComplexTypeCache", new object?[] { null });
                await Assert.ThrowsAsync<InvalidOperationException>(() => Call(type, "CreateValue", new object?[] { values }));
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
