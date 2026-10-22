using System;
using System.Linq;
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
        private const string EdSignature = "15181d233cb3f7c3f7457e36e3d7b7412fc5d3a473a3086de5a814a14345aa6e3ab148d593ba6dcfd7f64679039f19fb35a845a7f053ed46c73c01451769bd03";
        private const string EdZeroSignature = "d5bf1c70bba0d083ea0395bd8118fe8cb18476969e62f85265ee3f417be05707bc08d101d3b955cea653b42ef203a6e4c952d2c896692ef86169d37656005b0a";
        private static byte[] EdChallenge(bool trailingZero = false)
        {
            var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            if (trailingZero) seed[31] = 0;
            return seed;
        }

        [Theory]
        [InlineData("secretpw", EdSignature)]
        [InlineData("päss🔑", "2ffea4b8101ad8cdeaf2a0e8658104c80aab57b537873965e250b0a4bb5cc3cf1446a6ea103bd462da4d3f2006024f12b0c296c89f613d232a50f2b930d90002")]
        [InlineData("a", "5a2687b92d9d4d5c8254d304f53d31955d49150f4fb1642eaa04195da55c2b2a44af2f5aca0cc216d51edd5af146212bf551ede27a9a17a35fcdb54e7bce6909")]
        [InlineData("", "")]
        public void Ed25519MatchesFixedJavaSignatures(string password, string expected)
        {
            var conversation = new AuthenticationConversation(BuildOptions(password), "client_ed25519", EdChallenge());
            Assert.Equal(Convert.FromHexString(expected), conversation.InitialResponse);
            conversation.ValidateSuccess();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ed25519SwitchPreservesBinaryChallengeIncludingTrailingZero(bool trailingZero)
        {
            var conversation = new AuthenticationConversation(BuildOptions(), "mysql_native_password", SeedBytes(SeedHex));
            var packet = new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("client_ed25519\0")).Concat(EdChallenge(trailingZero)).ToArray();
            Assert.Equal(Convert.FromHexString(trailingZero ? EdZeroSignature : EdSignature), conversation.Continue(packet));
            Assert.Equal("client_ed25519", conversation.PluginName);
            Assert.Throws<ProtocolException>(() => conversation.Continue(packet));
            Assert.Throws<ProtocolException>(() => conversation.Continue(new byte[] { 1, 3 }));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(20)]
        [InlineData(31)]
        [InlineData(33)]
        public void Ed25519RejectsWrongChallengeLength(int length)
        {
            Assert.Throws<ProtocolException>(() => new AuthenticationConversation(BuildOptions(), "client_ed25519", new byte[length]));
        }

        [Fact]
        public void Ed25519RejectsInvalidUnicodeWithoutEcho()
        {
            var error = Assert.Throws<OceanBaseException>(() => new AuthenticationConversation(BuildOptions("hidden\ud800secret"), "client_ed25519", EdChallenge()));
            Assert.Contains("Unicode", error.Message);
            Assert.DoesNotContain("hidden", error.ToString()); Assert.DoesNotContain("secret", error.ToString());
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Ed25519AuthenticatesThroughInitialAndSwitchHandshake(bool asynchronous, bool switchPlugin)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: switchPlugin ? "mysql_native_password" : "client_ed25519",
                    seed: switchPlugin ? SeedBytes(SeedHex) : EdChallenge()), budget.Token);
                var response = await channel.ReadPacketAsync(budget.Token);
                if (switchPlugin)
                {
                    var packet = new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("client_ed25519\0")).Concat(EdChallenge(trailingZero: true)).ToArray();
                    await channel.WritePacketAsync(packet, budget.Token); response = await channel.ReadPacketAsync(budget.Token);
                }
                else
                {
                    var cursor = new ByteCursor(response); cursor.Skip(32); Assert.Equal("u@sys", cursor.ReadNullTerminatedAscii());
                    response = cursor.ReadLengthEncodedBytes(); Assert.Equal("client_ed25519", cursor.ReadNullTerminatedAscii());
                }
                Assert.Equal(Convert.FromHexString(switchPlugin ? EdZeroSignature : EdSignature), response);
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true), budget.Token);
                await ServerIdentity.ReplyAsync(channel);
                Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            try
            {
                using var transport = await TcpTransport.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(10), budget.Token);
                var channel = new PacketChannel(transport.Stream);
                var result = asynchronous ? await HandshakeFlow.AuthenticateAsync(transport, channel, BuildOptions(), budget.Token)
                    : HandshakeFlow.Authenticate(transport, channel, BuildOptions());
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, result.Mode);
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(budget.Token);
        }

        [Theory]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(true, "cancel")]
        public async Task Ed25519SignatureStillRequiresFinalResultWithinConnectionBudget(bool asynchronous, string interruption)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: "client_ed25519", seed: EdChallenge()));
                var response = await channel.ReadPacketAsync();
                var cursor = new ByteCursor(response); cursor.Skip(32); cursor.ReadNullTerminatedAscii();
                Assert.Equal(Convert.FromHexString(EdSignature), cursor.ReadLengthEncodedBytes());
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

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Ed25519FinalErrorsNeverPublishAnOpenSession(bool asynchronous, bool serverError)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = LoopbackServer.Start(async client =>
            {
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(BuildInitialHandshake(pluginName: "client_ed25519", seed: EdChallenge()), budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(serverError ? new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48, 115, 101, 99, 114, 101, 116, 112, 119 }
                    : new byte[] { 1, 3 }, budget.Token);
                Assert.Equal(0, await stream.ReadUpToAsync(new byte[1], 1));
            });
            using var session = Sha2Session(server.Port);
            try
            {
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync(budget.Token))
                    : Assert.Throws<OceanBaseException>(session.Open);
                Assert.DoesNotContain("secretpw", error.ToString()); Assert.False(error.IsExecutionOutcomeUnknown);
                if (serverError) { Assert.Equal(1045, error.ServerErrorCode); Assert.Equal("28000", error.SqlState); }
                else Assert.IsType<ProtocolException>(error.InnerException);
                Assert.Equal(SessionState.Broken, session.State); Assert.Null(session.Info);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(budget.Token);
        }
    }
}
