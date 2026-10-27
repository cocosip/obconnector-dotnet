using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed partial class TextCommandTests
{
    public static IEnumerable<object[]> TopologyAcceptanceRows()
    {
        foreach (var oracle in new[] { false, true })
            foreach (var asynchronous in new[] { false, true })
                foreach (var scenario in new[] { "idle-refresh", "borrowed-isolation", "tcp-fallback" })
                    yield return new object[] { oracle, asynchronous, scenario, false, false };
        yield return new object[] { false, true, "idle-refresh", true, false };
        yield return new object[] { true, false, "borrowed-isolation", true, false };
        yield return new object[] { false, true, "idle-refresh", false, true };
    }

    [Theory]
    [MemberData(nameof(TopologyAcceptanceRows))]
    public async Task TopologyAcceptanceExercisesRoutesAndCleansConnections(bool oracle, bool asynchronous, string scenario, bool wrongResult, bool cancelQuery)
    {
        var test = Assert.Single(DatabaseCases.All(), c => c.Name == "matrix.topology-failover." + scenario + (asynchronous ? ".async" : ".sync"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var sessions = new List<Task>(); var accepted = 0; var closed = 0; var queries = 0;
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepted); sessions.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var config = AcceptanceConfig.Parse("{\"mode\":\"" + (oracle ? "Oracle" : "MySql") + "\",\"connectionString\":\"unused\",\"topologyFaultProfile\":true}");
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                UserId = "synthetic",
                Password = "synthetic-private",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
                CommandTimeout = 3,
            };
            var output = new StringWriter();
            var report = await AcceptanceSuite.RunAsync(config, new[] { test }, output, caller.Token, builder.ConnectionString);
            var result = Assert.Single(report.Results);
            Assert.True(result.Status == (cancelQuery ? "cancelled" : wrongResult ? "failed" : "passed"), result.Detail);
            Assert.Empty(report.Objects);
            if (!wrongResult && !cancelQuery) Assert.Equal(scenario == "tcp-fallback" ? 1 : scenario == "borrowed-isolation" ? 3 : 2, Volatile.Read(ref accepted));
            var serialized = JsonSerializer.Serialize(report) + output;
            Assert.DoesNotContain("synthetic-private", serialized); Assert.DoesNotContain("127.0.0.1", serialized);
            Assert.DoesNotContain(builder.ConnectionString, serialized);
        }
        finally
        {
            // Allow peer EOF to finish before fixture cancellation can conceal a leaked relay.
            await Task.WhenAll(sessions.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel(); await accepting.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(accepted, closed);
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                var channel = new PacketChannel(client.GetStream());
                try
                {
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                    await channel.ReadPacketAsync(stop.Token);
                    await channel.WritePacketAsync(oracle ? TimeZoneOk("+00:00") : Ok(status: 2), stop.Token);
                    await ServerIdentity.ReplyAsync(channel, oracle, token: stop.Token);
                    while (true)
                    {
                        channel.BeginCommand(); var packet = await channel.ReadPacketAsync(stop.Token);
                        if (packet[0] == 1) return;
                        if (packet[0] is 0x1f or 0x0e) { await channel.WritePacketAsync(Ok(status: 2), stop.Token); continue; }
                        Assert.Equal(3, packet[0]);
                        var sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1);
                        if (sql == SessionTimeZoneSql) { await TimeZoneReply(channel); continue; }
                        if (sql == NationalCharacterSetSql) { await NationalCharacterSetReply(channel); continue; }
                        if (sql == "set autocommit = 1") { await channel.WritePacketAsync(Ok(status: 2), stop.Token); continue; }
                        Assert.Equal(oracle ? "SELECT 1 FROM DUAL" : "SELECT 1", sql);
                        Interlocked.Increment(ref queries);
                        if (cancelQuery)
                        {
                            caller.Cancel();
                            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], stop.Token));
                            return;
                        }
                        await channel.WritePacketAsync(new byte[] { 1 }, stop.Token);
                        await channel.WritePacketAsync(Column("n", 8), stop.Token);
                        await channel.WritePacketAsync(Eof(), stop.Token);
                        await channel.WritePacketAsync(new byte[] { 1, (byte)(wrongResult ? '0' : '1') }, stop.Token);
                        await channel.WritePacketAsync(Eof(), stop.Token);
                    }
                }
                catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                finally { Interlocked.Increment(ref closed); }
            }
        }
    }
}
