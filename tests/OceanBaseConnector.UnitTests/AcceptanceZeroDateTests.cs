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
        [InlineData(false, true, "MySql", false)]
        [InlineData(true, false, "MySql", false)]
        [InlineData(true, true, "Oracle", false)]
        [InlineData(true, true, "MySql", true)]
        public async Task ZeroDateAcceptanceRequiresAnExplicitObjectAndModeProfile(bool enabled, bool objects, string mode, bool applicable)
        {
            var config = AcceptanceConfig.Parse("{\"mode\":\"" + mode + "\",\"connectionString\":\"unused\",\"zeroDateProfile\":" +
                enabled.ToString().ToLowerInvariant() + ",\"allowCreateObjects\":" + objects.ToString().ToLowerInvariant() + "}");
            var test = Assert.Single(DatabaseCases.All(), c => c.Name == "types.zero-date-policy");
            var reason = AcceptanceSuite.SkipReason(config, new OceanBaseConnectionStringBuilder(), test);
            Assert.Equal(applicable, reason == null);
            if (!applicable)
            {
                var report = await AcceptanceSuite.RunAsync(config, new[] { test }, TextWriter.Null, CancellationToken.None, "Server=unused");
                Assert.Equal("skipped", Assert.Single(report.Results).Status);
                Assert.Empty(report.Objects); Assert.Empty(report.Servers);
            }
        }

        [Theory]
        [InlineData("STRICT_TRANS_TABLES", "none", true)]
        [InlineData("", "none", true)]
        [InlineData("NO_ZERO_DATE", "none", false)]
        [InlineData("strict_trans_tables, no_zero_in_date ", "none", false)]
        [InlineData(null, "none", false)]
        [InlineData("", "text", false)]
        [InlineData("", "binary", false)]
        [InlineData("", "copy", false)]
        public async Task ZeroDateAcceptanceChecksStoredFieldsAndNeverWeakensSqlMode(string? mode, string normalize, bool passes)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var creates = 0; var drops = 0; var reads = 0; var writes = 0;
            var serving = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(stop.Token); peer.NoDelay = true;
                    var channel = new PacketChannel(peer.GetStream());
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                    await channel.ReadPacketAsync(stop.Token);
                    await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                    await ServerIdentity.ReplyAsync(channel, oracle: false, token: stop.Token);
                    byte statement = 0; string sql = "";
                    try
                    {
                        while (true)
                        {
                            channel.BeginCommand(); var packet = await channel.ReadPacketAsync(stop.Token);
                            if (packet[0] == 25) continue;
                            if (packet[0] == 22)
                            {
                                sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1);
                                Assert.True(sql.StartsWith("SELECT d, dt FROM OBA_", StringComparison.Ordinal) || sql.StartsWith("INSERT INTO OBA_", StringComparison.Ordinal));
                                await Prepared(channel, ++statement, (ushort)(sql.StartsWith("INSERT", StringComparison.Ordinal) ? 2 : 0));
                                continue;
                            }
                            if (packet[0] == 3) sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1);
                            else { Assert.Equal(23, packet[0]); Assert.Equal(statement, packet[1]); }
                            Assert.False(sql.StartsWith("SET", StringComparison.OrdinalIgnoreCase), "The acceptance case must not weaken SQL_MODE.");
                            if (sql == "SELECT @@SESSION.sql_mode")
                            {
                                await channel.WritePacketAsync(new byte[] { 1 }, stop.Token);
                                await channel.WritePacketAsync(Column("mode", 253), stop.Token);
                                await channel.WritePacketAsync(Eof(status: 2), stop.Token);
                                await channel.WritePacketAsync(mode == null ? new byte[] { 251 } : WithLength(Encoding.UTF8.GetBytes(mode)), stop.Token);
                                await channel.WritePacketAsync(Eof(status: 2), stop.Token);
                            }
                            else if (sql.StartsWith("CREATE TABLE OBA_", StringComparison.Ordinal))
                            { creates++; await channel.WritePacketAsync(Ok(status: 2), stop.Token); }
                            else if (sql.StartsWith("DROP TABLE OBA_", StringComparison.Ordinal))
                            { drops++; await channel.WritePacketAsync(Ok(status: 2), stop.Token); return; }
                            else if (sql.StartsWith("INSERT INTO OBA_", StringComparison.Ordinal))
                            {
                                if (packet[0] == 23)
                                {
                                    Assert.InRange(writes, 0, 3);
                                    var fields = ZeroAcceptanceBinaryFields(writes);
                                    var expected = (writes == 3 ? new byte[] { 3, 1, 6, 0, 6, 0 } : new byte[] { 0, 1, 10, 0, 12, 0 }).Concat(fields).ToArray();
                                    Assert.Equal(expected, packet.AsSpan(10).ToArray()); writes++;
                                }
                                await channel.WritePacketAsync(Ok(packet[0] == 23 ? (byte)1 : (byte)4, status: 2), stop.Token);
                            }
                            else
                            {
                                Assert.StartsWith("SELECT d, dt FROM OBA_", sql); reads++;
                                var binary = packet[0] == 23;
                                await channel.WritePacketAsync(new byte[] { 2 }, stop.Token);
                                await channel.WritePacketAsync(ValueColumnPacket(10), stop.Token);
                                await channel.WritePacketAsync(ValueColumnPacket(12, scale: 6), stop.Token);
                                await channel.WritePacketAsync(Eof(status: 2), stop.Token);
                                for (var row = 0; row < 4; row++)
                                {
                                    var actual = row == 1 && (normalize == (binary ? "binary" : "text") || normalize == "copy" && reads > 2) ? 0 : row;
                                    var fields = binary ? ZeroAcceptanceBinaryFields(actual) : ZeroAcceptanceTextFields(actual);
                                    await channel.WritePacketAsync(binary ? new byte[] { 0, (byte)(row == 3 ? 12 : 0) }.Concat(fields).ToArray() : fields, stop.Token);
                                }
                                await channel.WritePacketAsync(Eof(status: 2), stop.Token);
                            }
                        }
                    }
                    catch (ProtocolException e) when (e.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                    catch (IOException) { }
                }
            });
            try
            {
                var config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"unused\",\"zeroDateProfile\":true,\"allowCreateObjects\":true}");
                var test = Assert.Single(DatabaseCases.All(), c => c.Name == "types.zero-date-policy");
                var report = await AcceptanceSuite.RunAsync(config, new[] { test }, TextWriter.Null, stop.Token,
                    Configuration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Pooling=false;ExpectedMode=MySql");
                var result = Assert.Single(report.Results);
                Assert.True(result.Status == (passes ? "passed" : "failed"), result.Detail);
                if (!passes) Assert.Contains(normalize == "none" ? "SQL_MODE" : "without normalization", result.Detail);
                Assert.Equal(0, report.CleanupFailures);
                var hasTable = mode != null && !mode.Contains("zero", StringComparison.OrdinalIgnoreCase);
                Assert.Equal(hasTable ? 1 : 0, creates); Assert.Equal(creates, drops);
                if (passes) { Assert.Equal(4, reads); Assert.Equal(4, writes); }
                Assert.All(report.Objects, item => Assert.Equal("removed", item.Cleanup));
                if (hasTable) await serving.WaitAsync(stop.Token);
            }
            finally
            {
                stop.Cancel(); listener.Stop();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
            }
        }

        private static byte[] ZeroAcceptanceBinaryFields(int row) => row switch
        {
            0 => new byte[] { 4, 232, 7, 2, 29, 11, 232, 7, 2, 29, 1, 2, 3, 64, 226, 1, 0 },
            1 => new byte[] { 4, 232, 7, 0, 12, 11, 232, 7, 2, 0, 1, 2, 3, 64, 226, 1, 0 },
            2 => new byte[] { 0, 0 },
            _ => Array.Empty<byte>(),
        };
        private static byte[] ZeroAcceptanceTextFields(int row) => row switch
        {
            0 => WithLength(Encoding.ASCII.GetBytes("2024-02-29")).Concat(WithLength(Encoding.ASCII.GetBytes("2024-02-29 01:02:03.123456"))).ToArray(),
            1 => WithLength(Encoding.ASCII.GetBytes("2024-00-12")).Concat(WithLength(Encoding.ASCII.GetBytes("2024-02-00 01:02:03.123456"))).ToArray(),
            2 => WithLength(Encoding.ASCII.GetBytes("0000-00-00")).Concat(WithLength(Encoding.ASCII.GetBytes("0000-00-00 00:00:00.000000"))).ToArray(),
            _ => new byte[] { 251, 251 },
        };
    }
}
