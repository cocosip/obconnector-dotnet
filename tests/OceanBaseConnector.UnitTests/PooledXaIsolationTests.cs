using System;
using System.Collections.Generic;
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
        public static IEnumerable<object[]> PooledXaPhases()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var phase in new[] { "idle", "active", "ended", "suspended", "prepared", "committed", "rolledback" })
                        yield return new object[] { oracle, asynchronous, phase };
        }

        [Theory]
        [MemberData(nameof(PooledXaPhases))]
        public async Task DedicatedXaSessionIsDestroyedInEveryPhaseBeforePoolReplacement(bool oracle, bool asynchronous, string phase)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serving = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync(stop.Token))
                {
                    var channel = await Handshake(first);
                    if (phase != "idle")
                    {
                        if (oracle) await OracleXaInitialize(channel);
                        await Stage(channel, "START", 0, previous: false);
                        if (phase != "active") await Stage(channel, "END", phase == "suspended" ? 0x02000000 : 0x04000000);
                        if (phase == "prepared") await Stage(channel, "PREPARE");
                        if (phase == "committed") await Stage(channel, "COMMIT", 1);
                        if (phase == "rolledback") await Stage(channel, "ROLLBACK");
                    }
                    // Any reset or implicit XA completion is a contract violation, not a new response to fake.
                    Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], stop.Token));
                }
                using var second = await listener.AcceptTcpClientAsync(stop.Token);
                var replacement = await Handshake(second);
                await Query(replacement, "replacement is usable");
                await replacement.WritePacketAsync(Ok(1, oracle ? (byte)6 : (byte)2), stop.Token);
                Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)listener.LocalEndpoint).Port) +
                ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1;Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var oldSession = connection.GetOpenSession();
                var old = connection.GetXaResource();
                if (phase != "idle")
                {
                    await XaStart(old, asynchronous);
                    if (phase != "active") await XaEnd(old, asynchronous, phase == "suspended" ? OceanBaseXaEndMode.Suspend : OceanBaseXaEndMode.Success);
                    if (phase == "prepared") Assert.Equal(OceanBaseXaVote.Prepared, asynchronous ? await old.PrepareAsync(XaId) : old.Prepare(XaId));
                    if (phase == "committed") await XaCommit(old, asynchronous, onePhase: true);
                    if (phase == "rolledback") await XaRollback(old, asynchronous);
                }
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                Assert.NotSame(oldSession, connection.GetOpenSession());
                Assert.Throws<InvalidOperationException>(() => old.Start(XaId));
                Assert.Throws<InvalidOperationException>(() => old.End(XaId));
                Assert.Throws<InvalidOperationException>(() => old.Prepare(XaId));
                Assert.Throws<InvalidOperationException>(() => old.Commit(XaId));
                Assert.Throws<InvalidOperationException>(() => old.Rollback(XaId));
                Assert.Throws<InvalidOperationException>(() => old.Recover());
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.StartAsync(XaId));
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.EndAsync(XaId));
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.PrepareAsync(XaId));
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.CommitAsync(XaId));
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.RollbackAsync(XaId));
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.RecoverAsync());
                using var command = connection.CreateCommand();
                command.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(stop.Token) : command.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); OceanBaseConnection.ClearPool(connection); stop.Cancel(); listener.Stop();
                try { await serving; }
                catch (OperationCanceledException) when (failure != null && stop.IsCancellationRequested) { }
                catch (SocketException) when (failure != null && stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (failure != null && stop.IsCancellationRequested) { }
                catch (Exception error) when (failure != null) { throw new AggregateException(failure, error); }
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

            async Task Stage(PacketChannel channel, string operation, int? flag = null, bool previous = true)
            {
                if (oracle)
                {
                    await OracleXaRequest(channel, operation, flag, previous);
                    await OracleXaScalarReply(channel, flag.HasValue ? 2 : 1);
                }
                else
                {
                    var suffix = operation == "END" && phase == "suspended" ? " SUSPEND" : operation == "COMMIT" ? " ONE PHASE" : "";
                    channel.BeginCommand();
                    await XaReply(channel, "XA " + operation + " " + XaIdentity + suffix);
                }
            }
        }
    }
}
