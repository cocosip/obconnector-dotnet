using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CompletedCloseConnectionReaderCannotCloseAReusedLogicalLease(bool oracle, bool asynchronous)
            => await VerifyPooledReaderClose(oracle, asynchronous, batchReader: false);

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task BatchCloseCannotCloseALeaseReopenedAfterReservationRelease(bool oracle, bool asynchronous)
            => await VerifyPooledReaderClose(oracle, asynchronous, batchReader: true);

        private static async Task VerifyPooledReaderClose(bool oracle, bool asynchronous, bool batchReader)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                await channel.ReadPacketAsync(stop.Token);
                await channel.WritePacketAsync(oracle ? TimeZoneOk("+00:00") : Ok(status: 2), stop.Token);
                await ServerIdentity.ReplyAsync(channel, oracle, token: stop.Token);
                await Query(channel, "old reader");
                await channel.WritePacketAsync(new byte[] { 1 }, stop.Token);
                await channel.WritePacketAsync(Column("value", 3), stop.Token);
                await channel.WritePacketAsync(Eof(2), stop.Token);
                await channel.WritePacketAsync(new byte[] { 1, 49 }, stop.Token);
                await channel.WritePacketAsync(Eof(2), stop.Token);
                channel.BeginCommand();
                Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(stop.Token));
                await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                if (oracle) await ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                channel.BeginCommand();
                Assert.Equal(new byte[] { 14 }, await channel.ReadPacketAsync(stop.Token));
                await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                channel.BeginCommand();
                await Query(channel, "new lease survives");
                await channel.WritePacketAsync(Ok(1, 2), stop.Token);
                Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            DbDataReader? reader = null;
            Task? closing = null;
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var session = connection.GetOpenSession(); var generation = session.Generation;
                using var command = connection.CreateCommand(); command.CommandText = "old reader";
                using var batch = connection.CreateBatch(); BatchItem(batch, "old reader");
                reader = batchReader
                    ? asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection, stop.Token) : batch.ExecuteReader(CommandBehavior.CloseConnection)
                    : asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, stop.Token) : command.ExecuteReader(CommandBehavior.CloseConnection);
                Assert.True(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read());
                Assert.False(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync(stop.Token) : reader.NextResult());
                if (batchReader)
                {
                    var gate = typeof(OceanBaseConnection).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    lock (gate)
                    {
                        closing = Task.Run(async () => { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); });
                        Assert.True(SpinWait.SpinUntil(() => !session.IsReservedForOther(null), TimeSpan.FromSeconds(2)), "The old batch must release its reservation before closing the logical connection.");
                        connection.Close(); connection.Open();
                        Assert.Same(session, connection.GetOpenSession()); Assert.True(session.Generation > generation);
                    }
                    await closing.WaitAsync(stop.Token);
                }
                else
                {
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                    if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                    Assert.Same(session, connection.GetOpenSession()); Assert.True(session.Generation > generation);
                    if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                }
                Assert.Equal(ConnectionState.Open, connection.State);
                using var next = connection.CreateCommand(); next.CommandText = "new lease survives";
                Assert.Equal(1, asynchronous ? await next.ExecuteNonQueryAsync(stop.Token) : next.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await server.Completion.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var cleanupErrors = new List<Exception>();
                foreach (var cleanup in new Action[] { () => reader?.Dispose(), connection.Close, () => OceanBaseConnection.ClearPool(connection), () => stop.Cancel(), server.Stop })
                {
                    try { cleanup(); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                foreach (var task in closing == null ? new[] { server.Completion } : new[] { closing, server.Completion })
                {
                    try { await task; }
                    catch (OperationCanceledException) when (failure != null && stop.IsCancellationRequested) { }
                    catch (SocketException) when (failure != null && stop.IsCancellationRequested) { }
                    catch (ObjectDisposedException) when (failure != null && stop.IsCancellationRequested) { }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                if (cleanupErrors.Count != 0)
                {
                    if (failure != null) cleanupErrors.Insert(0, failure);
                    throw new AggregateException(cleanupErrors);
                }
            }
        }
    }
}
