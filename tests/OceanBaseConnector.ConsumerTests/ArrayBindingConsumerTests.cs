using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
        [Theory]
        [MemberData(nameof(OracleAssets))]
        public async Task EveryAssetExecutesArrayReturningAndKeepsOutputsAtomicAcrossOb20(string target, bool asynchronous, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0");
                async Task Reply(uint id, params byte[][] packets)
                {
                    var response = packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id + 1, 1, response) : response, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                byte[] Status(byte affected, byte flags) { var packet = Report(ob20, affected); packet[3] = flags; return packet; }
                async Task Sql(uint id, string sql, byte status)
                {
                    var request = await ReadRequest(stream, channel, ob20, id + 1, budget.Token);
                    Assert.Equal(3, request[0]); Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    await Reply(id, Status(0, status));
                }
                byte[] Column(string name, byte type)
                {
                    var text = Encoding.ASCII.GetBytes(name);
                    return new byte[] { 3, 100, 101, 102, 0, 0, 0, (byte)text.Length }.Concat(text).Concat(new[] { (byte)text.Length }).Concat(text)
                        .Concat(new byte[] { 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 }).ToArray();
                }
                byte[] Row(long index, long error, string? value)
                {
                    var row = new byte[] { 0, error != 0 ? (byte)16 : value == null ? (byte)48 : (byte)32 }
                        .Concat(BitConverter.GetBytes(index)).Concat(BitConverter.GetBytes(error));
                    if (value != null) row = row.Concat(new[] { (byte)value.Length }).Concat(Encoding.ASCII.GetBytes(value));
                    if (error != 0) row = row.Concat(new byte[] { 6, 115, 101, 99, 114, 101, 116 });
                    return row.ToArray();
                }
                await Sql(0, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", 6); await Sql(1, "set autocommit = 0", 4);
                var eof = Status(0, 5); eof[0] = 254;
                var execute = new ByteCursor(await ReadRequest(stream, channel, ob20, 3, budget.Token));
                Assert.Equal(0xa1, execute.ReadUInt8()); Assert.Equal(0u, execute.ReadUInt32()); Assert.Equal(0, execute.ReadUInt8()); Assert.Equal(2u, execute.ReadUInt32());
                Assert.Equal("update t set x=? returning x into ?", Encoding.UTF8.GetString(execute.ReadLengthEncodedBytes())); Assert.Equal(2u, execute.ReadUInt32());
                Assert.Equal(new byte[] { 0, 1, 160, 0, 0, 0, 0, 246, 160, 0, 0, 0, 0, 246, 3, 2, 1, 49, 1, 50, 3, 7 }, execute.ReadBytes(22));
                Assert.Equal(128u, execute.ReadUInt32()); Assert.Equal(0u, execute.ReadUInt32()); execute.ReadUInt32(); Assert.Equal(0u, execute.ReadUInt32()); Assert.False(execute.HasRemaining);
                var packets = new[]
                {
                    new byte[] { 0, 91, 0, 0, 0, 4, 0, 2, 0, 0, 0, 0, 3, 0, 0, 0, 1 }, Column("p", 8), Column("p", 8),
                    Column("sql_no", 8), Column("error_code", 8), Column("out", 0), Column("error_message", 253),
                    Row(0, 0, "42"), Row(0, 0, "43"), Row(1, 1, null), Row(2, 0, null), eof, Status(3, 5),
                };
                if (kind == "trailing") packets = packets.Concat(new[] { new byte[] { 0 } }).ToArray();
                await Reply(2, packets);
                if (kind != "trailing")
                {
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 4, budget.Token));
                    if (!ob20) channel.BeginCommand();
                    await Sql(4, "ROLLBACK", 4); await Sql(5, "set autocommit = 1", 6);
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("array-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute=true;Use OB20={ob20}";
                await connection.OpenAsync(budget.Token); using var transaction = await connection.BeginTransactionAsync(budget.Token);
                var type = connection.GetType().GetMethod("CreateArrayType", new[] { typeof(string), transaction.GetType() })!.Invoke(connection, new object[] { "NUMBER", transaction })!;
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "update t set x=:p returning x into :o";
                var input = command.CreateParameter(); input.ParameterName = "p"; input.Value = type.GetType().GetMethod("CreateValue")!.Invoke(type, new object[] { new object?[] { 1, null, 2 } }); command.Parameters.Add(input);
                var output = command.CreateParameter(); output.ParameterName = "o"; output.Direction = ParameterDirection.Output; output.Value = "old";
                output.GetType().GetProperty("ComplexType")!.SetValue(output, type); command.Parameters.Add(output);
                async Task<object> Execute()
                {
                    try
                    {
                        if (!asynchronous) return command.GetType().GetMethod("ExecuteArray")!.Invoke(command, new object[] { 3 })!;
                        var task = (Task)command.GetType().GetMethod("ExecuteArrayAsync")!.Invoke(command, new object[] { 3, budget.Token })!;
                        await task; return task.GetType().GetProperty("Result")!.GetValue(task)!;
                    }
                    catch (TargetInvocationException error) { throw error.InnerException!; }
                }
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Execute); Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
                    var partial = command.GetType().GetProperty("ArrayBindingResult")!.GetValue(command)!;
                    Assert.False((bool)partial.GetType().GetProperty("IsComplete")!.GetValue(partial)!);
                    var items = ((IEnumerable)partial.GetType().GetProperty("Items")!.GetValue(partial)!).Cast<object>().ToArray();
                    Assert.Equal("Unknown", items[2].GetType().GetProperty("Status")!.GetValue(items[2])!.ToString());
                }
                else
                {
                    var result = await Execute(); Assert.True((bool)result.GetType().GetProperty("IsComplete")!.GetValue(result)!);
                    Assert.Equal(3L, result.GetType().GetProperty("RecordsAffected")!.GetValue(result));
                    var groups = Assert.IsType<object[][]>(output.Value); Assert.Equal(new[] { "42", "43" }, groups[0].Select(x => x.ToString()));
                    Assert.Empty(groups[1]); Assert.Equal(DBNull.Value, Assert.Single(groups[2]));
                    var items = ((IEnumerable)result.GetType().GetProperty("Items")!.GetValue(result)!).Cast<object>().ToArray();
                    Assert.Equal(1, items[1].GetType().GetProperty("ServerErrorCode")!.GetValue(items[1]));
                    command.Dispose(); await transaction.RollbackAsync(budget.Token);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
