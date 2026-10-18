using System;
using System.IO;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class HandshakeRegressionTests
    {
        [Fact]
        public void LongDnsNameUsesLengthEncodedAttributeValue()
        {
            var host = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61);
            var caps = CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSecureConnection | CapabilityFlags.ClientConnectAttrs;
            var packet = HandshakeResponsePacket.Build("u", Array.Empty<byte>(), "", caps, caps, 45,
                "mysql_native_password", host);
            var cursor = new ByteCursor(packet);
            cursor.Skip(32);
            Assert.Equal("u", cursor.ReadNullTerminatedAscii());
            Assert.Empty(cursor.ReadLengthEncodedBytes());
            var attributes = new ByteCursor(cursor.ReadLengthEncodedBytes());
            for (var i = 0; i < 2; i++)
            {
                attributes.ReadLengthEncodedBytes();
                attributes.ReadLengthEncodedBytes();
            }
            Assert.Equal("_server_host", Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()));
            Assert.Equal(host, Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()));
            Assert.False(attributes.HasRemaining);
        }

        [Fact]
        public void AlternatingPacketsShareOneSequence()
        {
            using var stream = new MemoryStream(new byte[32]);
            var channel = new PacketChannel(stream);
            channel.WritePacket(new byte[] { 0x01 });
            stream.Write(new byte[] { 1, 0, 0, 1, 0x02 });
            stream.Position = 5;
            Assert.Equal(new byte[] { 0x02 }, channel.ReadPacket());
            channel.WritePacket(new byte[] { 0x03 });
            Assert.Equal(2, stream.ToArray()[13]);
        }

        [Fact]
        public void AuthSwitchSeedExcludesTerminatingNull()
        {
            var bytes = new byte[44];
            bytes[0] = 0xFE;
            Encoding.ASCII.GetBytes("mysql_native_password").CopyTo(bytes, 1);
            for (var i = 0; i < 20; i++) bytes[23 + i] = (byte)(i + 1);
            Assert.Equal(20, AuthSwitchRequest.Parse(bytes).Seed.Length);
        }

        [Theory]
        [InlineData(new byte[] { 0xFF })]
        [InlineData(new byte[] { 0xFE, 0, 0, 0, 0, 0, 0, 0, 0x80 })]
        public void InvalidLengthIntegerIsRejected(byte[] bytes)
        {
            Assert.Throws<ProtocolException>(() => new ByteCursor(bytes).ReadLengthEncodedInteger());
        }

        [Fact]
        public void SessionStateEntryHasItsOwnLength()
        {
            // OK, empty counters, AUTOCOMMIT|STATE_CHANGED, empty info,
            // outer state length 7, system-variable entry length 5: a=bc.
            var packet = new byte[] { 0, 0, 0, 2, 64, 0, 0, 0, 7, 0, 5, 1, 97, 2, 98, 99 };
            var ok = OkPacket.Parse(packet, CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSessionTrack);
            Assert.Equal("bc", ok.SessionVariables["a"]);
        }

        [Fact]
        public void TruncatedChangedSessionStateIsRejected()
        {
            Assert.Throws<ProtocolException>(() => OkPacket.Parse(
                new byte[] { 0, 0, 0, 2, 64, 0, 0, 0 },
                CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSessionTrack));
        }
    }
}
