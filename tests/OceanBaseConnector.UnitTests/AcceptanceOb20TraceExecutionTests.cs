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
using OceanBaseConnector.UnitTests.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed partial class TextCommandTests
{
    public static IEnumerable<object[]> Ob20TraceAcceptanceRows()
    {
        foreach (var oracle in new[] { false, true })
            foreach (var asynchronous in new[] { false, true })
                foreach (var profile in new[] { "ob20", "trace-legacy", "trace-modern", "declined-ob20", "declined-trace" })
                    yield return new object[] { oracle, asynchronous, profile, "success" };
        foreach (var asynchronous in new[] { false, true })
            foreach (var failure in new[] { "no-policy", "mismatch", "wrong-value", "truncated", "cancel" })
                yield return new object[] { true, asynchronous, "trace-modern", failure };
    }

    [Theory]
    [MemberData(nameof(Ob20TraceAcceptanceRows))]
    public async Task Ob20TraceAcceptanceChecksActualWireAndClosesPrivateConnection(bool oracle, bool asynchronous, string profile, string failure)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var queries = 0; var extras = 0; var closed = false;
        var serving = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token);
            var wire = peer.GetStream(); var channel = new PacketChannel(wire);
            var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
            var upper = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1 + 2 + 1 + 2;
            greeting[upper] |= 16; if (!oracle) greeting[upper] |= 4;
            await channel.WritePacketAsync(greeting, stop.Token);
            var handshake = new ByteCursor(await channel.ReadPacketAsync(stop.Token)); handshake.Skip(32);
            Assert.Equal("synthetic", handshake.ReadNullTerminatedAscii()); handshake.ReadLengthEncodedBytes(); handshake.ReadNullTerminatedAscii();
            var attributes = new ByteCursor(handshake.ReadLengthEncodedBytes()); var values = new Dictionary<string, string>();
            while (attributes.HasRemaining) values.Add(Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()), Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()));
            Assert.Equal(profile == "ob20" ? "256" : "311552", values["__proxy_capability_flag"]);
            var ob20 = profile != "declined-ob20";
            var modern = profile == "trace-modern" && failure != "mismatch";
            var trace = profile is "trace-legacy" or "trace-modern";
            var state = new Dictionary<string, string>
            {
                ["time_zone"] = "+00:00",
                ["ncharacter_set_connection"] = "utf8mb4",
                ["character_set_results"] = "utf8mb4",
                ["character_set_connection"] = "utf8mb4",
                ["ob_capability_flag"] = !ob20 ? "0" : !trace ? "256" : modern ? "311552" : "278784",
            };
            await channel.WritePacketAsync(SessionVariablesOk(state, status: oracle ? (byte)6 : (byte)2), stop.Token);
            channel.BeginCommand(); uint request = 0;
            var identity = await Read();
            Assert.Equal(oracle ? ServerIdentity.OracleQuery : ServerIdentity.MySqlQuery, Encoding.UTF8.GetString(identity.AsSpan(1)));
            await Reply(Ob20AcceptanceResult(oracle ? new[] { "4.4.2.2", "ORACLE" } : new[] { "4.4.2.2", "MYSQL", "1" }), false);
            if (oracle)
            {
                Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString((await Read()).AsSpan(1)));
                await Reply(Ob20AcceptanceFrame(1, Ok()), false);
            }
            try
            {
                while (true)
                {
                    var command = await Read();
                    if (command[0] == 1) break;
                    Assert.Equal(3, command[0]);
                    var sql = Encoding.ASCII.GetString(command.AsSpan(1));
                    if (sql == SessionTimeZoneSql) { await Reply(Ob20AcceptanceResult("+00:00"), false); continue; }
                    if (sql == NationalCharacterSetSql) { await Reply(Ob20AcceptanceResult("utf8mb4", "utf8mb4", "utf8mb4"), false); continue; }
                    Assert.Equal(oracle ? "SELECT 1 FROM DUAL" : "SELECT 1", sql);
                    queries++;
                    if (failure == "cancel")
                    {
                        caller.Cancel(); Assert.Equal(0, await wire.ReadAsync(new byte[1], stop.Token)); closed = true; return;
                    }
                    if (failure == "truncated") { await wire.WriteAsync(new byte[] { 20, 0 }, stop.Token); closed = true; return; }
                    await Reply(Ob20AcceptanceResult(failure == "wrong-value" ? "0" : "1"), trace && failure != "no-policy");
                }
                Assert.Equal(0, await wire.ReadAsync(new byte[1], stop.Token));
            }
            catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
            catch (EndOfStreamException) { }
            closed = true;

            async Task<byte[]> Read()
            {
                if (!ob20) { channel.BeginCommand(); return await channel.ReadPacketAsync(stop.Token); }
                var header = new byte[31]; await wire.ReadExactlyAsync(header, stop.Token);
                var parsed = Ob20Packet.ParseHeader(header, 1234, request, 0);
                var bytes = new byte[parsed.PayloadLength + 35]; header.CopyTo(bytes, 0);
                await wire.ReadExactlyAsync(bytes.AsMemory(31), stop.Token);
                var frame = Ob20Packet.Parse(bytes, 1234, request, 0);
                if (queries > 0 && frame.ExtraInfo.Count > 0) extras++;
                var inner = frame.Payload.ToArray();
                Assert.Equal(0, inner[3]); Assert.Equal(inner.Length - 4, inner[0] | inner[1] << 8 | inner[2] << 16);
                return inner.AsSpan(4).ToArray();
            }
            async Task Reply(byte[] bytes, bool policy)
            {
                var extra = policy ? Ob20TraceTests.Wrap(Ob20TraceTests.Control(1, 1), modern) : null;
                if (ob20) bytes = Ob20Packet.Build(1234, request++, 1, bytes, extra, (modern ? 8u : 0u) | 2u);
                await wire.WriteAsync(bytes, stop.Token);
            }
        });
        try
        {
            var config = AcceptanceConfig.Parse("{\"mode\":\"" + (oracle ? "Oracle" : "MySql") + "\",\"connectionString\":\"unused\",\"ob20TraceProfile\":\"" + profile + "\"}");
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                UserId = "synthetic",
                Password = "synthetic-private",
                TlsMode = OceanBaseTlsMode.Disabled,
                UseOb20 = true,
                EnableFullLinkTrace = profile != "ob20",
                Pooling = true,
                MinPoolSize = 2,
                ConnectionTimeout = 5,
                CommandTimeout = 3,
            };
            var test = Assert.Single(DatabaseCases.All(), c => c.Name == "matrix.ob20-trace-negotiation." + profile + (asynchronous ? ".async" : ".sync"));
            var output = new StringWriter();
            var report = await AcceptanceSuite.RunAsync(config, new[] { test }, output, caller.Token, builder.ConnectionString);
            var result = Assert.Single(report.Results);
            Assert.True(result.Status == (failure == "success" ? "passed" : failure == "cancel" ? "cancelled" : "failed"), result.Detail);
            Assert.Empty(report.Objects);
            var serialized = JsonSerializer.Serialize(report) + output;
            Assert.DoesNotContain("synthetic-private", serialized); Assert.DoesNotContain("127.0.0.1", serialized);
            Assert.DoesNotContain(builder.ConnectionString, serialized);
            await serving.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(closed);
            if (failure == "success")
            {
                Assert.Equal(3, queries);
                Assert.Equal(profile is "trace-legacy" or "trace-modern", extras > 0);
            }
        }
        finally { stop.Cancel(); listener.Stop(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static byte[] Ob20AcceptanceFrame(byte sequence, byte[] payload)
        => new[] { (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), sequence }.Concat(payload).ToArray();
    private static byte[] Ob20AcceptanceResult(params string[] values)
    {
        using var bytes = new MemoryStream(); byte sequence = 1;
        bytes.Write(Ob20AcceptanceFrame(sequence++, new[] { (byte)values.Length }));
        foreach (var value in values) bytes.Write(Ob20AcceptanceFrame(sequence++, Column("n", 253)));
        bytes.Write(Ob20AcceptanceFrame(sequence++, Eof()));
        using var row = new MemoryStream();
        foreach (var value in values) { var text = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)text.Length); row.Write(text); }
        bytes.Write(Ob20AcceptanceFrame(sequence++, row.ToArray())); bytes.Write(Ob20AcceptanceFrame(sequence, Eof()));
        return bytes.ToArray();
    }
}
