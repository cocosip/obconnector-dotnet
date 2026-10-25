using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionFailureErrRetiresTheCurrentSession(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "failed target");
                await channel.WritePacketAsync(ServerFailure(2013, "08S01"));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "failed target";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous
                    ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(2013, error.ServerErrorCode); Assert.Equal("08S01", error.SqlState);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> FatalServerErrorRows()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooled in new[] { false, true })
                        foreach (var code in new[] { 2013, 1927, 9001 })
                            yield return new object[] { oracle, asynchronous, pooled, code };
        }

        [Theory]
        [MemberData(nameof(FatalServerErrorRows))]
        public async Task FatalErrRequiresExplicitReopenAndRetiresOldTransaction(bool oracle, bool asynchronous, bool pooled, int code)
        {
            var state = code == 1927 ? "70100" : code == 2013 ? "08S01" : "08007";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync(stop.Token))
                {
                    var channel = await FatalErrorHandshake(first, oracle, stop.Token);
                    await TransactionBegin(channel, "READ COMMITTED");
                    await Query(channel, "failed target");
                    await channel.WritePacketAsync(ServerFailure(code, state), stop.Token);
                    Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], stop.Token));
                }
                using var next = await listener.AcceptTcpClientAsync(stop.Token);
                await ResourceReplacement(await FatalErrorHandshake(next, oracle, stop.Token), next, stop.Token);
            });
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)listener.LocalEndpoint).Port) +
                $";Pooling={pooled};Max Pool Size=1;Pool Wait Timeout=1");
            OceanBaseTransaction? transaction = null; Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var oldSession = connection.GetOpenSession();
                transaction = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, stop.Token)
                    : (OceanBaseTransaction)connection.BeginTransaction(IsolationLevel.ReadCommitted);
                using var command = connection.CreateCommand(); command.CommandText = "failed target"; command.Transaction = transaction;
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous
                    ? command.ExecuteNonQueryAsync(stop.Token) : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(code, error.ServerErrorCode); Assert.Equal(state, error.SqlState); Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.DoesNotContain("private", error.Message); Assert.DoesNotContain(command.CommandText, error.Message);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                Assert.Throws<InvalidOperationException>(() => connection.Open());
                if (asynchronous) { await connection.CloseAsync(); await connection.OpenAsync(stop.Token); }
                else { connection.Close(); connection.Open(); }
                Assert.NotSame(oldSession, connection.GetOpenSession());
                if (asynchronous) await transaction.DisposeAsync(); else transaction.Dispose();
                command.Dispose();
                using var replacement = connection.CreateCommand(); replacement.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await replacement.ExecuteNonQueryAsync(stop.Token) : replacement.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await FinishResourceTest(new Action[] { () => transaction?.Dispose(), connection.Close,
                    () => OceanBaseConnection.ClearPool(connection), () => stop.Cancel(), listener.Stop }, serving, stop, failure);
            }
        }

        [Theory]
        [InlineData(false, 1317, "70100")]
        [InlineData(true, 1317, "70100")]
        [InlineData(false, 1927, "HY000")]
        [InlineData(true, 1927, "HY000")]
        [InlineData(false, 1064, "42000")]
        [InlineData(true, 1064, "42000")]
        public async Task NonfatalErrKeepsSessionUsable(bool asynchronous, int code, string state)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "rejected target"); await channel.WritePacketAsync(ServerFailure(code, state));
                channel.BeginCommand(); await Query(channel, "after rejection"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "rejected target";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(code, error.ServerErrorCode); Assert.Equal(state, error.SqlState); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State); command.CommandText = "after rejection";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "prepare")]
        [InlineData(true, "prepare")]
        [InlineData(false, "commit")]
        [InlineData(true, "commit")]
        [InlineData(false, "rollback")]
        [InlineData(true, "rollback")]
        [InlineData(false, "database")]
        [InlineData(true, "database")]
        public async Task FatalErrOutcomeFollowsCurrentRequestSemantics(bool asynchronous, string operation)
        {
            async Task Reply(PacketChannel channel)
            {
                if (operation == "commit" || operation == "rollback") await TransactionBegin(channel);
                var request = await ReadClientCommand(channel);
                Assert.Equal(operation == "prepare" ? 22 : operation == "database" ? 2 : 3, request[0]);
                await channel.WritePacketAsync(ServerFailure(2013, "08S01"));
            }
            var server = operation == "database" ? LoopbackServer.Start(async client =>
                await Reply(await FatalErrorHandshake(client, false, CancellationToken.None))) : Server(Reply);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var transaction = operation == "commit" || operation == "rollback" ? await connection.BeginTransactionAsync() : null;
                using var command = connection.CreateCommand(); command.CommandText = "prepare target";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? operation switch
                {
                    "prepare" => command.PrepareAsync(),
                    "commit" => transaction!.CommitAsync(),
                    "rollback" => transaction!.RollbackAsync(),
                    _ => connection.ChangeDatabaseAsync("other")
                } : RunSync(() =>
                {
                    switch (operation)
                    {
                        case "prepare": command.Prepare(); break;
                        case "commit": transaction!.Commit(); break;
                        case "rollback": transaction!.Rollback(); break;
                        default: connection.ChangeDatabase("other"); break;
                    }
                }));
                Assert.Equal(operation != "prepare", error.IsExecutionOutcomeUnknown);
                Assert.Equal(2013, error.ServerErrorCode); Assert.Equal("08S01", error.SqlState);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task StreamingFatalErrRetiresSessionBeforeOrAfterFirstRow(bool asynchronous, bool row)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "stream target");
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("value")); await channel.WritePacketAsync(Eof());
                if (row) await channel.WritePacketAsync(new byte[] { 1, (byte)'7' });
                await channel.WritePacketAsync(ServerFailure(2013, "08S01"));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "stream target";
                OceanBaseException error;
                if (row)
                {
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(7, reader.GetInt32(0));
                    error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? reader.ReadAsync() : Task.FromResult(reader.Read()));
                    Assert.Throws<InvalidOperationException>(() => reader.Read());
                }
                else error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous
                    ? command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : Task.FromResult(command.ExecuteReader(CommandBehavior.SequentialAccess)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(2013, error.ServerErrorCode);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchFatalErrPreservesPrefixAndNeverSendsSuffix(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var prefix = BatchItem(batch, "prefix"); var failed = BatchItem(batch, "failed"); var suffix = BatchItem(batch, "never");
                var confirmed = TimeZoneOk("+00:00"); confirmed[1] = 2;
                using var responses = new MemoryStream(); responses.Write(ResponseFrame(confirmed)); responses.Write(ResponseFrame(ServerFailure(2013, "08S01")));
                var transport = new ProtocolFaultStream(responses.ToArray()); InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Same(failed, error.BatchCommand); Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(2013, error.ServerErrorCode);
                Assert.Equal(2, prefix.RecordsAffected); Assert.Equal(-1, failed.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                using var expected = new MemoryStream(); expected.Write(ResponseFrame(OceanBaseCommand.SqlPacket("prefix", 3), 0)); expected.Write(ResponseFrame(OceanBaseCommand.SqlPacket("failed", 3), 0));
                Assert.Equal(expected.ToArray(), transport.Written); Assert.True(transport.Disposed); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task FatalErrTrustRequiresCompleteFramingBoundary(bool asynchronous, bool ob20, bool trailing)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var inner = ResponseFrame(ServerFailure(2013, "08S01"));
                if (trailing) inner = inner.Concat(new byte[] { 42 }).ToArray();
                var bytes = ob20 ? Ob20Packet.Build(7, 0, 1, inner) : Protocol.MySqlCompressionTests.Block(1, inner);
                var transport = new ProtocolFaultStream(bytes); InstallFaultStream(connection, transport);
                using (var lease = connection.GetOpenSession().Acquire())
                {
                    if (ob20) lease.Channel.EnableOb20(7); else lease.Channel.EnableCompression();
                    lease.Complete();
                }
                using var command = connection.CreateCommand(); command.CommandText = "framed target";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(transport.Disposed);
                Assert.Equal(trailing ? 0 : 2013, error.ServerErrorCode);
                Assert.Equal(trailing ? null : "08S01", error.SqlState);
                if (trailing) Assert.IsType<ProtocolException>(error.InnerException);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FatalMetadataErrDoesNotMakeConfirmedParentOutcomeUnknown(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "confirmed target"); await channel.WritePacketAsync(Ok(1));
                channel.BeginCommand(); await Query(channel, "metadata lookup"); await channel.WritePacketAsync(ServerFailure(2013, "08S01"));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var releases = 0;
                using var exchange = new CommandExchange(connection.GetOpenSession(), new ConnectionLog(null), 5, CancellationToken.None, () => releases++);
                if (asynchronous) await exchange.SendAsync(OceanBaseCommand.SqlPacket("confirmed target", 3)); else exchange.Send(OceanBaseCommand.SqlPacket("confirmed target", 3));
                Assert.Equal(1, OkPacket.Parse(asynchronous ? await exchange.ReceiveAsync(CancellationToken.None) : exchange.Receive(), exchange.Capabilities).AffectedRows);
                exchange.ResponseComplete(); Assert.True(exchange.HasSentSql);
                if (asynchronous) await exchange.SendAsync(OceanBaseCommand.SqlPacket("metadata lookup", 3), executesSql: false);
                else exchange.Send(OceanBaseCommand.SqlPacket("metadata lookup", 3), executesSql: false);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? exchange.ReceiveAsync(CancellationToken.None) : Task.FromResult(exchange.Receive()));
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(2013, error.ServerErrorCode);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(1, releases);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FatalReaderErrRefreshesDiscoveryOnlyOnExplicitReopen(bool asynchronous)
        {
            var first = Server(async channel =>
            {
                await Query(channel, "old reader"); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("value")); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, (byte)'7' }); await channel.WritePacketAsync(ServerFailure(2013, "08S01"));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            var second = Server(async channel => { await Query(channel, "replacement is usable"); await channel.WritePacketAsync(Ok(1)); Assert.Equal(0, await channelStreamRead(channel)); });
            var resolver = new AddressResolver();
            string Resolve() => "127.0.0.1:" + (resolver.Calls == 1 ? first.Port : second.Port);
            if (asynchronous) resolver.Async = _ => Task.FromResult(Resolve()); else resolver.Sync = _ => Resolve();
            using var connection = new OceanBaseConnection(Configuration(first.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1") { AddressResolver = resolver };
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "old reader";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess)
                    : command.ExecuteReader(CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? reader.ReadAsync() : Task.FromResult(reader.Read()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(1, resolver.Calls);
                if (asynchronous) { await connection.CloseAsync(); await connection.OpenAsync(); }
                else { connection.Close(); connection.Open(); }
                Assert.Equal(2, resolver.Calls);
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                command.Dispose(); Assert.Equal(ConnectionState.Open, connection.State);
                using var replacement = connection.CreateCommand(); replacement.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await replacement.ExecuteNonQueryAsync() : replacement.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection); connection.Close();
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FatalPieceAcknowledgementPreservesTrustedErrorBeforeExecution(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 54, 1);
                Assert.Equal(162, (await StatementCommand(channel, 162))[0]);
                await channel.WritePacketAsync(ServerFailure(2013, "08S01"));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var source = new MemoryStream(new byte[] { 1, 2, 3 });
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(2013, error.ServerErrorCode); Assert.Equal("08S01", error.SqlState); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(source.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task FullyReceivedFatalErrKeepsIdentityWhenCancellationClosesBareTransport(bool asynchronous, bool callerToken)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cancelled target";
                var transport = new ProtocolFaultStream(ResponseFrame(ServerFailure(2013, "08S01")))
                { AfterInputConsumed = () => { if (callerToken) cancellation.Cancel(); else command.Cancel(); } };
                InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(cancellation.Token) : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(2013, error.ServerErrorCode); Assert.Equal("08S01", error.SqlState); Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(transport.Disposed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CancelledFramedFatalErrRequiresBoundaryEvidence(bool asynchronous, bool ob20)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "framed cancelled target";
                var packet = ResponseFrame(ServerFailure(2013, "08S01"));
                var transport = new ProtocolFaultStream(ob20 ? Ob20Packet.Build(7, 0, 1, packet) : Protocol.MySqlCompressionTests.Block(1, packet))
                { AfterInputConsumed = command.Cancel };
                InstallFaultStream(connection, transport);
                using (var lease = connection.GetOpenSession().Acquire())
                {
                    if (ob20) lease.Channel.EnableOb20(7); else lease.Channel.EnableCompression(); lease.Complete();
                }
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(0, error.ServerErrorCode); Assert.Null(error.SqlState);
                Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(transport.Disposed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task<PacketChannel> FatalErrorHandshake(TcpClient client, bool oracle, CancellationToken token)
        {
            var channel = new PacketChannel(client.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, token);
            await channel.ReadPacketAsync(token);
            await channel.WritePacketAsync(oracle ? TimeZoneOk("+00:00") : Ok(status: 2), token);
            await ServerIdentity.ReplyAsync(channel, oracle, token: token);
            return channel;
        }

        private static byte[] ServerFailure(int code, string state)
        {
            var suffix = Encoding.ASCII.GetBytes("#" + state + " private server details");
            var packet = new byte[3 + suffix.Length];
            packet[0] = 255; packet[1] = (byte)code; packet[2] = (byte)(code >> 8);
            suffix.CopyTo(packet, 3); return packet;
        }
    }
}
