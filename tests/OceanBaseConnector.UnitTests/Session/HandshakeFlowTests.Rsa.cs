using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        [Theory]
        [MemberData(nameof(RsaCases))]
        public async Task RsaAuthenticationUsesPinnedKeyOrExplicitRetrieval(bool asynchronous, string plugin, bool switchPlugin, bool retrieval)
        {
            using var rsa = RSA.Create(2048);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pem = rsa.ExportSubjectPublicKeyInfoPem();
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: switchPlugin ? "mysql_native_password" : plugin), budget.Token);
                var response = await channel.ReadPacketAsync(budget.Token);
                if (switchPlugin)
                {
                    await channel.WritePacketAsync(Sha2Switch(plugin), budget.Token);
                    response = await channel.ReadPacketAsync(budget.Token);
                }
                else
                {
                    var cursor = new ByteCursor(response); cursor.Skip(32); cursor.ReadNullTerminatedAscii();
                    response = cursor.ReadLengthEncodedBytes();
                }
                if (plugin == "caching_sha2_password")
                {
                    Assert.Equal(Sha2Token, response);
                    await channel.WritePacketAsync(new byte[] { 1, 4 }, budget.Token);
                    response = await channel.ReadPacketAsync(budget.Token);
                }
                if (retrieval)
                {
                    Assert.Equal(new byte[] { plugin == "sha256_password" ? (byte)1 : (byte)2 }, response);
                    var keyBytes = Encoding.ASCII.GetBytes(pem + (switchPlugin ? "\0" : ""));
                    var packet = new byte[keyBytes.Length + 1]; packet[0] = 1; keyBytes.CopyTo(packet, 1);
                    await channel.WritePacketAsync(packet, budget.Token);
                    response = await channel.ReadPacketAsync(budget.Token);
                }
                var password = rsa.Decrypt(response, RSAEncryptionPadding.OaepSHA1);
                var seed = SeedBytes(SeedHex);
                for (var i = 0; i < password.Length; i++) password[i] ^= seed[i % seed.Length];
                Assert.Equal(Encoding.UTF8.GetBytes("secretpw\0"), password);
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true), budget.Token);
            });
            try
            {
                var builder = new OceanBaseConnectionStringBuilder
                {
                    Server = "127.0.0.1", UserId = "u@sys", Password = "secretpw", TlsMode = OceanBaseTlsMode.Disabled,
                };
                builder.AllowPublicKeyRetrieval = true;
                if (!retrieval) builder.ServerRsaPublicKey = pem;
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), budget.Token);
                var channel = new PacketChannel(transport.Stream);
                var result = asynchronous ? await HandshakeFlow.AuthenticateAsync(transport, channel, builder.Build(), budget.Token)
                    : HandshakeFlow.Authenticate(transport, channel, builder.Build());
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
            }
            finally { budget.Cancel(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> RsaCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var plugin in new[] { "sha256_password", "caching_sha2_password" })
                    foreach (var switchPlugin in new[] { false, true })
                        foreach (var retrieval in new[] { false, true })
                            yield return new object[] { asynchronous, plugin, switchPlugin, retrieval };
        }

        [Fact]
        public void RsaOptionsFreezeRoundTripRedactAndCopyAcrossEndpoints()
        {
            using var rsa = RSA.Create(2048); var pem = rsa.ExportSubjectPublicKeyInfoPem();
            var builder = new OceanBaseConnectionStringBuilder("Server=one,two;User ID=u");
            Assert.False(builder.AllowPublicKeyRetrieval); Assert.Equal("", builder.ServerRsaPublicKey);
            builder["server rsa public key"] = pem; builder["allow public key retrieval"] = "TRUE";
            var options = builder.Build();
            var copy = options.ForEndpoint(options.Endpoints[1]);
            builder.ServerRsaPublicKey = "changed"; builder.AllowPublicKeyRetrieval = false;
            Assert.Equal(pem, options.ServerRsaPublicKey); Assert.True(options.AllowPublicKeyRetrieval);
            Assert.Equal(pem, copy.ServerRsaPublicKey); Assert.True(copy.AllowPublicKeyRetrieval);
            Assert.DoesNotContain("PUBLIC KEY", options.ToString()); Assert.DoesNotContain("PUBLIC KEY", copy.ToString());
            var roundTrip = new OceanBaseConnectionStringBuilder { Server = "one", UserId = "u", ServerRsaPublicKey = pem, AllowPublicKeyRetrieval = true };
            var parsed = new OceanBaseConnectionStringBuilder(roundTrip.ConnectionString);
            Assert.Equal(pem, parsed.ServerRsaPublicKey); Assert.True(parsed.AllowPublicKeyRetrieval);
            Assert.DoesNotContain("PUBLIC KEY", parsed.ToSanitizedConnectionString(false));
            Assert.Throws<ArgumentException>(() => parsed["Allow Public Key Retrieval"] = "1");
            var error = Assert.Throws<ArgumentException>(() => parsed.ServerRsaPublicKey = "sensitive-key-" + new string('x', 16384));
            Assert.DoesNotContain("sensitive-key", error.ToString());
            Assert.True(parsed.Remove("Server RSA Public Key")); Assert.Equal("", parsed.ServerRsaPublicKey);
        }

        [Theory]
        [InlineData(2048, "")]
        [InlineData(2048, "密码🔑")]
        [InlineData(2048, "long")]
        [InlineData(4096, "密码🔑")]
        public void RsaEncryptsStrictUtf8WithCorrectSeedAndOaepPayloadBoundary(int bits, string password)
        {
            using var rsa = RSA.Create(bits);
            if (password == "long") password = new string('a', bits / 8 - 43);
            var seed = SeedBytes(SeedHex); var clear = Encoding.UTF8.GetBytes(password + "\0");
            var cipher = RsaPassword.Encrypt(clear, seed, rsa.ExportSubjectPublicKeyInfoPem());
            Assert.Equal(bits / 8, cipher.Length);
            var decoded = rsa.Decrypt(cipher, RSAEncryptionPadding.OaepSHA1);
            for (var i = 0; i < decoded.Length; i++) decoded[i] ^= seed[i % seed.Length];
            Assert.Equal(clear, decoded);
            Assert.Equal(Encoding.UTF8.GetBytes(password + "\0"), clear);
            Assert.Throws<OceanBaseException>(() => RsaPassword.Encrypt(new byte[bits / 8 - 41], seed, rsa.ExportSubjectPublicKeyInfoPem()));
        }

        [Theory]
        [InlineData("sha256_password")]
        [InlineData("caching_sha2_password")]
        public void RsaVerifyFullTakesPriorityAndClearPluginStillRequiresVerifiedTls(string plugin)
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "host", UserId = "u", Password = "pw", TlsMode = OceanBaseTlsMode.VerifyFull,
                ServerRsaPublicKey = "invalid pin must be unused", AllowPublicKeyRetrieval = true,
            };
            var conversation = new AuthenticationConversation(builder.Build(), plugin, SeedBytes(SeedHex));
            var response = plugin == "sha256_password" ? conversation.InitialResponse : conversation.Continue(new byte[] { 1, 4 });
            Assert.Equal(new byte[] { 112, 119, 0 }, response);
            builder.TlsMode = OceanBaseTlsMode.Disabled;
            Assert.Throws<OceanBaseException>(() => new AuthenticationConversation(builder.Build(), "mysql_clear_password", SeedBytes(SeedHex)));
        }

        [Theory]
        [InlineData("sha256_password")]
        [InlineData("caching_sha2_password")]
        public void RsaInvalidPinNeverFallsBackToKeyRetrieval(string plugin)
        {
            var error = Assert.Throws<OceanBaseException>(() =>
            {
                var conversation = RsaConversation(plugin, "hidden-invalid-key");
                if (plugin == "caching_sha2_password") conversation.Continue(new byte[] { 1, 4 });
            });
            Assert.DoesNotContain("hidden-invalid-key", error.ToString()); Assert.Null(error.InnerException);
        }

        [Theory]
        [InlineData("sha256_password", "empty")]
        [InlineData("caching_sha2_password", "empty")]
        [InlineData("sha256_password", "header")]
        [InlineData("caching_sha2_password", "header")]
        [InlineData("caching_sha2_password", "switch")]
        [InlineData("sha256_password", "oversized")]
        [InlineData("caching_sha2_password", "oversized")]
        [InlineData("sha256_password", "unicode")]
        [InlineData("caching_sha2_password", "unicode")]
        [InlineData("sha256_password", "repeated")]
        [InlineData("caching_sha2_password", "repeated")]
        public void RsaRetrievalRejectsWrongPacketOrderAndMalformedKeys(string plugin, string kind)
        {
            var conversation = RsaConversation(plugin);
            if (plugin == "caching_sha2_password") Assert.Equal(new byte[] { 2 }, conversation.Continue(new byte[] { 1, 4 }));
            else Assert.Equal(new byte[] { 1 }, conversation.InitialResponse);
            Assert.Throws<ProtocolException>(conversation.ValidateSuccess);
            byte[] packet;
            switch (kind)
            {
                case "empty": packet = new byte[] { 1 }; break;
                case "header": packet = new byte[] { 2, 3 }; break;
                case "switch": packet = Sha2Switch("mysql_native_password"); break;
                case "oversized": packet = new byte[16387]; packet[0] = 1; break;
                case "unicode": packet = new byte[] { 1, 255 }; break;
                default:
                    using (var rsa = RSA.Create(2048))
                    {
                        var pem = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem());
                        packet = new byte[pem.Length + 1]; packet[0] = 1; pem.CopyTo(packet, 1);
                        Assert.NotNull(conversation.Continue(packet));
                        conversation.ValidateSuccess();
                    }
                    break;
            }
            Assert.Throws<ProtocolException>(() => conversation.Continue(packet));
        }

        private static AuthenticationConversation RsaConversation(string plugin, string pin = "")
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "host", UserId = "u", Password = "secretpw", TlsMode = OceanBaseTlsMode.Disabled,
                AllowPublicKeyRetrieval = true, ServerRsaPublicKey = pin,
            }.Build();
            return new AuthenticationConversation(options, plugin, SeedBytes(SeedHex));
        }

        [Theory]
        [InlineData(false, "sha256_password", false)]
        [InlineData(true, "sha256_password", false)]
        [InlineData(false, "caching_sha2_password", false)]
        [InlineData(true, "caching_sha2_password", false)]
        [InlineData(false, "sha256_password", true)]
        [InlineData(true, "sha256_password", true)]
        [InlineData(false, "caching_sha2_password", true)]
        [InlineData(true, "caching_sha2_password", true)]
        public async Task RsaKeyWaitRejectsEarlyOkAndPreservesServerError(bool asynchronous, string plugin, bool serverError)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: plugin));
                await channel.ReadPacketAsync();
                if (plugin == "caching_sha2_password") { await channel.WritePacketAsync(new byte[] { 1, 4 }); Assert.Equal(new byte[] { 2 }, await channel.ReadPacketAsync()); }
                await channel.WritePacketAsync(serverError
                    ? new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48, 115, 101, 99, 114, 101, 116, 112, 119 }
                    : BuildAuthOk(oracleMode: true));
                Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            using var session = RsaSession(server.Port);
            try
            {
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync())
                    : Assert.Throws<OceanBaseException>(session.Open);
                if (serverError) { Assert.Equal(1045, error.ServerErrorCode); Assert.Equal("28000", error.SqlState); }
                Assert.DoesNotContain("secretpw", error.ToString()); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(SessionState.Broken, session.State); Assert.Null(session.Info);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "timeout", "sha256_password")]
        [InlineData(true, "timeout", "sha256_password")]
        [InlineData(false, "close", "sha256_password")]
        [InlineData(true, "close", "sha256_password")]
        [InlineData(true, "cancel", "sha256_password")]
        [InlineData(false, "timeout", "caching_sha2_password")]
        [InlineData(true, "timeout", "caching_sha2_password")]
        [InlineData(false, "close", "caching_sha2_password")]
        [InlineData(true, "close", "caching_sha2_password")]
        [InlineData(true, "cancel", "caching_sha2_password")]
        public async Task RsaKeyWaitHonorsConnectionBudgetAndInterruption(bool asynchronous, string interruption, string plugin)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: plugin));
                await channel.ReadPacketAsync();
                if (plugin == "caching_sha2_password") { await channel.WritePacketAsync(new byte[] { 1, 4 }); await channel.ReadPacketAsync(); }
                waiting.SetResult(); Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            using var session = RsaSession(server.Port, interruption == "timeout" ? 1 : 10);
            using var caller = new CancellationTokenSource();
            try
            {
                var opening = asynchronous ? session.OpenAsync(caller.Token) : Task.Run(session.Open);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (interruption == "close") session.Close();
                if (interruption == "cancel") caller.Cancel();
                if (interruption == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.NotEqual(SessionState.Open, session.State); Assert.Null(session.Info);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static PhysicalSession RsaSession(int port, int timeout = 5) => new PhysicalSession(new OceanBaseConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = port, UserId = "u", Password = "secretpw", TlsMode = OceanBaseTlsMode.Disabled,
            AllowPublicKeyRetrieval = true, ConnectionTimeout = timeout,
        }.Build());

        [Theory]
        [InlineData(false, "mysql_native_password")]
        [InlineData(true, "mysql_native_password")]
        [InlineData(false, "caching_sha2_password")]
        [InlineData(true, "caching_sha2_password")]
        public async Task RsaInitialKeyRequestStillAllowsFirstAccountPluginSwitch(bool asynchronous, string accountPlugin)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: "sha256_password"));
                var cursor = new ByteCursor(await channel.ReadPacketAsync()); cursor.Skip(32); cursor.ReadNullTerminatedAscii();
                Assert.Equal(new byte[] { 1 }, cursor.ReadLengthEncodedBytes());
                await channel.WritePacketAsync(Sha2Switch(accountPlugin));
                Assert.Equal(accountPlugin == "mysql_native_password" ? ComputeScramble("secretpw", SeedBytes(SeedHex)) : Sha2Token,
                    await channel.ReadPacketAsync());
                if (accountPlugin == "caching_sha2_password") await channel.WritePacketAsync(new byte[] { 1, 3 });
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
            });
            using var session = RsaSession(server.Port);
            try
            {
                if (asynchronous) await session.OpenAsync(); else session.Open();
                Assert.Equal(SessionState.Open, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "sha256_password")]
        [InlineData(true, "sha256_password")]
        [InlineData(false, "caching_sha2_password")]
        [InlineData(true, "caching_sha2_password")]
        public async Task RsaEmptyPasswordUsesProtocolNoPasswordMarker(bool asynchronous, string plugin)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: plugin));
                var cursor = new ByteCursor(await channel.ReadPacketAsync()); cursor.Skip(32); cursor.ReadNullTerminatedAscii();
                Assert.Equal(plugin == "sha256_password" ? new byte[] { 0 } : Array.Empty<byte>(), cursor.ReadLengthEncodedBytes());
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
            });
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", TlsMode = OceanBaseTlsMode.Disabled,
                AllowPublicKeyRetrieval = true, ServerRsaPublicKey = "unused-invalid-pin", ConnectionTimeout = 5,
            };
            using var session = new PhysicalSession(builder.Build());
            try
            {
                if (asynchronous) await session.OpenAsync(); else session.Open();
                Assert.Equal(SessionState.Open, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
