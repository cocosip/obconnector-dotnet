using System;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        public async Task Sha2FastAuthenticationWaitsForFinalOk(bool asynchronous, bool switchPlugin, bool emptyPassword)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: switchPlugin ? "mysql_native_password" : "caching_sha2_password"), budget.Token);
                var response = await channel.ReadPacketAsync(budget.Token);
                if (switchPlugin)
                {
                    await channel.WritePacketAsync(Sha2Switch("caching_sha2_password"), budget.Token);
                    response = await channel.ReadPacketAsync(budget.Token);
                }
                else
                {
                    var cursor = new ByteCursor(response); cursor.Skip(32);
                    Assert.Equal("u@sys", cursor.ReadNullTerminatedAscii());
                    response = cursor.ReadLengthEncodedBytes();
                }
                Assert.Equal(emptyPassword ? Array.Empty<byte>() : Sha2Token, response);
                await channel.WritePacketAsync(new byte[] { 1, 3 }, budget.Token);
                // A client packet here would break the shared packet sequence at final OK.
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true), budget.Token);
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), budget.Token);
                var channel = new PacketChannel(transport.Stream);
                var options = BuildOptions(password: emptyPassword ? "" : "secretpw");
                var result = asynchronous
                    ? await HandshakeFlow.AuthenticateAsync(transport, channel, options, budget.Token)
                    : HandshakeFlow.Authenticate(transport, channel, options);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
            }
            finally { budget.Cancel(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static byte[] Sha2Token => Convert.FromHexString("40d9e546b2cb35d699e9c5254222d2d9961b99dcc2b43f3656634f559e76779b");

        [Theory]
        [InlineData("sha256_password", "")]
        [InlineData("sha256_password", "päss🔑")]
        [InlineData("caching_sha2_password", "")]
        [InlineData("caching_sha2_password", "päss🔑")]
        public void Sha2VerifiedTlsDecisionEncodesCompletePassword(string plugin, string password)
        {
            // Decision test only: does not bypass TLS or claim a trusted loopback certificate chain.
            var conversation = new AuthenticationConversation(BuildOptions(password, OceanBaseTlsMode.VerifyFull), plugin, SeedBytes(SeedHex));
            var response = plugin == "sha256_password" ? conversation.InitialResponse : conversation.Continue(new byte[] { 1, 4 });
            Assert.Equal(Encoding.UTF8.GetBytes(password + "\0"), response);
            Assert.Throws<ProtocolException>(() => conversation.Continue(new byte[] { 1, 4 }));
            if (plugin == "caching_sha2_password")
                Assert.Throws<ProtocolException>(() => conversation.Continue(Sha2Switch("mysql_native_password")));
            else
            {
                // Initial plugin negotiation can still select the account's actual plugin.
                Assert.NotNull(conversation.Continue(Sha2Switch("mysql_native_password")));
                Assert.Throws<ProtocolException>(() => conversation.Continue(Sha2Switch("mysql_native_password")));
            }
        }

        [Theory]
        [InlineData("sha256_password", OceanBaseTlsMode.Disabled)]
        [InlineData("sha256_password", OceanBaseTlsMode.Required)]
        [InlineData("caching_sha2_password", OceanBaseTlsMode.Disabled)]
        [InlineData("caching_sha2_password", OceanBaseTlsMode.Required)]
        public void Sha2CompletePasswordRequiresIdentityVerifiedTls(string plugin, OceanBaseTlsMode tls)
        {
            var error = Assert.Throws<OceanBaseException>(() =>
            {
                var conversation = new AuthenticationConversation(BuildOptions(tlsMode: tls), plugin, SeedBytes(SeedHex));
                conversation.Continue(new byte[] { 1, 4 });
            });
            Assert.Contains("VerifyFull", error.Message); Assert.DoesNotContain("secretpw", error.ToString());
        }

        [Theory]
        [InlineData("sha256_password", false)]
        [InlineData("sha256_password", true)]
        [InlineData("caching_sha2_password", false)]
        [InlineData("caching_sha2_password", true)]
        public void Sha2RejectsInvalidPasswordsWithoutEcho(string plugin, bool nul)
        {
            var password = nul ? "hidden\0secret" : "hidden\ud800secret";
            void Create() => _ = new AuthenticationConversation(BuildOptions(password, OceanBaseTlsMode.VerifyFull), plugin, SeedBytes(SeedHex));
            Exception error = nul ? Assert.Throws<ArgumentException>(Create) : Assert.Throws<OceanBaseException>(Create);
            Assert.DoesNotContain("hidden", error.ToString()); Assert.DoesNotContain("secret", error.ToString());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(19)]
        [InlineData(21)]
        public void Sha2RejectsMalformedChallengeBeforeResponding(int length)
        {
            Assert.Throws<ProtocolException>(() => new AuthenticationConversation(BuildOptions(), "caching_sha2_password", new byte[length]));
            var conversation = new AuthenticationConversation(BuildOptions(), "mysql_native_password", SeedBytes(SeedHex));
            using var packet = new MemoryStream(); packet.WriteByte(254);
            packet.Write(Encoding.ASCII.GetBytes("caching_sha2_password\0")); packet.Write(new byte[length]); packet.WriteByte(0);
            Assert.Throws<ProtocolException>(() => conversation.Continue(packet.ToArray()));
        }

        [Theory]
        [InlineData(false, "01", false)]
        [InlineData(true, "01", false)]
        [InlineData(false, "0102", false)]
        [InlineData(true, "0102", false)]
        [InlineData(false, "010300", false)]
        [InlineData(true, "010300", false)]
        [InlineData(false, "010400", false)]
        [InlineData(true, "010400", false)]
        [InlineData(false, "02", false)]
        [InlineData(true, "02", false)]
        [InlineData(false, "FE", false)]
        [InlineData(true, "FE", false)]
        [InlineData(false, "", false)]
        [InlineData(true, "", false)]
        [InlineData(false, "0103", true)]
        [InlineData(true, "0103", true)]
        [InlineData(false, "0104", true)]
        [InlineData(true, "0104", true)]
        public async Task Sha2MalformedOrRepeatedContinuationBreaksSession(bool asynchronous, string hex, bool afterFast)
        {
            await Sha2RejectedExchange(asynchronous, "caching_sha2_password", async channel =>
            {
                if (afterFast) await channel.WritePacketAsync(new byte[] { 1, 3 });
                await channel.WritePacketAsync(Convert.FromHexString(hex));
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Sha2CannotSwitchAfterFastOrSwitchTwice(bool asynchronous, bool afterFast)
        {
            await Sha2RejectedExchange(asynchronous, "caching_sha2_password", async channel =>
            {
                if (afterFast) await channel.WritePacketAsync(new byte[] { 1, 3 });
                else
                {
                    await channel.WritePacketAsync(Sha2Switch("caching_sha2_password"));
                    Assert.Equal(Sha2Token, await channel.ReadPacketAsync());
                }
                await channel.WritePacketAsync(Sha2Switch("mysql_native_password"));
            });
        }

        [Theory]
        [InlineData(false, "sha256_password", false)]
        [InlineData(true, "sha256_password", false)]
        [InlineData(false, "sha256_password", true)]
        [InlineData(true, "sha256_password", true)]
        [InlineData(false, "caching_sha2_password", false)]
        [InlineData(true, "caching_sha2_password", false)]
        public async Task Sha2WeakChannelNeverSendsCompletePassword(bool asynchronous, string plugin, bool switchPlugin)
        {
            await Sha2RejectedExchange(asynchronous, switchPlugin ? "mysql_native_password" : plugin,
                plugin == "sha256_password" && !switchPlugin ? null : async channel =>
                {
                    await channel.WritePacketAsync(switchPlugin ? Sha2Switch(plugin) : new byte[] { 1, 4 });
                }, "VerifyFull");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sha2FastSuccessStillRequiresFinalAuthenticationResult(bool asynchronous)
        {
            await Sha2RejectedExchange(asynchronous, "caching_sha2_password", async channel =>
            {
                await channel.WritePacketAsync(new byte[] { 1, 3 });
                await channel.WritePacketAsync(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48, 115, 101, 99, 114, 101, 116, 112, 119 });
            }, "1045");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sha2MoreDataIsNotAcceptedForNativePassword(bool asynchronous)
        {
            await Sha2RejectedExchange(asynchronous, "mysql_native_password", channel => channel.WritePacketAsync(new byte[] { 1, 3 }));
        }

        [Theory]
        [InlineData(false, "fast")]
        [InlineData(true, "fast")]
        [InlineData(false, "full")]
        [InlineData(true, "full")]
        [InlineData(false, "sha256")]
        [InlineData(true, "sha256")]
        [InlineData(false, "switch")]
        [InlineData(true, "switch")]
        public async Task Sha2RequiredTlsEncryptsFastButRefusesCompletePassword(bool asynchronous, string path)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: path == "sha256" ? "sha256_password" : "caching_sha2_password"));
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var certificate = TestCertificateFactory.CreateSelfSigned();
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(certificate);
                channel.ReplaceUnderlyingStream(tls);
                if (path != "sha256")
                {
                    var response = await channel.ReadPacketAsync();
                    var cursor = new ByteCursor(response); cursor.Skip(32); cursor.ReadNullTerminatedAscii();
                    Assert.Equal(Sha2Token, cursor.ReadLengthEncodedBytes());
                    if (path == "fast")
                    {
                        await channel.WritePacketAsync(new byte[] { 1, 3 });
                        await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
                    }
                    else await channel.WritePacketAsync(path == "switch" ? Sha2Switch("sha256_password") : new byte[] { 1, 4 });
                }
                Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
            });
            using var session = Sha2Session(server.Port, OceanBaseTlsMode.Required);
            try
            {
                if (path == "fast")
                {
                    if (asynchronous) await session.OpenAsync(); else session.Open();
                    Assert.Equal(SessionState.Open, session.State);
                }
                else
                {
                    var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync())
                        : Assert.Throws<OceanBaseException>(() => session.Open());
                    Assert.Contains("VerifyFull", error.Message); Assert.DoesNotContain("secretpw", error.ToString());
                    Assert.Equal(SessionState.Broken, session.State);
                }
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(true, "cancel")]
        public async Task Sha2FinalOkWaitHonorsWholeConnectionBudgetAndInterruption(bool asynchronous, string interruption)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: "caching_sha2_password"));
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(new byte[] { 1, 3 });
                waiting.SetResult(); Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            using var session = Sha2Session(server.Port, timeout: interruption == "timeout" ? 1 : 10);
            using var caller = new CancellationTokenSource();
            try
            {
                var opening = asynchronous ? session.OpenAsync(caller.Token) : Task.Run(session.Open);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (interruption == "close") session.Close();
                if (interruption == "cancel") caller.Cancel();
                if (interruption == "timeout")
                {
                    await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.True(opening.IsFaulted, "Open must time out before the test watchdog.");
                }
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.NotEqual(SessionState.Open, session.State); Assert.Null(session.Info);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static PhysicalSession Sha2Session(int port, OceanBaseTlsMode tls = OceanBaseTlsMode.Disabled, int timeout = 5)
            => new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = port, UserId = "u@sys", Password = "secretpw", TlsMode = tls, ConnectionTimeout = timeout,
            }.Build());

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sha2TruncatedFinalPacketBreaksSession(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: "caching_sha2_password"));
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(new byte[] { 1, 3 });
                await stream.WriteAsync(new byte[] { 7, 0, 0, 3, 0, 0 });
            });
            using var session = Sha2Session(server.Port);
            try
            {
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync())
                    : Assert.Throws<OceanBaseException>(session.Open);
                Assert.Equal(SessionState.Broken, session.State); Assert.False(error.IsExecutionOutcomeUnknown);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Sha2FastAuthenticationPreservesModeGates(bool asynchronous, bool oracleBit)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: "caching_sha2_password"));
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(new byte[] { 1, 3 });
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: oracleBit));
                Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), default);
                var channel = new PacketChannel(transport.Stream); var options = BuildOptions(expectedMode: OceanBaseCompatibilityMode.MySql);
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => HandshakeFlow.AuthenticateAsync(transport, channel, options, default))
                    : Assert.Throws<OceanBaseException>(() => HandshakeFlow.Authenticate(transport, channel, options));
                Assert.Contains(oracleBit ? "ExpectedMode" : "verified", error.Message);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task Sha2RejectedExchange(bool asynchronous, string plugin, Func<PacketChannel, Task>? challenge, string? message = null)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: plugin));
                if (challenge is not null)
                {
                    await channel.ReadPacketAsync(); await challenge(channel);
                }
                Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u@sys", Password = "secretpw", TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5,
            }.Build());
            try
            {
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync())
                    : Assert.Throws<OceanBaseException>(() => session.Open());
                Assert.DoesNotContain("secretpw", error.ToString()); Assert.False(error.IsExecutionOutcomeUnknown);
                if (message is not null) Assert.Contains(message, error.Message);
                if (message == "1045") { Assert.Equal(1045, error.ServerErrorCode); Assert.Equal("28000", error.SqlState); }
                Assert.Equal(SessionState.Broken, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static byte[] Sha2Switch(string plugin)
        {
            using var buffer = new MemoryStream(); buffer.WriteByte(254);
            buffer.Write(Encoding.ASCII.GetBytes(plugin)); buffer.WriteByte(0);
            buffer.Write(SeedBytes(SeedHex)); buffer.WriteByte(0);
            return buffer.ToArray();
        }
    }
}
