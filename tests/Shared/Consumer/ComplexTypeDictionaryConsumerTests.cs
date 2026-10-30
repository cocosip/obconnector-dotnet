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
        public static IEnumerable<object[]> ComplexDictionaryAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var ob20 in new[] { false, true }) yield return new object[] { target, async, ob20 };
        }

        private static async Task EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValuesCore(string target, bool async, bool ob20, bool encrypted, bool objectScript = false, bool nestedObjectScript = false)
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
                        packets.Add(nestedObjectScript && !child ? Row("CHILD", "1", "Owner目标", "Child_Type", "OBJECT") : Row("ID", "1", null, "NUMBER", "NUMBER"));
                        packets.Add(Row("DATA", "2", null, refreshed ? "VARCHAR2" : "RAW", refreshed ? "VARCHAR2" : "RAW"));
                    }
                    else packets.Add(Row("Owner目标", child ? "Child_Type" : "Mixed_Type", "OBJECT", "2"));
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
                            Assert.Contains(members ? "ALL_TYPE_ATTRS" : "ALL_TYPES", sql); Assert.DoesNotContain("Owner目标", sql); Assert.DoesNotContain("Mixed_Type", sql);
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
                        var literal = i == 0 ? "\"Owner目标\".\"Mixed_Type\"(" + (nestedObjectScript ? "\"Owner目标\".\"Child_Type\"(31,HEXTORAW('0102'))" : "31") + ",HEXTORAW('0102'))" : i == 1 ? "NULL" : "\"Owner目标\".\"Mixed_Type\"(NULL,NULL)";
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
                    Assert.Equal(new byte[] { 10, 1 }.Concat(typeHeader).Concat(typeHeader).Concat(anonymousHeader).Concat(anonymousHeader)
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
                object? nestedValue = null;
                if (nestedObjectScript)
                {
                    var members = (System.Collections.IEnumerable)type.GetType().GetProperty("Members")!.GetValue(type)!;
                    var member = members.Cast<object>().First(); var child = member.GetType().GetProperty("ComplexType")!.GetValue(member)!;
                    nestedValue = (await Call(child, "CreateValue", new object?[] { new object?[] { 31, bytes } }))!;
                    values[0] = nestedValue;
                }
                var value = (await Call(type, "CreateValue", new object?[] { values }))!; bytes[0] = 9; values[0] = 99;
                var first = (object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!;
                if (nestedObjectScript)
                {
                    Assert.Same(nestedValue, first[0]);
                    var nestedFields = (object?[])(await Call(first[0]!, "GetValues", Array.Empty<object?>()))!;
                    Assert.Equal(31, nestedFields[0]); Assert.Equal(new byte[] { 1, 2 }, nestedFields[1]);
                }
                else Assert.Equal(31, first[0]);
                Assert.Equal(new byte[] { 1, 2 }, first[1]); ((byte[])first[1]!)[0] = 8;
                Assert.Equal(new byte[] { 1, 2 }, ((object?[])(await Call(value, "GetValues", Array.Empty<object?>()))!)[1]);
                if (objectScript)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "insert into t values (:p); update t set v=:P";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p";
                    parameter.GetType().GetProperty("ComplexType")!.SetValue(parameter, type); command.Parameters.Add(parameter);
                    foreach (var input in new[] { value, DBNull.Value, (await Call(type, "CreateValue", new object?[] { new object?[] { null, null } }))! })
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
    }
}
