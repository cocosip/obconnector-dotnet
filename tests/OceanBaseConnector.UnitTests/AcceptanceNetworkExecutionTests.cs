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
    public static IEnumerable<object[]> NetworkAcceptanceRows()
    {
        foreach (var oracle in new[] { false, true })
            foreach (var asynchronous in new[] { false, true })
                foreach (var scenario in new[] { "insert.before-forward", "insert.partial-request", "insert.whole-response", "insert.partial-response",
                    "commit.whole-response", "commit.partial-response", "rollback.whole-response", "rollback.partial-response" })
                    yield return new object[] { oracle, asynchronous, scenario, false };
        yield return new object[] { false, true, "insert.whole-response", true };
        yield return new object[] { true, false, "commit.partial-response", true };
    }

    [Theory]
    [MemberData(nameof(NetworkAcceptanceRows))]
    public async Task NetworkAcceptanceChecksReconciliationAndCleansOwnedTable(bool oracle, bool asynchronous, string scenario, bool corruptCount)
    {
        var test = Assert.Single(DatabaseCases.All(), c => c.Name == "matrix.network-outcome-unknown." + scenario + (asynchronous ? ".async" : ".sync"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var sessions = new List<Task>(); var committed = 0; var creates = 0; var drops = 0; var writes = 0;
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    sessions.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var config = AcceptanceConfig.Parse("{\"mode\":\"" + (oracle ? "Oracle" : "MySql") + "\",\"connectionString\":\"unused\",\"allowCreateObjects\":true,\"networkFaultProfile\":true}");
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                UserId = "synthetic",
                Password = "synthetic-private",
                TlsMode = OceanBaseTlsMode.Disabled,
                CommandTimeout = 3,
                ConnectionTimeout = 3,
            };
            var output = new StringWriter();
            var report = await AcceptanceSuite.RunAsync(config, new[] { test }, output, stop.Token, builder.ConnectionString);
            var result = Assert.Single(report.Results);
            Assert.True(result.Status == (corruptCount ? "failed" : "passed"), result.Detail);
            if (corruptCount) Assert.Contains("Reconciliation", result.Detail);
            Assert.Equal(1, creates); Assert.Equal(1, drops);
            Assert.Equal("removed", Assert.Single(report.Objects).Cleanup);
            if (!corruptCount)
                Assert.Equal(scenario is "insert.before-forward" or "insert.partial-request" ? 2 : 3, writes);
            var serialized = JsonSerializer.Serialize(report) + output;
            Assert.DoesNotContain("synthetic-private", serialized);
            Assert.DoesNotContain("INSERT INTO", serialized);
            Assert.DoesNotContain("127.0.0.1", serialized);
        }
        finally
        {
            stop.Cancel();
            await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            listener.Stop();
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                var pending = 0; var auto = true;
                var channel = new PacketChannel(client.GetStream());
                try
                {
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                    await channel.ReadPacketAsync(stop.Token);
                    await channel.WritePacketAsync(oracle ? TimeZoneOk("+00:00") : Ok(status: 2), stop.Token);
                    await ServerIdentity.ReplyAsync(channel, oracle, token: stop.Token);
                    while (true)
                    {
                        channel.BeginCommand();
                        var packet = await channel.ReadPacketAsync(stop.Token);
                        if (packet[0] == 1) return;
                        Assert.Equal(3, packet[0]);
                        var sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1);
                        if (sql == SessionTimeZoneSql) { await TimeZoneReply(channel); continue; }
                        if (sql == NationalCharacterSetSql) { await NationalCharacterSetReply(channel); continue; }
                        if (sql.StartsWith("SELECT COUNT(*) FROM OBA_", StringComparison.Ordinal))
                        {
                            await channel.WritePacketAsync(new byte[] { 1 }, stop.Token);
                            await channel.WritePacketAsync(Column("n", 8), stop.Token);
                            await channel.WritePacketAsync(Eof(), stop.Token);
                            var number = Volatile.Read(ref committed);
                            var digits = Encoding.ASCII.GetBytes((number + (corruptCount && number > 0 ? 10 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            await channel.WritePacketAsync(new[] { (byte)digits.Length }.Concat(digits).ToArray(), stop.Token);
                            await channel.WritePacketAsync(Eof(), stop.Token);
                            continue;
                        }
                        byte affected = 0;
                        if (sql.StartsWith("CREATE TABLE OBA_", StringComparison.Ordinal)) Interlocked.Increment(ref creates);
                        else if (sql.StartsWith("DROP TABLE OBA_", StringComparison.Ordinal)) Interlocked.Increment(ref drops);
                        else if (sql.StartsWith("INSERT INTO OBA_", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref writes); affected = 1;
                            if (auto) Interlocked.Increment(ref committed); else pending++;
                        }
                        else if (sql == "set autocommit = 0") auto = false;
                        else if (sql == "set autocommit = 1") auto = true;
                        else if (sql == "COMMIT") { Interlocked.Add(ref committed, pending); pending = 0; }
                        else if (sql == "ROLLBACK") pending = 0;
                        else Assert.StartsWith("SET SESSION TRANSACTION ISOLATION LEVEL ", sql);
                        await channel.WritePacketAsync(Ok(affected, (byte)(auto ? 6 : pending == 0 ? 4 : 5)), stop.Token);
                    }
                }
                catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }
}
