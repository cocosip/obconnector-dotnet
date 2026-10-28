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

        private static async Task Handshake(PacketChannel channel, Stream stream, string kind, CancellationToken token, bool deprecated = false, bool lobLocators = false, string version = "4.4.2.2", SslStream? tls = null, X509Certificate2? certificate = null, bool mysql = false, bool initializeAutocommit = true)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var upper = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1 + 2 + 1 + 2;
            if (kind != "no-attributes") greeting[upper] |= 16;
            if (mysql) greeting[upper] |= 4;
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
            var report = Report(kind != "unsupported", 0); if (mysql) report[3] &= 251;
            await channel.WritePacketAsync(report, token); channel.BeginCommand();
            await ReplyIdentity(channel, stream, kind == "enabled", token, deprecated, version, mysql, initializeAutocommit);
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

        private static byte[] TextResult(params string[] values) => TextResult(false, values);

        private static byte[] TextResult(bool deprecated, params string[] values)
        {
            using var result = new MemoryStream(); byte sequence = 1;
            result.Write(Frame(sequence++, new byte[] { (byte)values.Length }));
            foreach (var value in values)
                result.Write(Frame(sequence++, new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 122, 1, 122, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }));
            if (!deprecated) result.Write(Frame(sequence++, new byte[] { 254, 0, 0, 6, 0 }));
            using var row = new MemoryStream();
            foreach (var value in values) { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
            result.Write(Frame(sequence++, row.ToArray())); result.Write(Frame(sequence, deprecated ? new byte[] { 254, 0, 0, 6, 0, 0, 0 } : new byte[] { 254, 0, 0, 6, 0 }));
            return result.ToArray();
        }
        private static byte[] Frame(byte sequence, byte[] payload)
        {
            var bytes = new byte[4 + payload.Length]; bytes[0] = (byte)payload.Length;
            bytes[1] = (byte)(payload.Length >> 8); bytes[2] = (byte)(payload.Length >> 16); bytes[3] = sequence; payload.CopyTo(bytes, 4); return bytes;
        }
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "enabled", "unrequested", "unsupported", "no-attributes" })
                        yield return new object[] { target, async, kind };
        }

        private static async Task Serve(TcpListener listener, string kind, CancellationToken token, X509Certificate2? certificate = null)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var tls = certificate is null ? null : new SslStream(peer.GetStream());
            Stream stream = tls is null ? peer.GetStream() : tls;
            var channel = new PacketChannel(peer.GetStream());
            await Handshake(channel, stream, kind, token, tls: tls, certificate: certificate);
            var ob20 = kind == "enabled";
            for (uint request = 0; request < 4; request++)
            {
                var command = await ReadRequest(stream, channel, ob20, request + 2, token);
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
                if (ob20) await stream.WriteAsync(Ob20Packet.Build(1234, request + 2, 1, response), token);
                else { await stream.WriteAsync(response, token); channel.BeginCommand(); }
            }
            await ObserveClose(stream, token);
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

        internal static async Task ReplyIdentity(PacketChannel channel, Stream stream, bool ob20, CancellationToken token, bool deprecated = false, string version = "4.4.2.2", bool mysql = false, bool initializeAutocommit = true)
        {
            if (!ob20)
            {
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, oracle: !mysql, version: version, deprecated: deprecated, token: token, initializeAutocommit: initializeAutocommit);
                return;
            }
            // Oracle initialization follows identity; feature exchanges start at request 2 (MySQL: 1).
            var query = await ReadRequest(stream, channel, true, 0, token);
            Assert.Equal(3, query[0]);
            Assert.Equal(mysql ? "select ob_version(), @@ob_compatibility_mode, @@autocommit from dual" : "select ob_version(), @@ob_compatibility_mode from dual", Encoding.UTF8.GetString(query, 1, query.Length - 1));
            await stream.WriteAsync(Ob20Packet.Build(1234, 0, 1, TextResult(deprecated, mysql ? new[] { version, "MYSQL", "1" } : new[] { version, "ORACLE" })), token);
            if (!mysql && initializeAutocommit)
            {
                var setup = await ReadRequest(stream, channel, true, 1, token);
                Assert.Equal(3, setup[0]);
                Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(setup, 1, setup.Length - 1));
                await stream.WriteAsync(Ob20Packet.Build(1234, 1, 1, Frame(1, Report(false, 0))), token);
            }
        }
    }
}
