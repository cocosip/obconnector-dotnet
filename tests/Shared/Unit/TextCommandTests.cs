using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;Password=secret;TLS Mode=Disabled;Connection Timeout=5";

        private static LoopbackServer Server(Func<PacketChannel, Task> exchange, bool deprecateEof = false, bool reportTimeZone = true, string nationalCharacterSet = "utf8mb4", bool lobLocators = false, string serverVersion = "4.4.2.2", bool multiStatements = false) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
            if (deprecateEof) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 1;
            if (lobLocators) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 32;
            if (multiStatements) greeting[41] |= 1;
            if (multiStatements) Assert.True(InitialHandshakePacket.Parse(greeting).Capabilities.HasFlag(CapabilityFlags.ClientMultiStatements));
            await channel.WritePacketAsync(greeting);
            var authentication = await channel.ReadPacketAsync();
            if (deprecateEof) Assert.Equal(1, authentication[3] & 1);
            if (multiStatements) Assert.Equal(1, authentication[2] & 1);
            await channel.WritePacketAsync(reportTimeZone ? SessionVariablesOk(new NationalCharacterSet(nationalCharacterSet, "utf8mb4", "utf8mb4").Variables("+00:00")) : Ok());
            await ServerIdentity.ReplyAsync(channel, version: serverVersion, deprecated: deprecateEof);
            await exchange(channel);
        });

        private static byte[] Ok(byte affected = 0, byte status = 6) => new byte[] { 0, affected, 0, status, 0, 0, 0 };
        private static byte[] Eof(byte status = 6) => new byte[] { 254, 0, 0, status, 0 };

        private static byte[] Column(string name, byte type = 9, ushort charset = 45, ushort flags = 0)
        {
            using var bytes = new MemoryStream();
            foreach (var value in new[] { "def", "", "", "", name, name })
            {
                var encoded = Encoding.UTF8.GetBytes(value);
                bytes.WriteByte((byte)encoded.Length);
                bytes.Write(encoded, 0, encoded.Length);
            }
            bytes.Write(new byte[] { 12, (byte)charset, (byte)(charset >> 8), 255, 0, 0, 0,
                type, (byte)flags, (byte)(flags >> 8), 0, 0, 0 }, 0, 13);
            return bytes.ToArray();
        }
    }
}
