using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed class Ob20TraceConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "modern", "legacy", "disabled", "no-trace", "no-listener" }) yield return new object[] { target, async, kind };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public Task EveryAssetCorrelatesFullLinkTraceWithoutSendingApplicationData(string target, bool async, string kind)
            => CorrelatesFullLinkTrace(target, async, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(Cases))]
        public Task EveryAssetCorrelatesFullLinkTraceOverTlsWithoutSendingApplicationData(string target, bool async, string kind)
            => CorrelatesFullLinkTrace(target, async, kind, encrypted: true);

        private static async Task CorrelatesFullLinkTrace(string target, bool async, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var active = kind == "modern" || kind == "legacy" || kind == "no-listener"; var modern = kind != "legacy";
            var activities = new ConcurrentQueue<Activity>(); var remoteIds = new List<string>(); string? parentId = null;
            using var activityListener = new ActivityListener
            {
                ShouldListenTo = source => kind != "no-listener" && source.Name == "OceanBaseConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => activities.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(activityListener);
            using var parent = new Activity("application").SetIdFormat(ActivityIdFormat.W3C).Start(); parent.AddBaggage("private_baggage", "private_payload");
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream wire = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, wire, kind, budget.Token, tls, certificate);
                for (uint request = 0; request < 3; request++)
                {
                    var packet = await ReadFrame(wire, request + 2, budget.Token);
                    Assert.Equal(3, packet.Payload.Array![packet.Payload.Offset + 4]);
                    Assert.Equal("update t set x='private_payload'", Encoding.UTF8.GetString(packet.Payload.Array!, packet.Payload.Offset + 5, packet.Payload.Count - 5));
                    if (active && request > 0)
                    {
                        Assert.Equal(modern ? 11u : 3u, packet.Header.Flags);
                        var trace = Fields(Unwrap(packet.ExtraInfo.ToArray(), modern));
                        var span = Fields(trace[2004]);
                        Assert.Equal(new byte[] { 1 }, span[2030]); Assert.Equal(new byte[] { 1 }, span[2031]);
                        remoteIds.Add(Convert.ToHexString(span[2032]).ToLowerInvariant()); Assert.Equal(16, span[2034].Length);
                        var text = Encoding.UTF8.GetString(packet.ExtraInfo.ToArray());
                        Assert.DoesNotContain("private", text); Assert.DoesNotContain("password", text);
                        if (request == 2)
                        {
                            var log = Fields(trace[1])[1]; Assert.Equal(0, log[log.Length - 1]);
                            using var json = JsonDocument.Parse(log.AsMemory(0, log.Length - 1));
                            Assert.Equal("oceanbase_dotnet", json.RootElement.GetProperty("name").GetString());
                            parentId = json.RootElement.GetProperty("parent_id").GetString()!.Replace("-", "");
                            Assert.True(json.RootElement.GetProperty("end_ts").GetInt64() >= json.RootElement.GetProperty("start_ts").GetInt64());
                            Assert.True(trace.ContainsKey(2005));
                        }
                    }
                    else Assert.Equal(0, packet.ExtraInfo.Count);
                    var response = Frame(1, Ob20ConsumerTests.Report(false, 1));
                    var extra = request == 0 ? Wrap(Control(), modern) : null;
                    await wire.WriteAsync(Ob20Packet.Build(1234, request + 2, 1, response, extra, flags: modern && active ? 10u : 2u), budget.Token);
                }
                Assert.Equal(0, await wire.ReadAsync(new byte[1], budget.Token));
            });
            var context = new AssemblyLoadContext("ob20-trace-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use OB20=true;Enable Full Link Trace={kind != "disabled"}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                for (var i = 0; i < 3; i++)
                {
                    using var command = connection.CreateCommand(); command.CommandText = "update t set x='private_payload'";
                    Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
                var commands = activities.Where(a => a.OperationName == "oceanbase.execute").ToArray(); Assert.Equal(kind == "no-listener" ? 0 : 3, commands.Length);
                if (active)
                {
                    Assert.All(remoteIds, id => Assert.Equal(parent.TraceId.ToHexString(), id));
                    Assert.Equal(new string('0', 16) + (kind == "no-listener" ? parent.SpanId : commands[1].SpanId).ToHexString(), parentId);
                }
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        public static IEnumerable<object[]> ControlCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var modern in new[] { false, true }) yield return new object[] { async, modern };
        }

        [Theory]
        [MemberData(nameof(ControlCases))]
        public Task FullLinkTraceCoversPrepareCursorAndTransactionControl(bool async, bool modern)
            => TracePrepareCursorAndTransactionControl(async, modern, encrypted: false);

        [Theory]
        [MemberData(nameof(ControlCases))]
        public Task FullLinkTraceOverTlsCoversPrepareCursorAndTransactionControl(bool async, bool modern)
            => TracePrepareCursorAndTransactionControl(async, modern, encrypted: true);

        private static async Task TracePrepareCursorAndTransactionControl(bool async, bool modern, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var parent = new Activity("application").SetIdFormat(ActivityIdFormat.W3C).Start();
            var activities = new ConcurrentQueue<Activity>(); var previousParents = new Dictionary<uint, string>();
            using var activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "OceanBaseConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => activities.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(activityListener);
            var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 8, 0, 0, 0, 0, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream wire = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, wire, modern ? "modern" : "legacy", budget.Token, tls, certificate);
                var spans = new HashSet<string>();
                for (uint request = 0; request < 10; request++)
                {
                    var packet = await ReadFrame(wire, request + 2, budget.Token); var command = packet.Payload.ToArray().AsSpan(4).ToArray();
                    if (request > 0)
                    {
                        var trace = Fields(Unwrap(packet.ExtraInfo.ToArray(), modern)); var span = Fields(trace[2004]);
                        Assert.True(spans.Add(Convert.ToHexString(span[2034])));
                        if (request != 5) Assert.Equal(parent.TraceId.ToHexString(), Convert.ToHexString(span[2032]).ToLowerInvariant());
                        if (request > 1)
                        {
                            var log = Fields(trace[1])[1]; using var json = JsonDocument.Parse(log.AsMemory(0, log.Length - 1));
                            previousParents.Add(request - 1, json.RootElement.GetProperty("parent_id").GetString()!.Replace("-", ""));
                        }
                    }
                    byte[] data;
                    if (request == 0) { Assert.Equal(3, command[0]); data = Frame(1, Ob20ConsumerTests.Report(false, 1)); }
                    else if (request == 1)
                    {
                        Assert.Equal(22, command[0]);
                        data = Frame(1, new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 })).ToArray();
                    }
                    else if (request == 2)
                    {
                        Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 1, 1, 0, 0, 0 }, command);
                        data = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 70, 0 })).ToArray();
                    }
                    else if (request == 3 || request == 4)
                    {
                        Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 1, 0, 0, 0 }, command);
                        var row = new byte[10]; BitConverter.GetBytes((long)request).CopyTo(row, 2);
                        data = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 }))
                            .Concat(Frame(4, row)).Concat(Frame(5, new byte[] { 254, 0, 0, request == 4 ? (byte)134 : (byte)70, 0 })).ToArray();
                    }
                    else if (request == 5) { Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, command); continue; }
                    else
                    {
                        Assert.Equal(3, command[0]);
                        var expected = request == 6 ? "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED" : request == 7 ? "set autocommit = 0" : request == 8 ? "COMMIT" : "set autocommit = 1";
                        Assert.Equal(expected, Encoding.UTF8.GetString(command, 1, command.Length - 1));
                        var ok = Ob20ConsumerTests.Report(false, 0); if (request == 7 || request == 8) ok[3] = 4; data = Frame(1, ok);
                    }
                    await wire.WriteAsync(Ob20Packet.Build(1234, request + 2, 1, data, request == 0 ? Wrap(Control(), modern) : null, flags: modern ? 10u : 2u), budget.Token);
                }
                Assert.Equal(0, await wire.ReadAsync(new byte[1], budget.Token));
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use OB20=true;Enable Full Link Trace=true");
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                { command.CommandText = "update t set x=1"; if (async) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select n"; ((OceanBaseCommand)command).FetchSize = 1;
                    using var reader = async ? await command.ExecuteReaderAsync(budget.Token) : command.ExecuteReader();
                    for (var value = 3; value < 5; value++)
                    { Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(value, reader.GetInt64(0)); }
                    Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                }
                using (var transaction = async ? await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, budget.Token) : connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
                { if (async) await transaction.CommitAsync(budget.Token); else transaction.Commit(); }
                connection.Close(); await serving.WaitAsync(budget.Token);
                var cursorActivity = activities.Last(a => a.OperationName == "oceanbase.execute");
                foreach (var request in new uint[] { 1, 2, 3, 4 }) Assert.Equal(new string('0', 16) + cursorActivity.SpanId.ToHexString(), previousParents[request]);
                var begin = activities.Single(a => a.OperationName == "oceanbase.transaction.begin");
                foreach (var request in new uint[] { 6, 7 }) Assert.Equal(new string('0', 16) + begin.SpanId.ToHexString(), previousParents[request]);
                var commit = activities.Single(a => a.OperationName == "oceanbase.transaction.commit");
                Assert.Equal(new string('0', 16) + commit.SpanId.ToHexString(), previousParents[8]);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        public static IEnumerable<object[]> FaultCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var modern in new[] { false, true })
                    foreach (var kind in new[] { "malformed", "limit", "crc", "partial-cancel" }) yield return new object[] { async, modern, kind };
        }

        [Theory]
        [MemberData(nameof(FaultCases))]
        public Task TraceExtraFaultsAndPartialBodyCancellationBreakConnectionWithoutReplay(bool async, bool modern, string kind)
            => TraceFaultsAndPartialBodyCancellation(async, modern, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(FaultCases))]
        public Task TraceOverTlsExtraFaultsAndPartialBodyCancellationBreakConnectionWithoutReplay(bool async, bool modern, string kind)
            => TraceFaultsAndPartialBodyCancellation(async, modern, kind, encrypted: true);

        private static async Task TraceFaultsAndPartialBodyCancellation(bool async, bool modern, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream wire = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, wire, modern ? "modern" : "legacy", budget.Token, tls, certificate); Assert.Equal(0, (await ReadFrame(wire, 2, budget.Token)).ExtraInfo.Count);
                var extra = kind == "limit" ? new byte[65537] : Wrap(kind == "malformed" ? new byte[] { 211, 7, 255, 255, 255, 255 } : Control(), modern);
                var bytes = Ob20Packet.Build(1234, 2, 1, Frame(1, Ob20ConsumerTests.Report(false, 1)), extra, flags: modern ? 10u : 2u);
                if (kind == "crc") bytes[bytes.Length - 1] ^= 1;
                try { await wire.WriteAsync(bytes.AsMemory(0, kind == "partial-cancel" ? 31 + 4 + extra.Length + 2 : bytes.Length), budget.Token); }
                catch (IOException) when (kind == "limit") { }
                waiting.SetResult();
                try { Assert.Equal(0, await wire.ReadAsync(new byte[1], budget.Token)); } catch (IOException) { }
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use OB20=true;Enable Full Link Trace=true");
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "update t set x=1";
                var executing = async ? command.ExecuteNonQueryAsync(caller.Token) : Task.Run(command.ExecuteNonQuery);
                await waiting.Task.WaitAsync(budget.Token);
                if (kind == "partial-cancel") { Assert.False(executing.IsCompleted); if (async) caller.Cancel(); else command.Cancel(); }
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(budget.Token));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(System.Data.ConnectionState.Broken, connection.State);
                if (kind == "partial-cancel")
                {
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                    if (async) Assert.Equal(caller.Token, canceled.CancellationToken);
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        private static async Task Handshake(PacketChannel channel, Stream stream, string kind, CancellationToken token, SslStream? tls = null, X509Certificate2? certificate = null)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var upper = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1 + 2 + 1 + 2; greeting[upper] |= 16;
            await channel.WritePacketAsync(greeting, token);
            if (tls is not null)
            {
                var request = await channel.ReadPacketAsync(token);
                Assert.Equal(32, request.Length);
                Assert.NotEqual(0, request[1] & 8);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
                Assert.True(tls.IsEncrypted);
                channel.ReplaceUnderlyingStream(tls);
            }
            var handshake = new ByteCursor(await channel.ReadPacketAsync(token));
            handshake.Skip(32); Assert.Equal("u", handshake.ReadNullTerminatedAscii()); handshake.ReadLengthEncodedBytes(); handshake.ReadNullTerminatedAscii();
            var attributes = new ByteCursor(handshake.ReadLengthEncodedBytes()); var values = new Dictionary<string, string>();
            while (attributes.HasRemaining) values.Add(Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()), Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()));
            Assert.Equal(kind == "disabled" ? "256" : "311552", values["__proxy_capability_flag"]);
            var capability = kind == "no-trace" ? "256" : kind == "legacy" ? "278784" : "311552";
            await channel.WritePacketAsync(Ob20ConsumerTests.Report(true, 0, capability), token); channel.BeginCommand();
            await Ob20ConsumerTests.ReplyIdentity(channel, stream, true, token);
        }

        private static async Task<Ob20Packet> ReadFrame(Stream stream, uint request, CancellationToken token)
        {
            var header = new byte[31]; await stream.ReadExactlyAsync(header, token);
            var parsed = Ob20Packet.ParseHeader(header, 1234, request, 0); var bytes = new byte[parsed.PayloadLength + 35]; header.CopyTo(bytes, 0);
            await stream.ReadExactlyAsync(bytes.AsMemory(31), token); return Ob20Packet.Parse(bytes, 1234, request, 0);
        }
        private static byte[] Frame(byte sequence, byte[] bytes) => new[] { (byte)bytes.Length, (byte)(bytes.Length >> 8), (byte)(bytes.Length >> 16), sequence }.Concat(bytes).ToArray();
        private static byte[] Control() => Tlv(2003, Tlv(2020, new byte[] { 1 }).Concat(Tlv(2021, BitConverter.GetBytes(1d)))
            .Concat(Tlv(2022, new byte[] { 1 })).Concat(Tlv(2025, new byte[] { 1 })).ToArray());
        private static byte[] Tlv(ushort type, byte[] bytes) => new[] { (byte)type, (byte)(type >> 8) }.Concat(BitConverter.GetBytes(bytes.Length)).Concat(bytes).ToArray();
        private static byte[] Wrap(byte[] bytes, bool modern) => modern ? Tlv(2003, bytes) : Legacy(Encoding.ASCII.GetBytes("full_trc")).Concat(Legacy(bytes)).ToArray();
        private static byte[] Legacy(byte[] bytes)
        {
            using var stream = new MemoryStream(); stream.Write(new byte[] { 22, 2, 0, 255 }); var length = bytes.Length;
            while (length >= 128) { stream.WriteByte((byte)(length | 128)); length >>= 7; }
            stream.WriteByte((byte)length); stream.Write(bytes); stream.WriteByte(0); return stream.ToArray();
        }
        private static byte[] Unwrap(byte[] bytes, bool modern)
        {
            if (modern) return Fields(bytes)[2003]; var cursor = new ByteCursor(bytes); byte[] result = Array.Empty<byte>();
            for (var i = 0; i < 2; i++)
            {
                cursor.Skip(4); var length = 0; var shift = 0; byte part;
                do { part = cursor.ReadUInt8(); length |= (part & 127) << shift; shift += 7; } while (part >= 128);
                result = cursor.ReadBytes(length); Assert.Equal(0, cursor.ReadUInt8());
            }
            Assert.False(cursor.HasRemaining); return result;
        }
        private static Dictionary<ushort, byte[]> Fields(byte[] bytes)
        {
            var cursor = new ByteCursor(bytes); var fields = new Dictionary<ushort, byte[]>();
            while (cursor.HasRemaining) { var key = cursor.ReadUInt16(); fields.Add(key, cursor.ReadBytes((int)cursor.ReadUInt32())); }
            return fields;
        }
    }
}
