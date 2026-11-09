using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
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
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    /// <summary>
    /// Shared fake-server helpers and consumer asset rows for the Ob20 consumer
    /// suites. The tests live in one class per scenario file so xunit can run the
    /// collections in parallel; this base holds the members they all need.
    /// </summary>
    public abstract class Ob20TestSupport
    {
        public static IEnumerable<object[]> ComplexDictionaryAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var ob20 in new[] { false, true }) yield return new object[] { target, async, ob20 };
        }

        internal static async Task EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValuesCore(string target, bool async, bool ob20, bool encrypted, bool objectScript = false, bool nestedObjectScript = false, bool collectionScript = false)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0", tls: tls, certificate: certificate);
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
                byte[] Result(bool members, bool refreshed = false, bool child = false)
                {
                    var names = members ? new[] { "MEMBER_NAME", "MEMBER_POSITION", "MEMBER_SCHEMA", "MEMBER_TYPE", "MEMBER_KIND" }
                        : new[] { "TYPE_SCHEMA", "TYPE_NAME", "TYPE_KIND", "MEMBER_COUNT" };
                    var packets = new List<byte[]> { new[] { (byte)names.Length } }; packets.AddRange(names.Select(Column));
                    if (members)
                    {
                        if (child && collectionScript) packets.Add(Row(null, "1", null, "RAW", "RAW"));
                        else
                        {
                            packets.Add(nestedObjectScript && !child ? Row("CHILD", "1", "Owner目标", "Child_Type", collectionScript ? "COLLECTION" : "OBJECT") : Row("ID", "1", null, "NUMBER", "NUMBER"));
                            packets.Add(Row("DATA", "2", null, refreshed ? "VARCHAR2" : "RAW", refreshed ? "VARCHAR2" : "RAW"));
                        }
                    }
                    else packets.Add(Row("Owner目标", child ? "Child_Type" : "Mixed_Type", child && collectionScript ? "COLLECTION" : "OBJECT", child && collectionScript ? null : "2"));
                    var eof = Report(false, 0); eof[0] = 254; packets.Add(eof);
                    return packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray();
                }
                async Task Dictionary(uint offset, byte headerId, byte memberId, bool refreshed, bool child = false)
                {
                    for (uint requestId = offset; requestId < offset + 6; requestId++)
                    {
                        var request = await ReadRequest(stream, channel, ob20, requestId + 2, budget.Token);
                        var relative = requestId - offset; var members = relative >= 3; var id = members ? memberId : headerId; byte[] response;
                        if (relative % 3 == 0)
                        {
                            Assert.Equal(22, request[0]); var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                            Assert.Contains(members ? child && collectionScript ? "ALL_COLL_TYPES" : "ALL_TYPE_ATTRS" : "ALL_TYPES", sql); Assert.DoesNotContain("Owner目标", sql); Assert.DoesNotContain("Mixed_Type", sql);
                            response = Frame(1, new byte[] { 0, id, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 })
                                .Concat(Frame(2, Column("p"))).Concat(Frame(3, Column("p"))).ToArray();
                        }
                        else if (relative % 3 == 1)
                        {
                            Assert.Equal(23, request[0]); Assert.Equal(id, request[1]);
                            var cursor = new ByteCursor(request); cursor.Skip(16);
                            Assert.Equal("Owner目标", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                            Assert.Equal(child ? "Child_Type" : "Mixed_Type", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes())); Assert.False(cursor.HasRemaining);
                            response = Result(members, refreshed, child);
                        }
                        else { Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, request); if (!ob20) channel.BeginCommand(); continue; }
                        await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 2, 1, response) : response, budget.Token);
                        if (!ob20) channel.BeginCommand();
                    }
                }
                await Dictionary(0, 9, 10, false);
                if (nestedObjectScript) await Dictionary(6, 11, 12, false, child: true);
                if (objectScript)
                {
                    var start = nestedObjectScript ? 14u : 8u;
                    for (uint i = 0; i < 3; i++)
                    {
                        var request = await ReadRequest(stream, channel, ob20, start + i, budget.Token);
                        Assert.Equal(3, request[0]);
                        var literal = collectionScript ? i == 0 ? "\"Owner目标\".\"Child_Type\"(HEXTORAW('0102'),NULL,HEXTORAW('0304'))" : i == 1 ? "NULL" : "\"Owner目标\".\"Child_Type\"()"
                            : i == 0 ? "\"Owner目标\".\"Mixed_Type\"(" + (nestedObjectScript ? "\"Owner目标\".\"Child_Type\"(31,HEXTORAW('0102'))" : "31") + ",HEXTORAW('0102'))" : i == 1 ? "NULL" : "\"Owner目标\".\"Mixed_Type\"(NULL,NULL)";
                        Assert.Equal("insert into t values ((" + literal + ")); update t set v=(" + literal + ")", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                        var response = Frame(1, new byte[] { 0, 1, 0, 14, 0, 0, 0 }).Concat(Frame(2, Report(false, 1))).ToArray();
                        await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, start + i, 1, response) : response, budget.Token);
                        if (!ob20) channel.BeginCommand();
                    }
                    var after = await ReadRequest(stream, channel, ob20, start + 3, budget.Token);
                    Assert.Equal(3, after[0]); Assert.Equal("after object script", Encoding.UTF8.GetString(after, 1, after.Length - 1));
                    var afterReply = Frame(1, Report(false, 1));
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, start + 3, 1, afterReply) : afterReply, budget.Token);
                    await ObserveClose(stream, budget.Token);
                    return;
                }
                var prepared = await ReadRequest(stream, channel, ob20, 8, budget.Token); Assert.Equal(22, prepared[0]);
                var preparedReply = Frame(1, new byte[] { 0, 19, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0 })
                    .Concat(Frame(2, Column("p"))).Concat(Frame(3, Column("p"))).Concat(Frame(4, Column("p"))).Concat(Frame(5, Column("p"))).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 8, 1, preparedReply) : preparedReply, budget.Token);
                if (!ob20) channel.BeginCommand();
                var complexColumn = Column("complex"); complexColumn[complexColumn.Length - 6] = 160;
                var complexName = Encoding.UTF8.GetBytes("Owner目标");
                complexColumn = complexColumn.Concat(new[] { (byte)complexName.Length }).Concat(complexName)
                    .Concat(new byte[] { 10 }).Concat(Encoding.UTF8.GetBytes("Mixed_Type")).Concat(new byte[] { 17 }).ToArray();
                var trailing = Column("after"); trailing[trailing.Length - 6] = 8;
                var anonymousColumn = Column("anonymous"); anonymousColumn[anonymousColumn.Length - 6] = 160;
                anonymousColumn = anonymousColumn.Concat(new byte[] { 0, 0, 0, 203 }).ToArray();
                for (uint requestId = 7; requestId < 9; requestId++)
                {
                    if (requestId == 8) { complexColumn[complexColumn.Length - 1] = 251; anonymousColumn[anonymousColumn.Length - 2] = 251; }
                    var request = await ReadRequest(stream, channel, ob20, requestId + 2, budget.Token); Assert.Equal(23, request[0]); Assert.Equal(19, request[1]);
                    var typeHeader = new byte[] { 160, 0, (byte)complexName.Length }.Concat(complexName)
                        .Concat(new byte[] { 10 }).Concat(Encoding.UTF8.GetBytes("Mixed_Type")).Concat(new byte[] { 0 }).ToArray();
                    var anonymousHeader = new byte[] { 160, 0, 0, 0, 0, 203 };
                    Assert.Equal(new byte[] { 10, 1 }.Concat(typeHeader).Concat(new byte[] { 6, 0 }).Concat(anonymousHeader).Concat(anonymousHeader)
                        .Concat(new byte[] { 0, 2, 51, 49, 2, 1, 2, 1, 0, 2, 8, 9 }), request.Skip(10));
                    var eof = Report(false, 0); eof[0] = 254;
                    var packets = new[] { new byte[] { 4 }, complexColumn, trailing, complexColumn, anonymousColumn,
                        new byte[] { 0, 16, 0, 2, 51, 49, 2, 1, 2 }.Concat(BitConverter.GetBytes(42L)).Concat(new byte[] { 2, 0, 2, 8, 9, 0 }).ToArray(), eof };
                    var response = packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 2, 1, response) : response, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var close = await ReadRequest(stream, channel, ob20, 11, budget.Token); Assert.Equal(new byte[] { 25, 19, 0, 0, 0 }, close);
                if (!ob20) channel.BeginCommand();
                var outputEof = Report(false, 0); outputEof[0] = 254;
                var outputRequest = new ByteCursor(await ReadRequest(stream, channel, ob20, 12, budget.Token));
                Assert.Equal(0xa1, outputRequest.ReadUInt8()); outputRequest.Skip(9);
                Assert.Equal("BEGIN pkg.p(?, ?); END;", Encoding.UTF8.GetString(outputRequest.ReadLengthEncodedBytes())); Assert.Equal(2u, outputRequest.ReadUInt32());
                var outputHeader = new byte[] { 3, 1, 6, 0, 6, 0 };
                Assert.Equal(outputHeader, outputRequest.ReadBytes(outputHeader.Length));
                var outputReply = Frame(1, new byte[] { 0, 20, 0, 0, 0, 2, 0, 2, 0, 0, 0, 0, 4, 0, 0, 0, 1 })
                    .Concat(Frame(2, complexColumn)).Concat(Frame(3, anonymousColumn)).Concat(Frame(4, complexColumn)).Concat(Frame(5, anonymousColumn))
                    .Concat(Frame(6, new byte[] { 0, 0, 0, 2, 51, 49, 2, 1, 2, 2, 8, 2, 8, 9 }))
                    .Concat(Frame(7, outputEof)).Concat(Frame(8, Report(false, 2))).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 12, 1, outputReply) : outputReply, budget.Token);
                if (!ob20) channel.BeginCommand();
                Assert.Equal(new byte[] { 25, 20, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 13, budget.Token));
                if (!ob20) channel.BeginCommand();
                var ddl = await ReadRequest(stream, channel, ob20, 14, budget.Token);
                Assert.Equal(3, ddl[0]); Assert.Equal("ALTER TYPE Owner目标.Mixed_Type MODIFY ATTRIBUTE DATA VARCHAR2(32) CASCADE", Encoding.UTF8.GetString(ddl, 1, ddl.Length - 1));
                var ddlReply = Frame(1, Report(false, 0));
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 14, 1, ddlReply) : ddlReply, budget.Token);
                if (!ob20) channel.BeginCommand();
                await Dictionary(13, 29, 30, true);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("complex-dictionary-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20};Allow Multi Statements={objectScript}";
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
                object? nestedValue = null; object? nestedType = null;
                if (nestedObjectScript)
                {
                    var members = (System.Collections.IEnumerable)type.GetType().GetProperty("Members")!.GetValue(type)!;
                    var member = members.Cast<object>().First(); nestedType = member.GetType().GetProperty("ComplexType")!.GetValue(member)!;
                    nestedValue = (await Call(nestedType, "CreateValue", new object?[] { collectionScript ? new object?[] { bytes, null, new byte[] { 3, 4 } } : new object?[] { 31, bytes } }))!;
                    values[0] = nestedValue;
                }
                var value = (await Call(type, "CreateValue", new object?[] { values }))!; bytes[0] = 9; values[0] = 99;
                var first = (object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!;
                if (nestedObjectScript)
                {
                    Assert.Same(nestedValue, first[0]);
                    var nestedFields = (object?[])(await Call(first[0]!, "GetValues", Array.Empty<object?>()))!;
                    if (collectionScript) { Assert.Equal(new byte[] { 1, 2 }, nestedFields[0]); Assert.Null(nestedFields[1]); Assert.Equal(new byte[] { 3, 4 }, nestedFields[2]); }
                    else { Assert.Equal(31, nestedFields[0]); Assert.Equal(new byte[] { 1, 2 }, nestedFields[1]); }
                }
                else Assert.Equal(31, first[0]);
                Assert.Equal(new byte[] { 1, 2 }, first[1]); ((byte[])first[1]!)[0] = 8;
                Assert.Equal(new byte[] { 1, 2 }, ((object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!)[1]);
                if (objectScript)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "insert into t values (:p); update t set v=:P";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p";
                    var parameterType = collectionScript ? nestedType! : type;
                    parameter.GetType().GetProperty("ComplexType")!.SetValue(parameter, parameterType); command.Parameters.Add(parameter);
                    foreach (var input in new[] { collectionScript ? nestedValue! : value, DBNull.Value, (await Call(parameterType, "CreateValue", new object?[] { collectionScript ? Array.Empty<object?>() : new object?[] { null, null } }))! })
                    {
                        parameter.Value = input;
                        Assert.Equal(2, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    }
                    await Call(connection, "ClearComplexTypeCache", new object?[] { null });
                    await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery()));
                    command.Parameters.Clear(); command.CommandText = "after object script";
                    Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    connection.Close(); await serving.WaitAsync(budget.Token);
                    return;
                }
                var arrayType = connection.GetType().GetMethod("CreateArrayType", new[] { typeof(string), assembly.GetType("OceanBaseConnector.OceanBaseTransaction")! })!
                    .Invoke(connection, new object?[] { "RAW", null })!;
                var arrayValue = (await Call(arrayType, "CreateValue", new object?[] { new object?[] { new byte[] { 8, 9 } } }))!;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "read complex :p, :n, :a, :z";
                    var input = command.CreateParameter(); input.ParameterName = "p"; input.Value = value; command.Parameters.Add(input);
                    var typedNull = command.CreateParameter(); typedNull.ParameterName = "n"; typedNull.Value = DBNull.Value;
                    typedNull.GetType().GetProperty("ComplexType")!.SetValue(typedNull, type); command.Parameters.Add(typedNull);
                    var array = command.CreateParameter(); array.ParameterName = "a"; array.Value = arrayValue; command.Parameters.Add(array);
                    var arrayNull = command.CreateParameter(); arrayNull.ParameterName = "z"; arrayNull.Value = DBNull.Value;
                    arrayNull.GetType().GetProperty("ComplexType")!.SetValue(arrayNull, arrayType); command.Parameters.Add(arrayNull);
                    if (async) await command.PrepareAsync(budget.Token); else command.Prepare();
                    foreach (var sequential in new[] { false, true })
                    {
                        using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token)
                            : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                        Assert.Same(assembly.GetType("OceanBaseConnector.OceanBaseComplexValue"), reader.GetFieldType(0));
                        Assert.Equal("Mixed_Type", reader.GetDataTypeName(0));
                        Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                        var result = async ? await reader.GetFieldValueAsync<object>(0, budget.Token) : reader.GetValue(0);
                        Assert.Same(type, result.GetType().GetProperty("Type")!.GetValue(result));
                        var fields = (object?[])(await Call(result, "GetValues", Array.Empty<object?>()))!;
                        Assert.Equal("31", fields[0]!.ToString()); Assert.Equal(new byte[] { 1, 2 }, fields[1]);
                        Assert.Equal(42L, async ? await reader.GetFieldValueAsync<long>(1, budget.Token) : reader.GetInt64(1));
                        Assert.True(async ? await reader.IsDBNullAsync(2, budget.Token) : reader.IsDBNull(2)); Assert.Equal(DBNull.Value, reader.GetValue(2));
                        Assert.Equal("ARRAY", reader.GetDataTypeName(3));
                        var anonymous = async ? await reader.GetFieldValueAsync<object>(3, budget.Token) : reader.GetValue(3);
                        var anonymousType = anonymous.GetType().GetProperty("Type")!.GetValue(anonymous)!;
                        Assert.True((bool)anonymousType.GetType().GetProperty("IsAnonymous")!.GetValue(anonymousType)!);
                        Assert.Equal("", anonymousType.GetType().GetProperty("TypeName")!.GetValue(anonymousType));
                        var anonymousItems = (object?[])(await Call(anonymous, "GetValues", Array.Empty<object?>()))!;
                        Assert.Equal(new byte[] { 8, 9 }, anonymousItems[0]); Assert.Empty((byte[])anonymousItems[1]!);
                        Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                    var output = command.CreateParameter(); output.ParameterName = "p"; output.Direction = ParameterDirection.Output; output.Value = "old";
                    output.GetType().GetProperty("ComplexType")!.SetValue(output, type); command.Parameters.Add(output);
                    var arrayOutput = command.CreateParameter(); arrayOutput.ParameterName = "a"; arrayOutput.Direction = ParameterDirection.Output; arrayOutput.Value = "old";
                    arrayOutput.GetType().GetProperty("ComplexType")!.SetValue(arrayOutput, arrayType); command.Parameters.Add(arrayOutput);
                    Assert.Equal(2, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    Assert.Same(type, output.Value!.GetType().GetProperty("Type")!.GetValue(output.Value));
                    var fields = (object?[])(await Call(output.Value, "GetValues", Array.Empty<object?>()))!;
                    Assert.Equal("31", fields[0]!.ToString()); Assert.Equal(new byte[] { 1, 2 }, fields[1]);
                    Assert.Same(arrayType, arrayOutput.Value!.GetType().GetProperty("Type")!.GetValue(arrayOutput.Value));
                    var elements = (object?[])(await Call(arrayOutput.Value, "GetValues", Array.Empty<object?>()))!;
                    Assert.Equal(new byte[] { 8, 9 }, elements[0]); Assert.Null(elements[1]);
                }
                using (var ddl = connection.CreateCommand())
                {
                    ddl.CommandText = "ALTER TYPE Owner目标.Mixed_Type MODIFY ATTRIBUTE DATA VARCHAR2(32) CASCADE";
                    Assert.Equal(0, async ? await ddl.ExecuteNonQueryAsync(budget.Token) : ddl.ExecuteNonQuery());
                }
                await Assert.ThrowsAsync<InvalidOperationException>(() => Call(type, "CreateValue", new object?[] { values }));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Call(arrayType, "CreateValue", new object?[] { Array.Empty<object?>() }));
                var snapshot = (object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!;
                Assert.Equal(31, snapshot[0]); Assert.Equal(new byte[] { 1, 2 }, snapshot[1]);
                using (var stale = connection.CreateCommand())
                {
                    stale.CommandText = "SELECT :p FROM dual";
                    var parameter = stale.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = value; stale.Parameters.Add(parameter);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => async ? stale.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(stale.ExecuteNonQuery()));
                }
                var fresh = (await Call(connection, async ? "GetComplexTypeAsync" : "GetComplexType", arguments))!;
                Assert.NotSame(type, fresh); Assert.Same(fresh, await Call(connection, async ? "GetComplexTypeAsync" : "GetComplexType", arguments));
                var freshValue = (await Call(fresh, "CreateValue", new object?[] { new object?[] { 31, "changed" } }))!;
                Assert.Equal("changed", ((object?[])(await Call(freshValue, "GetValues", Array.Empty<object?>()))!)[1]);
                await Call(connection, "ClearComplexTypeCache", new object?[] { null });
                await Assert.ThrowsAsync<InvalidOperationException>(() => Call(fresh, "CreateValue", new object?[] { new object?[] { 31, "changed" } }));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Call(type, "CreateValue", new object?[] { values }));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Call(arrayType, "CreateValue", new object?[] { Array.Empty<object?>() }));
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
        public static IEnumerable<object[]> LobAssets()
        {
            foreach (var args in OracleAssets())
                foreach (var character in new[] { false, true }) yield return args.Concat(new object[] { character }).ToArray();
        }

        internal static async Task EveryAssetReadsBoundLobChunksAndValidatesOb20CompletionCore(string target, bool async, string kind, bool character, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard"; var data = Encoding.UTF8.GetBytes("患者😀a"); var type = character ? (byte)211 : (byte)210;
            var locator = new byte[92]; new byte[] { 1, 132, 1, 0, 2, 1, 0, 0, 1, 129, 0, 0 }.CopyTo(locator, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(locator.AsSpan(28), data.Length);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0", lobLocators: true, tls: tls, certificate: certificate);
                byte[] Column(byte code, bool binary = false) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, binary ? (byte)63 : (byte)45, 0, 20, 0, 0, 0, code, 0, 0, 0, 0, 0 };
                byte[] Bytes(byte[] value) => new[] { (byte)value.Length }.Concat(value).ToArray();
                async Task Reply(uint id, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id + 2, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var query = await ReadRequest(stream, channel, ob20, 2, budget.Token);
                Assert.Equal("select lob_value", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                await Reply(0, Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(type))).Concat(Frame(3, Bytes(locator))).Concat(Frame(4, eof)).ToArray());
                var request = new ByteCursor(await ReadRequest(stream, channel, ob20, 3, budget.Token));
                Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                Assert.Equal("BEGIN DBMS_LOB.READ(?, ?, ?, ?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                Assert.Equal(4u, request.ReadUInt32()); Assert.Equal(8, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                Assert.Equal((ushort)type, request.ReadUInt16()); Assert.Equal((ushort)3, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal((ushort)6, request.ReadUInt16());
                Assert.Equal(locator, request.ReadLengthEncodedBytes()); Assert.Equal(character ? 8191u : (uint)data.Length, request.ReadUInt32()); Assert.Equal(1ul, request.ReadUInt64());
                var response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 2, 0, 4, 0, 0, 0, 0, 4, 0, 0, 0, 1 });
                for (var i = 0; i < 4; i++) response = response.Concat(Frame((byte)(2 + i), Column(253))).ToArray();
                response = response.Concat(Frame(6, Column(8))).Concat(Frame(7, Column(character ? (byte)253 : (byte)203, !character)))
                    .Concat(Frame(8, new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(character ? 4L : data.Length)).Concat(Bytes(data)).ToArray()))
                    .Concat(Frame(9, eof)).Concat(Frame(10, Report(false, 0))).ToArray();
                if (kind == "trailing") response = response.Concat(Frame(11, new byte[] { 0 })).ToArray();
                await Reply(1, response);
                if (kind != "trailing") Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 4, budget.Token));
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("lob-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use Lob Locators=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var reader = async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token) : command.ExecuteReader();
                Assert.Equal("OceanBaseConnector.OceanBaseLob", reader.GetFieldType(0).FullName); Assert.Equal(character ? "CLOB" : "BLOB", reader.GetDataTypeName(0));
                var column = Assert.Single(reader.GetColumnSchema()); Assert.True(column.IsLong); Assert.Null(column.ColumnSize);
                Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); using var lob = Assert.IsAssignableFrom<IDisposable>(reader.GetValue(0));
                Assert.Equal((long)data.Length, lob.GetType().GetProperty("ByteLength")!.GetValue(lob));
                Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                async Task Consume()
                {
                    if (character)
                    {
                        using var source = (TextReader)lob.GetType().GetMethod("OpenTextReader")!.Invoke(lob, null)!;
                        Assert.Equal("患者😀a", async ? await source.ReadToEndAsync() : source.ReadToEnd());
                    }
                    else
                    {
                        var source = (Stream)lob.GetType().GetMethod("OpenRead")!.Invoke(lob, null)!;
                        try { using var destination = new MemoryStream(); if (async) await source.CopyToAsync(destination, budget.Token); else source.CopyTo(destination); Assert.Equal(data, destination.ToArray()); }
                        finally { if (async) await source.DisposeAsync(); else source.Dispose(); }
                    }
                }
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Consume); Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else { await Consume(); Assert.Equal(ConnectionState.Open, connection.State); }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        internal static async Task EveryAssetMutatesLobLocatorsAndKeepsWriteOutcomeAtOb20BoundaryCore(string target, bool async, string kind, bool character, bool encrypted)
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

        internal static async Task Handshake(PacketChannel channel, Stream stream, string kind, CancellationToken token, bool deprecated = false, bool lobLocators = false, string version = "4.4.2.2", SslStream? tls = null, X509Certificate2? certificate = null, bool mysql = false, bool initializeAutocommit = true)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var upper = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1 + 2 + 1 + 2;
            if (kind != "no-attributes") greeting[upper] |= 16;
            if (mysql) greeting[upper] |= 4;
            if (deprecated) greeting[upper + 1] |= 1;
            if (lobLocators) greeting[upper + 1] |= 32;
            await channel.WritePacketAsync(greeting, token);
            if (tls is not null)
            {
                var request = await channel.ReadPacketAsync(token);
                Assert.Equal(32, request.Length);
                Assert.NotEqual(0, request[1] & 8);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
                Assert.True(tls.IsEncrypted);
                channel.ReplaceUnderlyingStream(tls);
            }
            var handshake = new ByteCursor(await channel.ReadPacketAsync(token)); handshake.Skip(32);
            Assert.Equal("u", handshake.ReadNullTerminatedAscii()); handshake.ReadLengthEncodedBytes(); handshake.ReadNullTerminatedAscii();
            var attributes = new Dictionary<string, string>();
            if (kind != "no-attributes")
            {
                var values = new ByteCursor(handshake.ReadLengthEncodedBytes());
                while (values.HasRemaining) attributes.Add(Encoding.UTF8.GetString(values.ReadLengthEncodedBytes()), Encoding.UTF8.GetString(values.ReadLengthEncodedBytes()));
            }
            Assert.False(handshake.HasRemaining);
            if (kind != "unrequested" && kind != "no-attributes") Assert.Equal("256", attributes["__proxy_capability_flag"]);
            else Assert.False(attributes.ContainsKey("__proxy_capability_flag"));
            if (lobLocators) Assert.Equal("1", attributes["__ob_client_attribute_capability_flag"]);
            var report = Report(kind != "unsupported", 0); if (mysql) report[3] &= 251;
            await channel.WritePacketAsync(report, token); channel.BeginCommand();
            await ReplyIdentity(channel, stream, kind == "enabled", token, deprecated, version, mysql, initializeAutocommit);
        }

        internal static async Task<byte[]> ReadRequest(Stream stream, PacketChannel channel, bool ob20, uint request, CancellationToken token)
        {
            return ob20 ? (await ReadOb20Request(stream, request, token)).Payload : await channel.ReadPacketAsync(token);
        }

        internal static async Task<(byte[] Payload, byte NextSequence)> ReadOb20Request(Stream stream, uint request, CancellationToken token)
        {
            using var payload = new MemoryStream(); byte sequence = 0;
            while (true)
            {
                var header = new byte[31]; await stream.ReadExactlyAsync(header, token);
                var parsed = Ob20Packet.ParseHeader(header, 1234, request, sequence);
                var bytes = new byte[35 + parsed.PayloadLength]; header.CopyTo(bytes, 0);
                await stream.ReadExactlyAsync(bytes.AsMemory(31), token);
                payload.Write(Ob20Packet.Parse(bytes, 1234, request, sequence).Payload);
                sequence = unchecked((byte)(sequence + 1));
                if ((parsed.Flags & Ob20Packet.LastPacketFlag) != 0) break;
            }
            var inner = payload.ToArray();
            Assert.Equal(0, inner[3]); Assert.Equal(inner.Length - 4, inner[0] | inner[1] << 8 | inner[2] << 16);
            return (inner.AsSpan(4).ToArray(), sequence);
        }

        internal static async Task ObserveClose(Stream stream, CancellationToken token)
        {
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException) { }
        }

        internal static byte[] TextResult(params string[] values) => TextResult(false, values);

        internal static byte[] TextResult(bool deprecated, params string[] values)
        {
            using var result = new MemoryStream(); byte sequence = 1;
            result.Write(Frame(sequence++, new byte[] { (byte)values.Length }));
            foreach (var value in values)
                result.Write(Frame(sequence++, new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 122, 1, 122, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }));
            if (!deprecated) result.Write(Frame(sequence++, new byte[] { 254, 0, 0, 6, 0 }));
            using var row = new MemoryStream();
            foreach (var value in values) { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
            result.Write(Frame(sequence++, row.ToArray())); result.Write(Frame(sequence, deprecated ? new byte[] { 254, 0, 0, 6, 0, 0, 0 } : new byte[] { 254, 0, 0, 6, 0 }));
            return result.ToArray();
        }
        internal static byte[] Frame(byte sequence, byte[] payload)
        {
            var bytes = new byte[4 + payload.Length]; bytes[0] = (byte)payload.Length;
            bytes[1] = (byte)(payload.Length >> 8); bytes[2] = (byte)(payload.Length >> 16); bytes[3] = sequence; payload.CopyTo(bytes, 4); return bytes;
        }
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "enabled", "unrequested", "unsupported", "no-attributes" })
                        yield return new object[] { target, async, kind };
        }

        internal static async Task Serve(TcpListener listener, string kind, CancellationToken token, X509Certificate2? certificate = null)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var tls = certificate is null ? null : new SslStream(peer.GetStream());
            Stream stream = tls is null ? peer.GetStream() : tls;
            var channel = new PacketChannel(peer.GetStream());
            await Handshake(channel, stream, kind, token, tls: tls, certificate: certificate);
            var ob20 = kind == "enabled";
            for (uint request = 0; request < 4; request++)
            {
                var command = await ReadRequest(stream, channel, ob20, request + 2, token);
                byte[] response;
                if (request == 0)
                { Assert.Equal(3, command[0]); Assert.Equal("update t set x=1", Encoding.UTF8.GetString(command, 1, command.Length - 1)); response = Frame(1, Report(false, 1)); }
                else if (request == 1)
                {
                    Assert.Equal(22, command[0]); Assert.Equal("update t set x=?", Encoding.UTF8.GetString(command, 1, command.Length - 1));
                    response = Frame(1, new byte[] { 0, 9, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 })
                        .Concat(Frame(2, new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 11, 0, 0, 0, 3, 0, 0, 0, 0, 0 }))
                        .Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 })).ToArray();
                }
                else if (request == 2)
                { Assert.Equal(23, command[0]); Assert.Equal(9, command[1]); Assert.Equal(7, command[command.Length - 4]); response = Frame(1, Report(false, 1)); }
                else { Assert.Equal(new byte[] { 25, 9, 0, 0, 0 }, command); break; }
                if (ob20) await stream.WriteAsync(Ob20Packet.Build(1234, request + 2, 1, response), token);
                else { await stream.WriteAsync(response, token); channel.BeginCommand(); }
            }
            await ObserveClose(stream, token);
        }

        internal static byte[] Report(bool ob20, byte affected, string? capability = null)
        {
            using var state = new MemoryStream();
            var names = new[] { "time_zone", "ncharacter_set_connection", "character_set_results", "character_set_connection", "ob_capability_flag" };
            var values = new[] { "+00:00", "utf8mb4", "utf8mb4", "utf8mb4", capability ?? (ob20 ? "256" : "0") };
            for (var i = 0; i < names.Length; i++)
            {
                state.WriteByte(0); state.WriteByte((byte)(2 + names[i].Length + values[i].Length));
                state.WriteByte((byte)names[i].Length); state.Write(Encoding.ASCII.GetBytes(names[i]));
                state.WriteByte((byte)values[i].Length); state.Write(Encoding.ASCII.GetBytes(values[i]));
            }
            Assert.True(state.Length < 251);
            using var packet = new MemoryStream(); packet.Write(new byte[] { 0, affected, 0, 6, 64, 0, 0, 0 });
            packet.WriteByte((byte)state.Length); packet.Write(state.ToArray()); return packet.ToArray();
        }

        internal static async Task ReplyIdentity(PacketChannel channel, Stream stream, bool ob20, CancellationToken token, bool deprecated = false, string version = "4.4.2.2", bool mysql = false, bool initializeAutocommit = true)
        {
            if (!ob20)
            {
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, oracle: !mysql, version: version, deprecated: deprecated, token: token, initializeAutocommit: initializeAutocommit);
                return;
            }
            // Oracle initialization follows identity; feature exchanges start at request 2 (MySQL: 1).
            var query = await ReadRequest(stream, channel, true, 0, token);
            Assert.Equal(3, query[0]);
            Assert.Equal(mysql ? "select ob_version(), @@ob_compatibility_mode, @@autocommit from dual" : "select ob_version(), @@ob_compatibility_mode from dual", Encoding.UTF8.GetString(query, 1, query.Length - 1));
            await stream.WriteAsync(Ob20Packet.Build(1234, 0, 1, TextResult(deprecated, mysql ? new[] { version, "MYSQL", "1" } : new[] { version, "ORACLE" })), token);
            if (!mysql && initializeAutocommit)
            {
                var setup = await ReadRequest(stream, channel, true, 1, token);
                Assert.Equal(3, setup[0]);
                Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(setup, 1, setup.Length - 1));
                await stream.WriteAsync(Ob20Packet.Build(1234, 1, 1, Frame(1, Report(false, 0))), token);
            }
        }
        public static IEnumerable<object[]> OracleAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "standard", "ob20", "trailing" }) yield return new object[] { target, async, kind };
        }

        internal static async Task EveryAssetExecutesOracleOutputsAndValidatesOb20BeforePublishingCore(string target, bool async, string kind, bool encrypted, bool batchExecution = false, int fetchSize = 0)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.2.0", tls: tls, certificate: certificate);
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                var eof = Report(false, 0); eof[0] = 254;
                var execute = new ByteCursor(await ReadRequest(stream, channel, ob20, 2, budget.Token));
                Assert.Equal(0xa1, execute.ReadUInt8()); Assert.Equal(0u, execute.ReadUInt32()); Assert.Equal(0, execute.ReadUInt8()); Assert.Equal(1u, execute.ReadUInt32());
                Assert.Equal("BEGIN pkg.proc(?, ?); END;", Encoding.UTF8.GetString(execute.ReadLengthEncodedBytes())); Assert.Equal(2u, execute.ReadUInt32());
                Assert.Equal(new byte[] { 2, 1, 8, 0, 6, 0, 7, 0, 0, 0, 0, 0, 0, 0 }, execute.ReadBytes(14));
                var response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 1, 0, 2, 0, 0, 0, 0, 4, 0, 0, 0, 1 })
                    .Concat(Frame(2, Column(8))).Concat(Frame(3, Column(8))).Concat(Frame(4, Column(8)))
                    .Concat(Frame(5, new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 })).Concat(Frame(6, eof)).Concat(Frame(7, Report(false, 2))).ToArray();
                if (kind == "trailing") response = response.Concat(Frame(8, new byte[] { 0 })).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 2, 1, response) : response, budget.Token);
                if (!ob20) channel.BeginCommand();
                if (kind != "trailing") Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 3, budget.Token));
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("oracle-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                var command = connection.CreateCommand(); command.CommandText = "pkg.proc"; command.CommandType = CommandType.StoredProcedure;
                command.GetType().GetProperty("FetchSize")!.SetValue(command, fetchSize);
                var input = command.CreateParameter(); input.ParameterName = "input"; input.Value = 7L; command.Parameters.Add(input);
                var output = command.CreateParameter(); output.ParameterName = "output"; output.Value = "old"; output.Direction = ParameterDirection.Output; command.Parameters.Add(output);
                using var batch = batchExecution ? connection.CreateBatch() : null;
                if (batch != null)
                {
                    var item = batch.CreateBatchCommand(); item.CommandText = command.CommandText; item.CommandType = command.CommandType;
                    command.Parameters.Clear(); item.Parameters.Add(input); item.Parameters.Add(output); batch.BatchCommands.Add(item);
                }
                int Execute() => batch == null ? command.ExecuteNonQuery() : batch.ExecuteNonQuery();
                Task<int> ExecuteAsync() => batch == null ? command.ExecuteNonQueryAsync(budget.Token) : batch.ExecuteNonQueryAsync(budget.Token);
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(async () => { if (async) await ExecuteAsync(); else Execute(); });
                    Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    Assert.Equal(2, async ? await ExecuteAsync() : Execute()); Assert.Equal(42L, output.Value);
                }
                if (batch != null) { if (async) await batch.DisposeAsync(); else batch.Dispose(); }
                if (async) await command.DisposeAsync(); else command.Dispose();
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        public static IEnumerable<object[]> OracleBatchAssets() => OracleAssets().Where(value => !((string)value[0]).StartsWith("netstandard", StringComparison.Ordinal));
        public static IEnumerable<object[]> OracleXaAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var kind in new[] { "success", "readonly", "code", "disconnect", "boundary" })
                            yield return new object[] { target, asynchronous, ob20, kind };
        }

        internal static async Task EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishingCore(string target, bool asynchronous, bool ob20, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, tls: tls, certificate: certificate);
                uint requestId = 0;
                async Task<byte[]> Read() => await ReadRequest(stream, channel, ob20, requestId++ + 2, budget.Token);
                async Task Reply(byte[] response)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 1, 1, response) : response, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                async Task Text(string sql)
                { var request = await Read(); Assert.Equal(3, request[0]); Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1)); }
                async Task Stage(string operation, int? flag = null, bool close = true)
                {
                    if (close) { Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await Read()); if (!ob20) channel.BeginCommand(); }
                    var cursor = new ByteCursor(await Read());
                    Assert.Equal(0xa1, cursor.ReadUInt8()); Assert.Equal(0u, cursor.ReadUInt32());
                    Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
                    Assert.Equal(operation == "RECOVER"
                        ? "declare x DBMS_XA_XID_ARRAY := DBMS_XA_XID_ARRAY(); BEGIN x := DBMS_XA.XA_RECOVER(); ? := x; END;"
                        : "select DBMS_XA.XA_" + operation + (flag.HasValue ? "(?, ?) from dual" : "(?) from dual"), Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                    Assert.Equal(flag.HasValue ? 2u : 1u, cursor.ReadUInt32());
                    Assert.Equal(operation == "RECOVER" ? 1 : 0, cursor.ReadUInt8()); Assert.Equal(1, cursor.ReadUInt8());
                    Assert.Equal(operation == "RECOVER" ? 6 : 160, cursor.ReadUInt16());
                    if (operation != "RECOVER")
                    {
                        Assert.Equal("SYS", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal("DBMS_XA_XID", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal(0, cursor.ReadUInt8());
                    }
                    if (flag.HasValue) Assert.Equal(operation == "COMMIT" ? 1 : 3, cursor.ReadUInt16());
                    if (operation != "RECOVER")
                    {
                        Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal("-2147483648", Encoding.ASCII.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal(new byte[] { 0, 39, 255 }, cursor.ReadLengthEncodedBytes()); Assert.Empty(cursor.ReadLengthEncodedBytes());
                        if (flag.HasValue) Assert.Equal((uint)flag.Value, operation == "COMMIT" ? cursor.ReadUInt8() : cursor.ReadUInt32());
                    }
                    Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0u, cursor.ReadUInt32()); cursor.ReadUInt32(); Assert.Equal(0u, cursor.ReadUInt32());
                    Assert.False(cursor.HasRemaining);
                }
                await Text("set autocommit = 0"); await Reply(Frame(1, new byte[] { 0, 0, 0, 0, 0, 0, 0 }));
                await Stage("START", 0, false); await Reply(OracleXaFrames(2, rowCompletion: asynchronous));
                await Stage("END", 0x04000000); await Reply(OracleXaFrames(2, rowCompletion: asynchronous));
                await Stage("PREPARE");
                if (kind == "disconnect") return;
                var prepared = OracleXaFrames(1, kind == "readonly" ? 3 : kind == "code" ? 100 : 0, badStatus: kind == "boundary" && !ob20, rowCompletion: asynchronous);
                if (kind == "boundary" && ob20) prepared = prepared.Concat(Frame(9, new byte[] { 0 })).ToArray();
                await Reply(prepared);
                if (kind == "success" || kind == "readonly")
                {
                    if (kind == "success") { await Stage("COMMIT", 0); await Reply(OracleXaFrames(2, rowCompletion: asynchronous)); }
                    await Stage("RECOVER"); await Reply(OracleXaFrames(1, recovery: true, rowCompletion: asynchronous));
                    await Stage("ROLLBACK"); await Reply(OracleXaFrames(1, rowCompletion: asynchronous));
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("oracle-xa-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                var resource = connectionType.GetMethod("GetXaResource")!.Invoke(connection, null)!;
                var xid = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseXid")!, int.MinValue, new byte[] { 0, 39, 255 }, Array.Empty<byte>())!;
                async Task<object?> Invoke(string method, params object[] parameters)
                {
                    try
                    {
                        var result = resource.GetType().GetMethod(method + (asynchronous ? "Async" : ""))!.Invoke(resource,
                            asynchronous ? parameters.Append((object)budget.Token).ToArray() : parameters);
                        if (result is Task task) { await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
                        return result;
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
                }
                await Invoke("Start", xid, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaStartMode")!, "New"));
                await Invoke("End", xid, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaEndMode")!, "Success"));
                if (kind == "success" || kind == "readonly")
                {
                    Assert.Equal(kind == "readonly" ? "ReadOnly" : "Prepared", (await Invoke("Prepare", xid))!.ToString());
                    if (kind == "success") await Invoke("Commit", xid, false);
                    var recovered = Assert.IsAssignableFrom<Array>(await Invoke("Recover")); Assert.Single(recovered); Assert.Equal(xid, recovered.GetValue(0));
                    await Invoke("Rollback", recovered.GetValue(0)!); Assert.Equal(ConnectionState.Open, connection.State);
                }
                else
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => Invoke("Prepare", xid));
                    Assert.Equal(kind != "code", error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                    Assert.Equal(kind == "code" ? (object)100 : null, error.GetType().GetProperty("XaErrorCode")!.GetValue(error));
                    Assert.Equal(0, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error)); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("Recover"));
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        internal static byte[] OracleXaFrames(byte parameters, long code = 0, bool recovery = false, bool badStatus = false, bool rowCompletion = false)
        {
            var complex = OracleXaColumn(160, recovery ? "DBMS_XA_XID_ARRAY" : "DBMS_XA_XID");
            var packets = new List<byte[]> { new byte[] { 0, 42, 0, 0, 0, 1, 0, parameters, 0, 0, 0, 0, recovery ? (byte)4 : (byte)0, 0, 0, 0, 1 }, complex };
            if (parameters == 2) packets.Add(OracleXaColumn(3));
            var eof = new byte[] { 254, 0, 0, (byte)(rowCompletion && !recovery ? 0x40 : 0), 0 }; packets.Add(eof); packets.Add(recovery ? complex : OracleXaColumn(8)); packets.Add(eof);
            packets.Add(recovery
                ? new byte[] { 0, 0, 1, 0, 0, 11 }.Concat(Encoding.ASCII.GetBytes("-2147483648")).Concat(new byte[] { 3, 0, 39, 255, 0 }).ToArray()
                : new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(code)).ToArray());
            packets.Add(eof); packets.Add(new byte[] { 0, (byte)(rowCompletion ? 1 : 0), 0,
                (byte)((badStatus ? 8 : 0) | (rowCompletion && !recovery ? 0x40 : 0)), (byte)(rowCompletion && recovery ? 0x10 : 0), 0, 0 });
            return packets.SelectMany((packet, i) => Frame((byte)(i + 1), packet)).ToArray();
        }

        internal static byte[] OracleXaColumn(byte type, string? name = null)
        {
            var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 128, 0, 0, 0, type, 0, 0, 0, 0, 0 };
            return name == null ? column : column.Concat(new byte[] { 3, 83, 89, 83, (byte)name.Length })
                .Concat(Encoding.ASCII.GetBytes(name)).Concat(new byte[] { 0 }).ToArray();
        }

        internal static async Task EveryAssetReadsRefCursorAfterParentCommandDisposalCore(string target, bool async, string kind, bool encrypted, bool prepared = false, CommandBehavior behavior = CommandBehavior.Default, bool parentOffset = false)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard";
            var limited = behavior != CommandBehavior.Default;
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.2.0", tls: tls, certificate: certificate);
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                async Task Reply(uint requestId, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 2, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var offset = prepared ? 1u : 0u;
                if (prepared)
                {
                    var preparation = await ReadRequest(stream, channel, ob20, 2, budget.Token);
                    Assert.Equal(22, preparation[0]);
                    Assert.Equal("BEGIN pkg.open_cursor(?); END;", Encoding.UTF8.GetString(preparation, 1, preparation.Length - 1));
                    await Reply(0, Frame(1, new byte[] { 0, 91, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }).Concat(Frame(2, Column(163))).ToArray());
                }
                var execute = new ByteCursor(await ReadRequest(stream, channel, ob20, 1 + offset + 1, budget.Token));
                Assert.Equal(0xa1, execute.ReadUInt8()); Assert.Equal(prepared ? 91u : 0u, execute.ReadUInt32()); Assert.Equal(0, execute.ReadUInt8()); Assert.Equal(1u, execute.ReadUInt32());
                Assert.Equal("BEGIN pkg.open_cursor(?); END;", Encoding.UTF8.GetString(execute.ReadLengthEncodedBytes()));
                Assert.Equal(1u, execute.ReadUInt32()); Assert.Equal(new byte[] { 1, 1, 6, 0 }, execute.ReadBytes(4));
                await Reply(offset, Frame(1, new byte[] { 0, 91, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 4, 0, 0, 0, 1 })
                    .Concat(Frame(2, Column(163))).Concat(Frame(3, Column(163))).Concat(Frame(4, new byte[] { 0, 0, 92, 0, 0, 0 }))
                    .Concat(Frame(5, eof)).Concat(Frame(6, Report(false, 0))).ToArray());
                Assert.Equal(parentOffset ? new byte[] { 28, 92, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0 }
                    : new byte[] { 28, 92, 0, 0, 0, 2, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 2 + offset + 1, budget.Token));
                var last = Report(false, 0); last[0] = 254; last[3] |= 128;
                var response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(8)))
                    .Concat(Frame(3, new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 })).Concat(Frame(4, last)).ToArray();
                if (parentOffset) response = response.Concat(Frame(5, Report(false, 1))).ToArray();
                if (limited)
                {
                    var more = Report(false, 0); more[0] = 254; more[3] |= 64;
                    await Reply(1 + offset, Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(8)))
                        .Concat(Frame(3, new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 }))
                        .Concat(Frame(4, new byte[] { 0, 0, 43, 0, 0, 0, 0, 0, 0, 0 })).Concat(Frame(5, more)).ToArray());
                    Assert.Equal(new byte[] { 28, 92, 0, 0, 0, 2, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 3 + offset + 1, budget.Token));
                    offset++;
                    response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(8)))
                        .Concat(Frame(3, new byte[] { 0, 0, 44, 0, 0, 0, 0, 0, 0, 0 })).Concat(Frame(4, last)).ToArray();
                }
                if (kind == "trailing") response = response.Concat(Frame(5, new byte[] { 0 })).ToArray();
                await Reply(1 + offset, response);
                if (kind != "trailing")
                {
                    Assert.Equal(new byte[] { 25, 92, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 3 + offset + 1, budget.Token));
                    if (!ob20) channel.BeginCommand();
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 4 + offset + 1, budget.Token));
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("ref-cursor-" + target, isCollectible: true);
            Exception? failure = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                var command = connection.CreateCommand(); command.CommandText = "pkg.open_cursor"; command.CommandType = CommandType.StoredProcedure;
                if (parentOffset)
                {
                    command.GetType().GetProperty("FetchSize")!.SetValue(command, 1);
                    command.GetType().GetProperty("UseCursorOffset")!.SetValue(command, true);
                }
                var output = command.CreateParameter(); output.ParameterName = "c"; output.Direction = ParameterDirection.Output;
                output.GetType().GetProperty("OceanBaseDbType")!.SetValue(output, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "RefCursor"));
                command.Parameters.Add(output);
                if (prepared)
                {
                    output.Value = "not published";
                    if (async) await command.PrepareAsync(budget.Token); else command.Prepare();
                    Assert.Equal("not published", output.Value);
                }
                if (async) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); command.Dispose();
                using var cursor = Assert.IsAssignableFrom<IDisposable>(output.Value);
                async Task Consume()
                {
                    DbDataReader reader;
                    if (async)
                    {
                        var method = cursor.GetType().GetMethods().Single(candidate => candidate.Name == "GetDataReaderAsync" && candidate.GetParameters().Length == 3);
                        var opening = (Task)method.Invoke(cursor, new object[] { 2, behavior | CommandBehavior.SequentialAccess, budget.Token })!;
                        await opening; reader = (DbDataReader)opening.GetType().GetProperty("Result")!.GetValue(opening)!;
                    }
                    else
                    {
                        var method = cursor.GetType().GetMethods().Single(candidate => candidate.Name == "GetDataReader" && candidate.GetParameters().Length == 2);
                        reader = (DbDataReader)method.Invoke(cursor, new object[] { 2, behavior })!;
                    }
                    using (reader)
                    {
                        var rows = limited && (behavior & CommandBehavior.SingleRow) == 0 ? 3 : 1;
                        for (var row = 0; row < rows; row++)
                        {
                            Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(42L + row, reader.GetInt64(0));
                        }
                        Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                        Assert.False(async ? await reader.NextResultAsync(budget.Token) : reader.NextResult());
                    }
                }
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Consume);
                    Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else { await Consume(); Assert.Equal(ConnectionState.Open, connection.State); }
                cursor.Dispose(); connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                budget.Cancel(); listener.Stop();
                try { await serving; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                catch (Exception serverError) { if (failure != null && !ReferenceEquals(failure, serverError)) throw new AggregateException(failure, serverError); throw; }
                finally { context.Unload(); }
            }
        }
        public static IEnumerable<object[]> TemporaryLobAssets()
        {
            foreach (var args in LobAssets())
            {
                if ((string)args[2] != "trailing") { yield return args; continue; }
                args[2] = "create-trailing"; yield return args;
                var free = (object[])args.Clone(); free[2] = "free-trailing"; yield return free;
            }
        }

        internal static async Task EveryAssetCreatesMutatesAndExplicitlyFreesTemporaryLobsCore(string target, bool async, string kind, bool character, bool encrypted)
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
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0", lobLocators: true, tls: tls, certificate: certificate);
                byte[] Column(byte code) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, code, 0, 0, 0, 0, 0 };
                byte[] Bytes(byte[] value) => new[] { (byte)value.Length }.Concat(value).ToArray();
                async Task Reply(uint id, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id + 2, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                for (var operation = 0; operation < 3; operation++)
                {
                    var id = (uint)(operation * 2); var parameters = operation == 0 ? 1 : operation == 1 ? 4 : 2;
                    var request = new ByteCursor(await ReadRequest(stream, channel, ob20, id + 2, budget.Token)); Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                    var expected = operation == 0 ? "DECLARE lob_value " + (character ? "CLOB" : "BLOB") + "; BEGIN DBMS_LOB.CREATETEMPORARY(lob_value, FALSE, DBMS_LOB.SESSION); ? := lob_value; END;"
                        : operation == 1 ? "BEGIN DBMS_LOB.WRITE(?, ?, ?, ?); END;" : "DECLARE lob_value " + (character ? "CLOB" : "BLOB") + " := ?; BEGIN DBMS_LOB.FREETEMPORARY(lob_value); ? := '1'; END;";
                    Assert.Equal(expected, Encoding.UTF8.GetString(request.ReadLengthEncodedBytes())); Assert.Equal((uint)parameters, request.ReadUInt32());
                    Assert.Equal(operation == 0 ? 1 : operation == 1 ? 0 : 2, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                    if (operation == 0) Assert.Equal((ushort)6, request.ReadUInt16());
                    else
                    {
                        Assert.Equal((ushort)type, request.ReadUInt16());
                        if (operation == 1)
                        { Assert.Equal((ushort)3, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal((ushort)253, request.ReadUInt16()); }
                        else Assert.Equal((ushort)6, request.ReadUInt16());
                        Assert.Equal(Locator(operation == 1 ? Array.Empty<byte>() : data), request.ReadLengthEncodedBytes());
                        if (operation == 1)
                        { Assert.Equal(character ? 4u : (uint)data.Length, request.ReadUInt32()); Assert.Equal(1ul, request.ReadUInt64()); var buffer = request.ReadLengthEncodedBytes(); Assert.Equal(data, character ? buffer : Convert.FromHexString(Encoding.ASCII.GetString(buffer))); }
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
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, id + 3, budget.Token));
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
        public static IEnumerable<object[]> XaAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var kind in new[] { "success", "reject", "disconnect", "boundary" })
                            yield return new object[] { target, asynchronous, ob20, kind };
        }

        internal static async Task PinsXaPhasesAndRecoversWithoutReplaying(string target, bool asynchronous, bool ob20, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var emptyBranch = kind == "reject";
            var identity = emptyBranch ? "0x0027ff,X'',0x80000000" : "0x0027ff,0x00ff,0x80000000";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, tls: tls, certificate: certificate);
                var commands = new List<string> { "XA START " + identity, "update sample", "XA END " + identity, "XA PREPARE " + identity };
                if (kind == "reject") commands.Add("XA PREPARE " + identity);
                if (kind == "success" || kind == "reject") commands.AddRange(new[] { "XA COMMIT " + identity, "XA RECOVER", "XA ROLLBACK " + identity });
                for (uint i = 0; i < commands.Count; i++)
                {
                    var query = await ReadRequest(stream, channel, ob20, i + 2, budget.Token);
                    Assert.Equal(3, query[0]); Assert.Equal(commands[(int)i], Encoding.UTF8.GetString(query, 1, query.Length - 1));
                    if (i == 3 && kind == "disconnect") return;
                    var response = commands[(int)i] == "XA RECOVER" ? XaRecoveryFrames(emptyBranch)
                        : Frame(1, kind == "reject" && i == 3
                            ? new byte[] { 255, 119, 5, 35, 88, 65, 69, 48, 55 } // 1399/XAE07: ordinary phase rejection.
                            : new byte[] { 0, 0, 0, (byte)(i == 1 ? 3 : 2), 0, 0, 0 });
                    if (i == 3 && kind == "boundary")
                    {
                        if (ob20) response = response.Concat(Frame(2, new byte[] { 0 })).ToArray();
                        else response = Frame(1, new byte[] { 0, 0, 0, 10, 0, 0, 0 }); // Forbidden MORE_RESULTS.
                    }
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, i + 2, 1, response) : response, budget.Token);
                    if (i == 3 && kind == "boundary") break;
                    if (!ob20) channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("xa-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                // A05 public MySQL identification remains strict; this is an explicit post-auth fixture.
                var session = connectionType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = field.GetValue(session)!; var infoType = info.GetType();
                object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"),
                    Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                var resource = connectionType.GetMethod("GetXaResource")!.Invoke(connection, null)!;
                Assert.True(resource.GetType().IsSealed); Assert.False(resource is IDisposable);
                var xidType = assembly.GetType("OceanBaseConnector.OceanBaseXid")!;
                var xid = Activator.CreateInstance(xidType, int.MinValue, new byte[] { 0, 39, 255 }, emptyBranch ? Array.Empty<byte>() : new byte[] { 0, 255 })!;
                var startMode = Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaStartMode")!, "New");
                var endMode = Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaEndMode")!, "Success");
                async Task<object?> Invoke(string method, params object[] parameters)
                {
                    try
                    {
                        var args = asynchronous ? parameters.Append((object)budget.Token).ToArray() : parameters;
                        var result = resource.GetType().GetMethod(method + (asynchronous ? "Async" : ""))!.Invoke(resource, args);
                        if (result is Task task) { await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
                        return result;
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
                }
                using var command = connection.CreateCommand(); command.CommandText = "update sample";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                await Invoke("Start", xid, startMode);
                Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                await Invoke("End", xid, endMode);
                if (kind == "disconnect" || kind == "boundary")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => Invoke("Prepare", xid));
                    Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("Prepare", xid));
                }
                else
                {
                    if (kind == "reject")
                    {
                        var error = await Assert.ThrowsAnyAsync<DbException>(() => Invoke("Prepare", xid));
                        Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                        Assert.Equal(1399, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error));
                    }
                    Assert.Equal("Prepared", (await Invoke("Prepare", xid))!.ToString());
                    await Invoke("Commit", xid, false);
                    var recovered = Assert.IsAssignableFrom<Array>(await Invoke("Recover")); Assert.Single(recovered);
                    Assert.Equal(xid, recovered.GetValue(0)); await Invoke("Rollback", recovered.GetValue(0)!);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("Recover"));
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        internal static byte[] XaRecoveryFrames(bool emptyBranch)
        {
            byte[] XaColumn(string name, byte type, byte charset)
            {
                var prefix = new byte[] { 3, 100, 101, 102, 0, 0, 0, (byte)name.Length }.Concat(Encoding.ASCII.GetBytes(name))
                    .Concat(new[] { (byte)name.Length }).Concat(Encoding.ASCII.GetBytes(name));
                return prefix.Concat(new byte[] { 12, charset, 0, 128, 0, 0, 0, type, 0, 0, 0, 0, 0 }).ToArray();
            }
            var row = new List<byte>();
            foreach (var field in new[] { Encoding.ASCII.GetBytes("2147483648"), new byte[] { 51 }, new byte[] { emptyBranch ? (byte)48 : (byte)50 },
                emptyBranch ? new byte[] { 0, 39, 255 } : new byte[] { 0, 39, 255, 0, 255 } })
            { row.Add((byte)field.Length); row.AddRange(field); }
            return Frame(1, new byte[] { 4 }).Concat(Frame(2, XaColumn("formatID", 8, 63)))
                .Concat(Frame(3, XaColumn("gtrid_length", 3, 63))).Concat(Frame(4, XaColumn("bqual_length", 3, 63)))
                .Concat(Frame(5, XaColumn("data", 252, 63))).Concat(Frame(6, new byte[] { 254, 0, 0, 2, 0 }))
                .Concat(Frame(7, row.ToArray())).Concat(Frame(8, new byte[] { 254, 0, 0, 2, 0 })).ToArray();
        }

        internal static async Task MySqlProcedureConsumerCore(string target, bool async, bool ob20, bool encrypted, bool deprecated, int fetchSize = 0, bool inoutStream = false)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var modern = !target.StartsWith("netstandard", StringComparison.Ordinal);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: deprecated, tls: tls, certificate: certificate, mysql: true);
                uint requestId = 0;
                async Task<byte[]> Read() { if (!ob20) channel.BeginCommand(); return await ReadRequest(stream, channel, ob20, ++requestId, budget.Token); }
                async Task Reply(params byte[][] packets)
                {
                    var response = packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, response) : response, budget.Token);
                }
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 120, 1, 120, 12, 45, 0, 255, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                byte[] End(byte status) => deprecated ? new byte[] { 254, 0, 0, status, 0, 0, 0 } : new byte[] { 254, 0, 0, status, 0 };
                async Task Prepare(byte id, byte count)
                {
                    var packets = new List<byte[]> { new byte[] { 0, id, 0, 0, 0, 0, 0, count, 0, 0, 0, 0 } };
                    for (var i = 0; i < count; i++) packets.Add(Column(253));
                    if (!deprecated && count > 0) packets.Add(End(2));
                    await Reply(packets.ToArray());
                }
                for (var execution = 0; execution < (modern ? 2 : 1); execution++)
                {
                    var request = await Read(); Assert.Equal(22, request[0]);
                    var setup = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                    var variables = setup.Substring(4).Split(", ").Select(a => a.Split(" = ")[0]).ToArray();
                    Assert.Equal(2, variables.Length); Assert.All(variables, v => Assert.Matches("^@obc_[0-9a-f]{32}_[0-9]+$", v));
                    await Prepare(80, 2);
                    if (inoutStream) Assert.Equal(new byte[] { 24, 80, 0, 0, 0, 1, 0, 0, 255, 23 }, await Read());
                    request = await Read(); Assert.Equal(23, request[0]); Assert.Equal(80, request[1]);
                    await Reply(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                    Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await Read());
                    Assert.Equal(OceanBaseCommand.SqlPacket("CALL `p`(?, " + string.Join(", ", variables) + ")", 22), await Read());
                    await Prepare(81, 1); request = await Read(); Assert.Equal(23, request[0]); Assert.Equal(81, request[1]); Assert.Equal(0, request[5]);
                    var result = new List<byte[]> { new byte[] { 1 }, Column(8) };
                    if (!deprecated) result.Add(End(2));
                    result.Add(new byte[] { 0, 0, 9, 0, 0, 0, 0, 0, 0, 0 }); result.Add(End(10)); result.Add(new byte[] { 0, 3, 0, 2, 0, 0, 0 });
                    await Reply(result.ToArray());
                    Assert.Equal(OceanBaseCommand.SqlPacket("SELECT " + string.Join(", ", variables), 3), await Read());
                    if (inoutStream)
                    {
                        var binaryColumn = Column(252); binaryColumn[12] = 63;
                        var output = new List<byte[]> { new byte[] { 2 }, Column(8), binaryColumn };
                        if (!deprecated) output.Add(End(2));
                        output.Add(new byte[] { 2, 52, 50, 2, 0, 255 }); output.Add(End(2));
                        await Reply(output.ToArray());
                    }
                    else
                    {
                        var output = TextResult(deprecated, "42", "new");
                        await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, output) : output, budget.Token);
                    }
                    Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await Read());
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("mysql-procedure-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                DbParameter[] Bind(DbParameterCollection collection, Func<DbParameter> create)
                {
                    var input = create(); input.Value = 7L; collection.Add(input);
                    var output = create(); output.Direction = ParameterDirection.Output; output.Value = "old"; collection.Add(output);
                    var inout = create(); inout.Direction = ParameterDirection.InputOutput;
                    inout.Value = inoutStream ? new MemoryStream(new byte[] { 17, 0, 255, 23 }) { Position = 1 } : "input";
                    if (inoutStream) inout.DbType = DbType.Binary;
                    collection.Add(inout);
                    return new[] { output, inout };
                }
                async Task ReadClose(DbDataReader reader, DbParameter[] parameters)
                {
                    using var source = parameters[1].Value as MemoryStream;
                    using (reader)
                    {
                        Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                        Assert.Equal("old", parameters[0].Value);
                        if (inoutStream) Assert.Same(source, parameters[1].Value); else Assert.Equal("input", parameters[1].Value);
                        if (async) await reader.CloseAsync(); else reader.Close();
                        Assert.Equal(inoutStream ? 42L : "42", parameters[0].Value);
                        if (inoutStream) Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(parameters[1].Value)); else Assert.Equal("new", parameters[1].Value);
                        Assert.Equal(3, reader.RecordsAffected);
                        if (inoutStream) { Assert.True(source!.CanRead); Assert.Equal(source.Length, source.Position); }
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                    command.GetType().GetProperty("FetchSize")!.SetValue(command, fetchSize);
                    var parameters = Bind(command.Parameters, command.CreateParameter);
                    await ReadClose(async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token) : command.ExecuteReader(CommandBehavior.SequentialAccess), parameters);
                    Assert.Equal(fetchSize, command.GetType().GetProperty("FetchSize")!.GetValue(command));
                }
                if (modern)
                {
                    using var batch = connection.CreateBatch(); var item = batch.CreateBatchCommand(); item.CommandText = "p"; item.CommandType = CommandType.StoredProcedure; batch.BatchCommands.Add(item);
                    var parameters = Bind(item.Parameters, item.CreateParameter);
                    await ReadClose(async ? await batch.ExecuteReaderAsync(budget.Token) : batch.ExecuteReader(), parameters); Assert.Equal(3, item.RecordsAffected);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        public static IEnumerable<object[]> MySqlProcedureAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var tls in new[] { OceanBaseConnector.Tests.TransportTestMode.Encrypted })
                            foreach (var deprecated in new[] { false, true }) yield return new object[] { target, async, ob20, tls, deprecated };
        }

        public static IEnumerable<object[]> DecimalScaleAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var mysql in new[] { false, true }) yield return new object[] { target, asynchronous, mysql };
        }

        internal static byte[] KeyConsumerColumn(string name, byte type = 253, string source = "", ushort flags = 0)
        {
            using var column = new MemoryStream();
            foreach (var field in new[] { "def", source.Length == 0 ? "" : "Tenant", source.Length == 0 ? "" : "l", source.Length == 0 ? "" : "ledger", name, source })
            { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
            column.Write(new byte[] { 12, 45, 0, 80, 0, 0, 0, type, (byte)flags, (byte)(flags >> 8), 0, 0, 0 }); return column.ToArray();
        }

        public static IEnumerable<object[]> LongDataAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true }) yield return new object[] { target, asynchronous, ob20 };
        }

        public static IEnumerable<object[]> OracleMultiParameterAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        yield return new object[] { target, asynchronous, ob20 };
        }
    }
}
