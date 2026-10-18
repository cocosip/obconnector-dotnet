using System;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class HandshakePacketsTests
    {
        [Fact]
        public void InitialHandshakeFixtureParsesCompletely()
        {
            var fixture = ProtocolFixture.Load("initial-handshake-oceanbase");
            var handshake = InitialHandshakePacket.Parse(fixture.Payload);
            var expectation = fixture.Expectation;

            Assert.Equal((byte)expectation.GetProperty("protocolVersion").GetInt32(), handshake.ProtocolVersion);
            Assert.Equal(expectation.GetProperty("serverVersion").GetString(), handshake.ServerVersion);
            Assert.Equal((uint)expectation.GetProperty("connectionId").GetInt32(), handshake.ConnectionId);
            Assert.Equal((uint)expectation.GetProperty("capabilities").GetInt32(), (uint)handshake.Capabilities);
            Assert.Equal((byte)expectation.GetProperty("charset").GetInt32(), handshake.Charset);
            Assert.Equal((ushort)expectation.GetProperty("statusFlags").GetInt32(), (ushort)handshake.StatusFlags);
            Assert.Equal(
                expectation.GetProperty("seedHex").GetString(),
                Convert.ToHexString(handshake.Seed).ToLowerInvariant());
            Assert.Equal(
                expectation.GetProperty("authenticationPluginName").GetString(),
                handshake.AuthenticationPluginName);
            Assert.True(handshake.IsOceanBaseServer);
        }

        [Fact]
        public void NonOceanBaseServerVersionIsFlagged()
        {
            var handshake = InitialHandshakePacket.Parse(BuildMinimalHandshake("5.7.29-log"));
            Assert.False(handshake.IsOceanBaseServer);
        }

        [Fact]
        public void UnsupportedProtocolVersionIsRejected()
        {
            Assert.Throws<ProtocolException>(() => InitialHandshakePacket.Parse(new byte[] { 9 }));
        }

        [Fact]
        public void OkPacketOracleModeFixtureParsesWithSessionTrack()
        {
            var fixture = ProtocolFixture.Load("ok-packet-oracle-mode-session-track");
            var ok = OkPacket.Parse(
                fixture.Payload,
                CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSessionTrack);
            var expectation = fixture.Expectation;

            Assert.Equal(expectation.GetProperty("affectedRows").GetInt64(), ok.AffectedRows);
            Assert.Equal((ushort)expectation.GetProperty("statusFlags").GetInt32(), (ushort)ok.StatusFlags);
            Assert.True(
                ((ok.StatusFlags & ServerStatusFlags.ObServerUseOracleMode) != 0) ==
                expectation.GetProperty("statusFlagsHasOracleMode").GetBoolean());
            Assert.Equal(
                "1023",
                ok.SessionVariables["ob_capability_flag"]);
        }

        [Fact]
        public void OkPacketMysqlModeFixtureParsesWithoutSessionTrack()
        {
            var fixture = ProtocolFixture.Load("ok-packet-mysql-mode");
            var ok = OkPacket.Parse(
                fixture.Payload,
                CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSessionTrack);
            var expectation = fixture.Expectation;

            Assert.Equal(3, ok.AffectedRows);
            Assert.Equal(ServerStatusFlags.Autocommit, ok.StatusFlags & ServerStatusFlags.Autocommit);
            Assert.Equal(0, (int)(ok.StatusFlags & ServerStatusFlags.ObServerUseOracleMode));
            Assert.Equal(expectation.GetProperty("info").GetString(), ok.Info);
            Assert.Empty(ok.SessionVariables);
        }

        [Fact]
        public void ErrPacketFixtureParses()
        {
            var fixture = ProtocolFixture.Load("err-packet-handshake-failure");
            var error = ErrPacket.Parse(fixture.Payload);

            Assert.Equal(1045, error.ErrorCode);
            Assert.Equal("28000", error.SqlState);
            Assert.Contains("Access denied", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ErrPacketWithoutSqlStateMarkerParses()
        {
            var error = ErrPacket.Parse(new byte[] { 0xFF, 0x11, 0x04, (byte)'x' });
            Assert.Equal(1041, error.ErrorCode);
            Assert.Null(error.SqlState);
            Assert.Equal("x", error.Message);
        }

        [Fact]
        public void NativePasswordMatchesIndependentTestVector()
        {
            // Vector computed with openssl: password "pw1", seed 0x01..0x14.
            // token = SHA1(seed || SHA1(SHA1(pw))) XOR SHA1(pw).
            var seed = new byte[20];
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] = (byte)(i + 1);
            }

            var token = NativePassword.EncryptPassword("pw1", seed);
            Assert.Equal("87E3001F2563F83EC1F9CFDD6420B4A28676D841",
                BitConverter.ToString(token).Replace("-", string.Empty));
        }

        [Fact]
        public void EmptyPasswordProducesEmptyToken()
        {
            Assert.Empty(NativePassword.EncryptPassword(string.Empty, new byte[20]));
        }

        [Fact]
        public void HandshakeResponseWireLayoutMatchesProtocol()
        {
            var capabilities = CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection |
                CapabilityFlags.ClientPluginAuth |
                CapabilityFlags.ClientConnectWithDb;
            var serverCapabilities = CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection |
                CapabilityFlags.ClientPluginAuth;
            var authData = new byte[] { 0xAA, 0xBB, 0xCC };

            var packet = HandshakeResponsePacket.Build(
                "u@sys", authData, "test", capabilities, serverCapabilities, 45,
                "mysql_native_password", "ob1");

            var cursor = new ByteCursor(packet);
            Assert.Equal((uint)capabilities, cursor.ReadUInt32());
            Assert.Equal((uint)HandshakeResponsePacket.MaxPacketSize, cursor.ReadUInt32());
            Assert.Equal(45, cursor.ReadUInt8());
            cursor.Skip(19);
            Assert.Equal(0u, cursor.ReadUInt32()); // MariaDB extended flags

            Assert.Equal("u@sys", cursor.ReadNullTerminatedAscii());
            Assert.Equal((byte)authData.Length, cursor.ReadUInt8());
            Assert.Equal(authData, cursor.ReadBytes(authData.Length));
            Assert.Equal("test", cursor.ReadNullTerminatedAscii()); // database
            Assert.Equal("mysql_native_password", cursor.ReadNullTerminatedAscii());
            // CONNECT_ATTRS not offered by this server: nothing may follow.
            Assert.Equal(0, cursor.Remaining);
        }

        [Fact]
        public void HandshakeResponseUsesLenencAuthDataWhenServerSupportsIt()
        {
            var authData = new byte[300];
            authData[0] = 0x42;
            var packet = HandshakeResponsePacket.Build(
                "u",
                authData,
                string.Empty,
                CapabilityFlags.ClientProtocol41,
                CapabilityFlags.ClientPluginAuthLenencClientData | CapabilityFlags.ClientProtocol41 |
                    CapabilityFlags.ClientPluginAuth,
                45,
                "mysql_native_password",
                null);

            var cursor = new ByteCursor(packet);
            cursor.Skip(4 + 4 + 1 + 19 + 4);
            Assert.Equal("u", cursor.ReadNullTerminatedAscii());
            Assert.Equal(300, cursor.ReadLengthEncodedInteger());
            Assert.Equal(authData, cursor.ReadBytes(300));
        }

        [Fact]
        public void ConnectAttributesAreSentOnlyWhenServerSupportsThem()
        {
            var withAttrs = HandshakeResponsePacket.Build(
                "u", Array.Empty<byte>(), string.Empty,
                CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientConnectAttrs,
                CapabilityFlags.ClientConnectAttrs | CapabilityFlags.ClientProtocol41,
                45, "mysql_native_password", "ob1.example.com");
            var withoutAttrs = HandshakeResponsePacket.Build(
                "u", Array.Empty<byte>(), string.Empty,
                CapabilityFlags.ClientProtocol41,
                CapabilityFlags.ClientProtocol41,
                45, "mysql_native_password", "ob1.example.com");

            Assert.True(withAttrs.Length > withoutAttrs.Length);
            var tail = Encoding.UTF8.GetString(withAttrs, withoutAttrs.Length, withAttrs.Length - withoutAttrs.Length);
            Assert.Contains("_client_name", tail, StringComparison.Ordinal);
            Assert.Contains("OceanBase Connector/.NET", tail, StringComparison.Ordinal);
            Assert.Contains("_server_host", tail, StringComparison.Ordinal);
            Assert.Contains("ob1.example.com", tail, StringComparison.Ordinal);
        }

        [Fact]
        public void AuthSwitchRequestParsesPluginAndSeed()
        {
            var payload = new byte[] { 0xFE }
                .Concat(Encoding.ASCII.GetBytes("mysql_native_password"))
                .Concat(new byte[] { 0x00, 0x01, 0x02 });
            var switchRequest = AuthSwitchRequest.Parse(payload);

            Assert.Equal("mysql_native_password", switchRequest.PluginName);
            Assert.Equal(new byte[] { 0x01, 0x02 }, switchRequest.Seed);
        }

        [Fact]
        public void ClientCapabilitiesAlwaysIncludeFoundRowsAndOracleSupport()
        {
            var options = new OceanBaseConnectionStringBuilder { Server = "s", UserId = "u" }.Build();
            var capabilities = ClientCapabilities.Build(CapabilityFlags.None, options);

            Assert.True(capabilities.HasFlag(CapabilityFlags.ClientFoundRows));
            Assert.True(capabilities.HasFlag(CapabilityFlags.ClientSupportOracle));
            Assert.True(capabilities.HasFlag(CapabilityFlags.ClientSessionTrack));
            Assert.False(capabilities.HasFlag(CapabilityFlags.ClientConnectWithDb));
            Assert.False(capabilities.HasFlag(CapabilityFlags.ClientDeprecateEof));
        }

        [Fact]
        public void ClientCapabilitiesGateOnDatabaseAndServerSupport()
        {
            var withDatabase = new OceanBaseConnectionStringBuilder { Server = "s", UserId = "u", Database = "d" }.Build();
            var capabilities = ClientCapabilities.Build(CapabilityFlags.ClientDeprecateEof, withDatabase);

            Assert.True(capabilities.HasFlag(CapabilityFlags.ClientConnectWithDb));
            Assert.True(capabilities.HasFlag(CapabilityFlags.ClientDeprecateEof));
        }

        [Fact]
        public void EffectiveCapabilitiesAreTheIntersection()
        {
            var requested = CapabilityFlags.ClientSessionTrack | CapabilityFlags.ClientSupportOracle;
            var server = CapabilityFlags.ClientSessionTrack;
            var negotiated = new NegotiatedCapabilities(requested, server);

            Assert.Equal(requested, negotiated.Requested);
            Assert.Equal(server, negotiated.Server);
            Assert.Equal(CapabilityFlags.ClientSessionTrack, negotiated.Effective);
            Assert.True(negotiated.HasEffective(CapabilityFlags.ClientSessionTrack));
            Assert.False(negotiated.HasEffective(CapabilityFlags.ClientSupportOracle));
        }

        private static byte[] BuildMinimalHandshake(string serverVersion)
        {
            var payload = new byte[]
            {
                10,
            };
            var versionBytes = Encoding.ASCII.GetBytes(serverVersion);
            var packet = new byte[1 + versionBytes.Length + 1 + 4 + 8 + 1 + 2 + 1 + 2 + 2 + 1 + 10 + 4 + 13 + 1];
            var offset = 0;
            packet[offset++] = 10;
            versionBytes.CopyTo(packet, offset);
            offset += versionBytes.Length;
            packet[offset++] = 0;
            offset += 4; // connection id
            offset += 8; // seed1
            packet[offset++] = 0; // filler
            packet[offset++] = 0x00;
            packet[offset++] = 0x0A; // caps lower
            packet[offset++] = 45; // charset
            packet[offset++] = 0x02;
            packet[offset++] = 0x00; // status
            packet[offset++] = 0x00;
            packet[offset++] = 0x08; // caps upper (PLUGIN_AUTH etc.)
            packet[offset++] = 21;
            return packet;
        }
    }

    internal static class ByteArrayExtensions
    {
        public static byte[] Concat(this byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}
