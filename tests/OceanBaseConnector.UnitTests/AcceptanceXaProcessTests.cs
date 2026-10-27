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
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, false, false, true)]
    public async Task XaCoordinatorRecoversAfterProcessDeathWithoutTouchingOtherBranches(bool asynchronous, bool commit, bool hidePreparedBranch, bool cancelDuringWrite, bool failCompletion)
    {
        var test = Assert.Single(DatabaseCases.All(), c => c.Name == "matrix.xa-crash-recovery." + (commit ? "commit." : "rollback.") + (asynchronous ? "async" : "sync"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var sessions = new List<Task>(); var creates = 0; var drops = 0; var writes = 0; var committed = 0;
        var prepared = false; string? identity = null; byte[]? global = null; var branchStarts = 0; var completions = 0;
        var accepting = Task.Run(async () =>
        {
            try { while (true) sessions.Add(Serve(await listener.AcceptTcpClientAsync(stop.Token))); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"unused\",\"xaCrashProfile\":true,\"allowCreateObjects\":true}");
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                UserId = "synthetic",
                Password = "synthetic-private",
                TlsMode = OceanBaseTlsMode.Disabled,
                ExpectedMode = OceanBaseCompatibilityMode.MySql,
                ConnectionTimeout = 3,
                CommandTimeout = 3,
            };
            var output = new StringWriter();
            var report = await AcceptanceSuite.RunAsync(config, new[] { test }, output, runCancellation.Token, builder.ConnectionString);
            var result = Assert.Single(report.Results);
            Assert.Equal(cancelDuringWrite ? "cancelled" : hidePreparedBranch || failCompletion ? "failed" : "passed", result.Status);
            if (hidePreparedBranch || failCompletion) Assert.Contains("worker", result.Detail);
            Assert.Equal(1, creates); Assert.Equal(1, drops); Assert.Equal(1, writes); Assert.Equal(1, branchStarts);
            Assert.Equal(hidePreparedBranch || cancelDuringWrite || failCompletion ? 0 : 1, completions);
            if (failCompletion)
            {
                Assert.Equal(2, report.CleanupFailures);
                Assert.Contains("retained local decision journal", result.Detail);
                var owned = Assert.Single(report.Objects, o => o.Kind == "XA").Name;
                var retained = Assert.Single(Directory.GetDirectories(Path.GetTempPath(), "ob-xa-" + owned + "-*"));
                var journal = Path.Combine(retained, "decision.json");
                try { Assert.True(XaCrashWorker.ReadDecision(journal).Commit); }
                finally { File.Delete(journal); Directory.Delete(retained); }
            }
            else Assert.All(report.Objects, o => Assert.Equal("removed", o.Cleanup));
            var serialized = JsonSerializer.Serialize(report) + output;
            Assert.DoesNotContain("synthetic-private", serialized); Assert.DoesNotContain("INSERT INTO", serialized);
            Assert.DoesNotContain("127.0.0.1", serialized); Assert.DoesNotContain(Path.GetTempPath(), serialized);
        }
        finally
        {
            stop.Cancel(); await accepting.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(5));
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
                    await channel.ReadPacketAsync(stop.Token); await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                    await ServerIdentity.ReplyAsync(channel, oracle: false, token: stop.Token);
                    while (true)
                    {
                        channel.BeginCommand(); var packet = await channel.ReadPacketAsync(stop.Token);
                        if (packet[0] == 1) return;
                        Assert.Equal(3, packet[0]); var sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1);
                        if (sql == "XA RECOVER")
                        {
                            await channel.WritePacketAsync(new byte[] { 4 }, stop.Token);
                            await channel.WritePacketAsync(Column("formatID", 8), stop.Token);
                            await channel.WritePacketAsync(Column("gtrid_length", 3), stop.Token);
                            await channel.WritePacketAsync(Column("bqual_length", 3), stop.Token);
                            await channel.WritePacketAsync(Column("data", 252, charset: 63, flags: 128), stop.Token);
                            await channel.WritePacketAsync(Eof(2), stop.Token);
                            // An unrelated real-looking prepared branch must never be completed by the harness.
                            await channel.WritePacketAsync(XaRecoveryRow("7", "1", "0", new byte[] { 9 }), stop.Token);
                            if (Volatile.Read(ref prepared) && !hidePreparedBranch)
                                await channel.WritePacketAsync(XaRecoveryRow("5194307", "16", "1", global!.Concat(new byte[] { 1 }).ToArray()), stop.Token);
                            await channel.WritePacketAsync(Eof(2), stop.Token); continue;
                        }
                        if (sql.StartsWith("SELECT COUNT(*) FROM OBA_", StringComparison.Ordinal))
                        {
                            await channel.WritePacketAsync(new byte[] { 1 }, stop.Token);
                            await channel.WritePacketAsync(Column("n", 8), stop.Token);
                            await channel.WritePacketAsync(Eof(2), stop.Token);
                            await channel.WritePacketAsync(new byte[] { 1, (byte)('0' + Volatile.Read(ref committed)) }, stop.Token);
                            await channel.WritePacketAsync(Eof(2), stop.Token); continue;
                        }
                        byte affected = 0;
                        if (sql.StartsWith("CREATE TABLE OBA_", StringComparison.Ordinal)) Interlocked.Increment(ref creates);
                        else if (sql.StartsWith("DROP TABLE OBA_", StringComparison.Ordinal)) Interlocked.Increment(ref drops);
                        else if (sql.StartsWith("INSERT INTO OBA_", StringComparison.Ordinal))
                        {
                            affected = 1; Interlocked.Increment(ref writes);
                            if (cancelDuringWrite)
                            {
                                runCancellation.Cancel();
                                Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], stop.Token));
                                return;
                            }
                        }
                        else if (sql.StartsWith("XA START ", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref branchStarts); identity = sql.Substring(9);
                            global = Convert.FromHexString(identity.Split(',')[0].Substring(2));
                        }
                        else if (sql.StartsWith("XA END ", StringComparison.Ordinal)) Assert.Equal("XA END " + identity, sql);
                        else if (sql.StartsWith("XA PREPARE ", StringComparison.Ordinal))
                        { Assert.Equal("XA PREPARE " + identity, sql); Volatile.Write(ref prepared, true); }
                        else
                        {
                            Assert.Equal((commit ? "XA COMMIT " : "XA ROLLBACK ") + identity, sql);
                            if (failCompletion) { await channel.WritePacketAsync(Error(), stop.Token); continue; }
                            Interlocked.Increment(ref completions); Volatile.Write(ref prepared, false);
                            if (commit) Interlocked.Increment(ref committed);
                        }
                        await channel.WritePacketAsync(Ok(affected, 2), stop.Token);
                    }
                }
                catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                catch (IOException error) when (error.InnerException is SocketException) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }
}
