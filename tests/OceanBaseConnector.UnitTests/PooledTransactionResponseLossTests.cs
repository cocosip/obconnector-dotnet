using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> TransactionResponseLossRows()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var commit in new[] { false, true })
                        foreach (var partial in new[] { false, true })
                            yield return new object[] { oracle, asynchronous, commit, partial };
        }

        [Theory]
        [MemberData(nameof(TransactionResponseLossRows))]
        public async Task LostTransactionEndResponseDiscardsSessionAndLateCleanupPreservesReplacement(bool oracle, bool asynchronous, bool commit, bool partial)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync(stop.Token))
                {
                    var channel = await Handshake(first);
                    await TransactionBegin(channel, oracle ? "READ COMMITTED" : "REPEATABLE READ");
                    await Query(channel, commit ? "COMMIT" : "ROLLBACK");
                    if (partial)
                    {
                        var frame = ResponseFrame(Ok(status: 4));
                        await first.GetStream().WriteAsync(frame.AsMemory(0, 5), stop.Token);
                    }
                }
                using var next = await listener.AcceptTcpClientAsync(stop.Token);
                await ResourceReplacement(await Handshake(next), next, stop.Token);
            });
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            OceanBaseTransaction? transaction = null; Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var oldSession = connection.GetOpenSession();
                transaction = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(stop.Token) : (OceanBaseTransaction)connection.BeginTransaction();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous
                    ? commit ? transaction.CommitAsync(stop.Token) : transaction.RollbackAsync(stop.Token)
                    : RunSync(() => { if (commit) transaction.Commit(); else transaction.Rollback(); }));
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
                Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync(stop.Token));
                if (asynchronous) { await connection.CloseAsync(); await connection.OpenAsync(stop.Token); }
                else { connection.Close(); connection.Open(); }
                Assert.NotSame(oldSession, connection.GetOpenSession());
                if (asynchronous) await transaction.DisposeAsync(); else transaction.Dispose();
                using var command = connection.CreateCommand(); command.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(stop.Token) : command.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await FinishResourceTest(new Action[] { () => transaction?.Dispose(), connection.Close, () => OceanBaseConnection.ClearPool(connection), () => stop.Cancel(), listener.Stop }, serving, stop, failure);
            }

            async Task<PacketChannel> Handshake(TcpClient client)
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                await channel.ReadPacketAsync(stop.Token);
                await channel.WritePacketAsync(oracle ? TimeZoneOk("+00:00") : Ok(status: 2), stop.Token);
                await ServerIdentity.ReplyAsync(channel, oracle, token: stop.Token);
                return channel;
            }
        }
    }
}
