using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("+00:00", "+05:45")]
        [InlineData("+05:45", "+06:45")]
        public async Task AcceptanceTimeZoneIsolationUsesOffsetsWithoutRegionFiles(string baseline, string changed)
        {
            var test = Assert.Single(DatabaseCases.All(), c => c.Name == "matrix.pool-reset");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync(stop.Token))
                {
                    first.NoDelay = true;
                    if (!await Session(await ResourceHandshake(first, stop.Token), expectsChange: true)) return;
                }
                using var second = await listener.AcceptTcpClientAsync(stop.Token);
                second.NoDelay = true;
                await Session(await ResourceHandshake(second, stop.Token), expectsChange: false);
            });
            try
            {
                var config = AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionString\":\"unused\"}");
                var report = await AcceptanceSuite.RunAsync(config, new[] { test }, TextWriter.Null, stop.Token,
                    Configuration(((IPEndPoint)listener.LocalEndpoint).Port));
                Assert.Equal("passed", Assert.Single(report.Results).Status);
                await serving.WaitAsync(stop.Token);
            }
            finally
            {
                stop.Cancel(); listener.Stop();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }

            async Task<bool> Session(PacketChannel channel, bool expectsChange)
            {
                var zone = baseline; var applied = false;
                try
                {
                    while (true)
                    {
                        channel.BeginCommand();
                        var request = await channel.ReadPacketAsync(stop.Token);
                        if (request[0] == 31)
                        {
                            Assert.False(expectsChange, "Explicit state changes must retire the first session.");
                            await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                            await ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                            continue;
                        }
                        Assert.Equal(3, request[0]);
                        var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                        if (sql == SessionTimeZoneSql) await TimeZoneReply(channel, zone);
                        else if (sql == NationalCharacterSetSql) await NationalCharacterSetReply(channel);
                        else if (sql == "ALTER SESSION SET TIME_ZONE = 'UTC'")
                            await channel.WritePacketAsync(new byte[] { 255, 90, 7, 35, 72, 89, 48, 48, 48 }, stop.Token);
                        else
                        {
                            Assert.True(expectsChange);
                            Assert.Equal("ALTER SESSION SET TIME_ZONE = '" + changed + "'", sql);
                            zone = changed; applied = true;
                            await channel.WritePacketAsync(Ok(), stop.Token);
                        }
                    }
                }
                catch (ProtocolException e) when (e.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { return applied; }
            }
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public async Task AcceptanceRequiresResetReuseRatherThanSuccessfulReconnection(bool oracle, bool asynchronous, bool rejectReset)
        {
            var test = Assert.Single(DatabaseCases.All(), c => c.Name == "matrix.pool-reset-reuse." + (asynchronous ? "async" : "sync"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var accepted = 0; var prepares = 0; var resets = 0; var pings = 0;
            var serving = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        client.NoDelay = true;
                        accepted++;
                        var channel = new PacketChannel(client.GetStream());
                        var sessionOk = oracle ? SessionVariablesOk(new NationalCharacterSet("utf8mb4", "utf8mb4", "utf8mb4").Variables("+00:00")) : Ok(status: 2);
                        await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                        await channel.ReadPacketAsync(stop.Token);
                        await channel.WritePacketAsync(sessionOk, stop.Token);
                        await ServerIdentity.ReplyAsync(channel, oracle, token: stop.Token);
                        try
                        {
                            while (true)
                            {
                                channel.BeginCommand();
                                var packet = await ReadClientCommand(channel);
                                switch (packet[0])
                                {
                                    case 22:
                                        Assert.Equal(oracle ? "SELECT 1 FROM DUAL" : "SELECT 1", Encoding.UTF8.GetString(packet, 1, packet.Length - 1));
                                        await Prepared(channel, (byte)++prepares, 0, 1);
                                        break;
                                    case 23:
                                        Assert.Equal(prepares, packet[1]);
                                        await Scalar(channel, binary: true);
                                        break;
                                    case 3:
                                        Assert.Equal(oracle ? "SELECT 1 FROM DUAL" : "SELECT 1", Encoding.UTF8.GetString(packet, 1, packet.Length - 1));
                                        await Scalar(channel, binary: false);
                                        break;
                                    case 31:
                                        resets++;
                                        await channel.WritePacketAsync(rejectReset && resets == 1 ? Error() : Ok(status: 2), stop.Token);
                                        if (oracle && !(rejectReset && resets == 1))
                                        {
                                            channel.BeginCommand();
                                            await Query(channel, "set autocommit = 1");
                                            await channel.WritePacketAsync(sessionOk, stop.Token);
                                        }
                                        break;
                                    case 14:
                                        pings++; await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                                        break;
                                    case 25:
                                        Assert.Equal(prepares, packet[1]);
                                        break;
                                    default: throw new Xunit.Sdk.XunitException("Unexpected command in pool acceptance fixture.");
                                }
                            }
                        }
                        catch (ProtocolException e) when (e.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                        catch (IOException) { }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                catch (SocketException e) when (stop.IsCancellationRequested && e.SocketErrorCode == SocketError.OperationAborted) { }
            });
            try
            {
                var config = AcceptanceConfig.Parse("{\"mode\":\"" + (oracle ? "Oracle" : "MySql") + "\",\"connectionString\":\"unused\"}");
                var connectionString = Configuration(((IPEndPoint)listener.LocalEndpoint).Port);
                var report = await AcceptanceSuite.RunAsync(config, new[] { test }, TextWriter.Null, stop.Token, connectionString);
                var result = Assert.Single(report.Results);
                Assert.Equal(rejectReset ? "failed" : "passed", result.Status);
                if (rejectReset) Assert.Contains("same physical channel", result.Detail);
                Assert.Equal(rejectReset ? 2 : 1, Volatile.Read(ref accepted));
                Assert.Equal(rejectReset ? 1 : 2, Volatile.Read(ref prepares));
                Assert.Equal(rejectReset ? 2 : 3, Volatile.Read(ref resets));
                Assert.Equal(rejectReset ? 0 : 2, Volatile.Read(ref pings));
                Assert.Empty(report.Objects);
            }
            finally
            {
                stop.Cancel();
                await serving.WaitAsync(TimeSpan.FromSeconds(5));
                listener.Stop();
            }

            async Task Scalar(PacketChannel channel, bool binary)
            {
                await channel.WritePacketAsync(new byte[] { 1 }, stop.Token);
                await channel.WritePacketAsync(Column("value", 8), stop.Token);
                await channel.WritePacketAsync(Eof(), stop.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 } : new byte[] { 1, 49 }, stop.Token);
                await channel.WritePacketAsync(Eof(), stop.Token);
            }
        }
    }
}
