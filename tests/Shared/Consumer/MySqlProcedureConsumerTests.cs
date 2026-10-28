using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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

        [Theory]
        [MemberData(nameof(MySqlProcedureAssets))]
        public async Task EveryAssetExecutesMySqlProcedureAndPublishesOutputsOnClose(string target, bool async, bool ob20, bool encrypted, bool deprecated)
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
                    await Prepare(80, 2); request = await Read(); Assert.Equal(23, request[0]); Assert.Equal(80, request[1]);
                    await Reply(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                    Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await Read());
                    Assert.Equal(OceanBaseCommand.SqlPacket("CALL `p`(?, " + string.Join(", ", variables) + ")", 22), await Read());
                    await Prepare(81, 1); request = await Read(); Assert.Equal(23, request[0]); Assert.Equal(81, request[1]);
                    var result = new List<byte[]> { new byte[] { 1 }, Column(8) };
                    if (!deprecated) result.Add(End(2));
                    result.Add(new byte[] { 0, 0, 9, 0, 0, 0, 0, 0, 0, 0 }); result.Add(End(10)); result.Add(new byte[] { 0, 3, 0, 2, 0, 0, 0 });
                    await Reply(result.ToArray());
                    Assert.Equal(OceanBaseCommand.SqlPacket("SELECT " + string.Join(", ", variables), 3), await Read());
                    var output = TextResult(deprecated, "42", "new");
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, output) : output, budget.Token);
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
                    var inout = create(); inout.Direction = ParameterDirection.InputOutput; inout.Value = "input"; collection.Add(inout);
                    return new[] { output, inout };
                }
                async Task ReadClose(DbDataReader reader, DbParameter[] parameters)
                {
                    using (reader)
                    {
                        Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                        Assert.Equal("old", parameters[0].Value); Assert.Equal("input", parameters[1].Value);
                        if (async) await reader.CloseAsync(); else reader.Close();
                        Assert.Equal("42", parameters[0].Value); Assert.Equal("new", parameters[1].Value); Assert.Equal(3, reader.RecordsAffected);
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                    var parameters = Bind(command.Parameters, command.CreateParameter);
                    await ReadClose(async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token) : command.ExecuteReader(CommandBehavior.SequentialAccess), parameters);
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
    }
}
