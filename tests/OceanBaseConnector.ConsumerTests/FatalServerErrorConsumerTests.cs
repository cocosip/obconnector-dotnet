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
        [Theory]
        [MemberData(nameof(LongDataAssets))]
        public async Task EveryAssetPreservesFatalPieceErrBeforeTargetExecution(string target, bool asynchronous, bool ob20)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(stop.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, wire, ob20 ? "enabled" : "unrequested", stop.Token);
                Assert.Equal(22, (await ReadRequest(wire, channel, ob20, 2, stop.Token))[0]);
                var metadata = new[] { new byte[] { 0, 54, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 },
                    new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 },
                    new byte[] { 254, 0, 0, 6, 0 } };
                if (ob20) await wire.WriteAsync(Ob20Packet.Build(1234, 2, 1, metadata.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray()), stop.Token);
                else foreach (var packet in metadata) await channel.WritePacketAsync(packet, stop.Token);
                if (!ob20) channel.BeginCommand();
                Assert.Equal(162, (await ReadRequest(wire, channel, ob20, 3, stop.Token))[0]);
                var error = new byte[] { 255, 221, 7 }.Concat(Encoding.ASCII.GetBytes("#08S01 private server details")).ToArray();
                if (ob20) await wire.WriteAsync(Ob20Packet.Build(1234, 3, 1, Frame(1, error)), stop.Token);
                else await channel.WritePacketAsync(error, stop.Token);
                Assert.Equal(0, await wire.ReadAsync(new byte[1], stop.Token));
            });
            var context = new AssemblyLoadContext("fatal-piece-error-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20}";
                await connection.OpenAsync(stop.Token);
                using var source = new MemoryStream(new byte[] { 1, 2, 3 });
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:p)";
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = source; command.Parameters.Add(parameter);
                var error = await Assert.ThrowsAnyAsync<DbException>(() => asynchronous ? command.ExecuteNonQueryAsync(stop.Token) : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(2013, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error));
                Assert.Equal("08S01", error.GetType().GetProperty("SqlState")!.GetValue(error));
                Assert.Equal(false, error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                Assert.DoesNotContain("private", error.Message); Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(source.CanRead);
                connection.Close(); await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { stop.Cancel(); listener.Stop(); context.Unload(); }
        }

        public static IEnumerable<object[]> FatalServerErrorAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var mysql in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var code in new[] { 2013, 1927, 9001 })
                            yield return new object[] { target, mysql, asynchronous, code };
        }

        [Theory]
        [MemberData(nameof(FatalServerErrorAssets))]
        public async Task EveryAssetRetiresFatalErrAndExplicitlyReopensSingleSlotPool(string target, bool mysql, bool asynchronous, int code)
        {
            var state = code == 1927 ? "70100" : code == 2013 ? "08S01" : "08007";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync(stop.Token))
                {
                    var wire = first.GetStream(); var channel = new PacketChannel(wire);
                    await Handshake(channel, wire, "unrequested", stop.Token, mysql: mysql);
                    await Expect(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED");
                    await channel.WritePacketAsync(Report(false, 0), stop.Token); channel.BeginCommand();
                    await Expect(channel, "set autocommit = 0");
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0 }, stop.Token); channel.BeginCommand();
                    await Expect(channel, "failed target");
                    var text = Encoding.ASCII.GetBytes("#" + state + " private server details");
                    var packet = new byte[text.Length + 3]; packet[0] = 255; packet[1] = (byte)code; packet[2] = (byte)(code >> 8); text.CopyTo(packet, 3);
                    await channel.WritePacketAsync(packet, stop.Token);
                    Assert.Equal(0, await wire.ReadAsync(new byte[1], stop.Token));
                }
                using var next = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = next.GetStream(); var replacement = new PacketChannel(stream);
                await Handshake(replacement, stream, "unrequested", stop.Token, mysql: mysql);
                await Expect(replacement, "replacement is usable"); await replacement.WritePacketAsync(Report(false, 1), stop.Token);
                await ObserveClose(stream, stop.Token);
            });
            var context = new AssemblyLoadContext("fatal-server-error-" + target, isCollectible: true);
            DbConnection? connection = null; DbTransaction? transaction = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1",
                    ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u",
                    ["TLS Mode"] = "Disabled",
                    ["Pooling"] = true,
                    ["Max Pool Size"] = 1,
                    ["Pool Wait Timeout"] = 1,
                    ["Connection Timeout"] = 3,
                }.ConnectionString;
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                transaction = asynchronous ? await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, stop.Token) : connection.BeginTransaction(IsolationLevel.ReadCommitted);
                using var command = connection.CreateCommand(); command.CommandText = "failed target"; command.Transaction = transaction;
                var error = await Assert.ThrowsAnyAsync<DbException>(() => asynchronous ? command.ExecuteNonQueryAsync(stop.Token) : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(code, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error));
                Assert.Equal(state, error.GetType().GetProperty("SqlState")!.GetValue(error));
                Assert.Equal(true, error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                Assert.DoesNotContain("private", error.Message); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                if (asynchronous) { await connection.CloseAsync(); await connection.OpenAsync(stop.Token); }
                else { connection.Close(); connection.Open(); }
                transaction.Dispose(); command.Dispose(); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync(stop.Token) : after.ExecuteNonQuery());
                type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally
            {
                transaction?.Dispose(); connection?.Close();
                if (connection != null) connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                connection?.Dispose(); stop.Cancel(); listener.Stop(); context.Unload();
            }

            async Task Expect(PacketChannel channel, string sql)
            {
                var packet = mysql ? await channel.ReadPacketAsync(stop.Token) : await SessionTimeZoneFixture.ReadCommand(channel, stop.Token, expectProbe: false);
                Assert.Equal(3, packet[0]); Assert.Equal(sql, Encoding.UTF8.GetString(packet, 1, packet.Length - 1));
            }
        }
    }
}
