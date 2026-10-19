using System;
using System.Collections.Generic;
using System.Data.Common;
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
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "enabled", "unrequested", "unsupported", "no-attributes" })
                        yield return new object[] { target, async, kind };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetNegotiatesOb20OrKeepsStandardFramesThroughPublicApis(string target, bool async, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Serve(listener, kind, budget.Token);
            var context = new AssemblyLoadContext("ob20-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u", ["TLS Mode"] = "Disabled", ["Use OB20"] = kind != "unrequested",
                }.ConnectionString;
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "update t set x=1";
                    Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "update t set x=:p";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = 7; command.Parameters.Add(parameter);
                    Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static async Task Serve(TcpListener listener, string kind, CancellationToken token, X509Certificate2? certificate = null)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var tls = certificate is null ? null : new SslStream(peer.GetStream());
            Stream stream = tls is null ? peer.GetStream() : tls;
            var channel = new PacketChannel(peer.GetStream());
            await Handshake(channel, kind, token, tls: tls, certificate: certificate);
            var ob20 = kind == "enabled";
            for (uint request = 0; request < 4; request++)
            {
                var command = await ReadRequest(stream, channel, ob20, request, token);
                byte[] response;
                if (request == 0)
                { Assert.Equal(3, command[0]); Assert.Equal("update t set x=1", Encoding.UTF8.GetString(command, 1, command.Length - 1)); response = Frame(1, Report(false, 1)); }
                else if (request == 1)
                {
                    Assert.Equal(22, command[0]); Assert.Equal("update t set x=?", Encoding.UTF8.GetString(command, 1, command.Length - 1));
                    response = Frame(1, new byte[] { 0, 9, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 })
                        .Concat(Frame(2, new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 11, 0, 0, 0, 3, 0, 0, 0, 0, 0 }))
                        .Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 })).ToArray();
                }
                else if (request == 2)
                { Assert.Equal(23, command[0]); Assert.Equal(9, command[1]); Assert.Equal(7, command[command.Length - 4]); response = Frame(1, Report(false, 1)); }
                else { Assert.Equal(new byte[] { 25, 9, 0, 0, 0 }, command); break; }
                if (ob20) await stream.WriteAsync(Ob20Packet.Build(1234, request, 1, response), token);
                else { await stream.WriteAsync(response, token); channel.BeginCommand(); }
            }
            await ObserveClose(stream, token);
        }

        private static async Task Handshake(PacketChannel channel, string kind, CancellationToken token, bool deprecated = false, bool lobLocators = false, SslStream? tls = null, X509Certificate2? certificate = null)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var upper = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1 + 2 + 1 + 2;
            if (kind != "no-attributes") greeting[upper] |= 16;
            if (deprecated) greeting[upper + 1] |= 1;
            if (lobLocators) greeting[upper + 1] |= 32;
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
            var handshake = new ByteCursor(await channel.ReadPacketAsync(token)); handshake.Skip(32);
            Assert.Equal("u", handshake.ReadNullTerminatedAscii()); handshake.ReadLengthEncodedBytes(); handshake.ReadNullTerminatedAscii();
            var attributes = new Dictionary<string, string>();
            if (kind != "no-attributes")
            {
                var values = new ByteCursor(handshake.ReadLengthEncodedBytes());
                while (values.HasRemaining) attributes.Add(Encoding.UTF8.GetString(values.ReadLengthEncodedBytes()), Encoding.UTF8.GetString(values.ReadLengthEncodedBytes()));
            }
            Assert.False(handshake.HasRemaining);
            if (kind != "unrequested" && kind != "no-attributes") Assert.Equal("256", attributes["__proxy_capability_flag"]);
            else Assert.False(attributes.ContainsKey("__proxy_capability_flag"));
            if (lobLocators) Assert.Equal("1", attributes["__ob_client_attribute_capability_flag"]);
            await channel.WritePacketAsync(Report(kind != "unsupported", 0), token); channel.BeginCommand();
        }

        private static async Task<byte[]> ReadRequest(Stream stream, PacketChannel channel, bool ob20, uint request, CancellationToken token)
        {
            if (ob20)
            {
                var header = new byte[31]; await stream.ReadExactlyAsync(header, token);
                var parsed = Ob20Packet.ParseHeader(header, 1234, request, 0);
                var bytes = new byte[35 + parsed.PayloadLength]; header.CopyTo(bytes, 0);
                await stream.ReadExactlyAsync(bytes.AsMemory(31), token);
                var inner = Ob20Packet.Parse(bytes, 1234, request, 0).Payload.ToArray();
                Assert.Equal(0, inner[3]); Assert.Equal(inner.Length - 4, inner[0] | inner[1] << 8 | inner[2] << 16);
                return inner.AsSpan(4).ToArray();
            }
            return await channel.ReadPacketAsync(token);
        }

        private static async Task ObserveClose(Stream stream, CancellationToken token)
        {
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException) { }
        }

        [Theory]
        [InlineData(false, "crc")] [InlineData(true, "crc")]
        [InlineData(false, "last")] [InlineData(true, "last")]
        [InlineData(false, "trailing")] [InlineData(true, "trailing")]
        [InlineData(false, "timeout")] [InlineData(true, "timeout")]
        [InlineData(false, "cancel")] [InlineData(true, "cancel")]
        [InlineData(false, "close")] [InlineData(true, "close")]
        public async Task Ob20FaultsBreakSessionWithoutReplayingSql(bool async, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, "enabled", budget.Token);
                var request = await ReadRequest(wire, channel, true, 0, budget.Token);
                Assert.Equal("update t set x=1", Encoding.UTF8.GetString(request, 1, request.Length - 1)); received.SetResult();
                if (kind == "crc" || kind == "last" || kind == "trailing")
                {
                    var data = Frame(1, Report(false, 1));
                    if (kind == "trailing") data = data.Concat(Frame(2, new byte[] { 0 })).ToArray();
                    var bytes = Ob20Packet.Build(1234, 0, 1, data, flags: kind == "last" ? 0u : 2u);
                    if (kind == "crc") bytes[bytes.Length - 1] ^= 1;
                    await wire.WriteAsync(bytes, budget.Token);
                }
                await ObserveClose(wire, budget.Token);
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20=true");
            using var command = connection.CreateCommand(); command.CommandText = "update t set x=1"; command.CommandTimeout = kind == "timeout" ? 1 : 5;
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                var executing = async ? command.ExecuteNonQueryAsync(caller.Token) : Task.Run(command.ExecuteNonQuery);
                await received.Task.WaitAsync(budget.Token);
                if (kind == "cancel") { if (async) caller.Cancel(); else command.Cancel(); }
                if (kind == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(budget.Token));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (kind == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                Assert.Equal(kind == "close" ? System.Data.ConnectionState.Closed : System.Data.ConnectionState.Broken, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20TrustedServerErrorEndsResponseAndAllowsNextCommand(bool async)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, "enabled", budget.Token);
                for (uint request = 0; request < 4; request++)
                {
                    var command = await ReadRequest(wire, channel, true, request, budget.Token); Assert.Equal(3, command[0]);
                    var sql = Encoding.UTF8.GetString(command, 1, command.Length - 1);
                    byte[] response;
                    if (request == 1)
                    { Assert.Equal("SELECT SESSIONTIMEZONE FROM DUAL", sql); response = TextResult("+00:00"); }
                    else if (request == 2)
                    {
                        Assert.Equal("SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL", sql);
                        response = TextResult("utf8mb4", "utf8mb4", "utf8mb4");
                    }
                    else
                    {
                        Assert.Equal("update t set x=1", sql);
                        response = Frame(1, request == 0 ? new byte[] { 255, 122, 4, 35, 52, 50, 83, 48, 50, 101, 114, 114 } : Report(false, 1));
                    }
                    await wire.WriteAsync(Ob20Packet.Build(1234, request, 1, response), budget.Token);
                }
                await ObserveClose(wire, budget.Token);
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20=true");
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "update t set x=1";
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                { if (async) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal("42S02", error.SqlState);
                Assert.Equal(System.Data.ConnectionState.Open, connection.State);
                Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        [Theory]
        [InlineData(false, "success")] [InlineData(true, "success")]
        [InlineData(false, "crc")] [InlineData(true, "crc")]
        [InlineData(false, "last")] [InlineData(true, "last")]
        [InlineData(false, "cancel")] [InlineData(true, "cancel")]
        public async Task Ob20CursorPagesValidateEachOuterBoundary(bool async, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 8, 0, 0, 0, 0, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, "enabled", budget.Token);
                Assert.Equal(22, (await ReadRequest(wire, channel, true, 0, budget.Token))[0]);
                var prepared = Frame(1, new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 })
                    .Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 })).ToArray();
                await wire.WriteAsync(Ob20Packet.Build(1234, 0, 1, prepared), budget.Token);
                Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 1, 1, 0, 0, 0 }, await ReadRequest(wire, channel, true, 1, budget.Token));
                var initial = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 70, 0 })).ToArray();
                await wire.WriteAsync(Ob20Packet.Build(1234, 1, 1, initial), budget.Token);
                for (var page = 0; page < 3; page++)
                {
                    Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 1, 0, 0, 0 }, await ReadRequest(wire, channel, true, (uint)(page + 2), budget.Token));
                    if (page == 1 && kind == "cancel") { waiting.SetResult(); break; }
                    var row = new byte[10]; BitConverter.GetBytes((long)(page + 42)).CopyTo(row, 2);
                    var data = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 }))
                        .Concat(Frame(4, row)).Concat(Frame(5, new byte[] { 254, 0, 0, page == 2 ? (byte)134 : (byte)70, 0 })).ToArray();
                    var bytes = Ob20Packet.Build(1234, (uint)(page + 2), 1, data, flags: page == 1 && kind == "last" ? 0u : 2u);
                    if (page == 1 && kind == "crc") bytes[bytes.Length - 1] ^= 1;
                    await wire.WriteAsync(bytes, budget.Token);
                    if (page == 1 && kind != "success") break;
                }
                if (kind == "success") Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await ReadRequest(wire, channel, true, 5, budget.Token));
                await ObserveClose(wire, budget.Token);
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20=true");
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select n"; ((OceanBaseCommand)command).FetchSize = 1;
                    using var reader = async ? await command.ExecuteReaderAsync(budget.Token) : command.ExecuteReader();
                    Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(42L, reader.GetInt64(0));
                    if (kind == "success")
                    {
                        for (var value = 43; value < 45; value++)
                        { Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(value, reader.GetInt64(0)); }
                        Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
                    }
                    else
                    {
                        var reading = async ? reader.ReadAsync(caller.Token) : Task.Run(reader.Read);
                        if (kind == "cancel")
                        {
                            await waiting.Task.WaitAsync(budget.Token); Assert.False(reading.IsCompleted);
                            if (async) caller.Cancel(); else command.Cancel();
                        }
                        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                        {
                            // A missing last marker is detected when the next fetch would begin.
                            if (await reading.WaitAsync(budget.Token))
                                if (async) await reader.ReadAsync(budget.Token); else reader.Read();
                        });
                        Assert.True(error.IsExecutionOutcomeUnknown);
                        if (kind == "cancel")
                        {
                            var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                            if (async) Assert.Equal(caller.Token, canceled.CancellationToken);
                        }
                        Assert.Equal(System.Data.ConnectionState.Broken, connection.State);
                    }
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        private static byte[] TextResult(params string[] values)
        {
            using var result = new MemoryStream(); byte sequence = 1;
            result.Write(Frame(sequence++, new byte[] { (byte)values.Length }));
            foreach (var value in values)
                result.Write(Frame(sequence++, new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 122, 1, 122, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }));
            result.Write(Frame(sequence++, new byte[] { 254, 0, 0, 6, 0 }));
            using var row = new MemoryStream();
            foreach (var value in values) { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
            result.Write(Frame(sequence++, row.ToArray())); result.Write(Frame(sequence, new byte[] { 254, 0, 0, 6, 0 }));
            return result.ToArray();
        }

        internal static byte[] Report(bool ob20, byte affected, string? capability = null)
        {
            using var state = new MemoryStream();
            var names = new[] { "time_zone", "ncharacter_set_connection", "character_set_results", "character_set_connection", "ob_capability_flag" };
            var values = new[] { "+00:00", "utf8mb4", "utf8mb4", "utf8mb4", capability ?? (ob20 ? "256" : "0") };
            for (var i = 0; i < names.Length; i++)
            {
                state.WriteByte(0); state.WriteByte((byte)(2 + names[i].Length + values[i].Length));
                state.WriteByte((byte)names[i].Length); state.Write(Encoding.ASCII.GetBytes(names[i]));
                state.WriteByte((byte)values[i].Length); state.Write(Encoding.ASCII.GetBytes(values[i]));
            }
            Assert.True(state.Length < 251);
            using var packet = new MemoryStream(); packet.Write(new byte[] { 0, affected, 0, 6, 64, 0, 0, 0 });
            packet.WriteByte((byte)state.Length); packet.Write(state.ToArray()); return packet.ToArray();
        }
        private static byte[] Frame(byte sequence, byte[] payload)
        {
            var bytes = new byte[4 + payload.Length]; bytes[0] = (byte)payload.Length;
            bytes[1] = (byte)(payload.Length >> 8); bytes[2] = (byte)(payload.Length >> 16); bytes[3] = sequence; payload.CopyTo(bytes, 4); return bytes;
        }
    }
}
