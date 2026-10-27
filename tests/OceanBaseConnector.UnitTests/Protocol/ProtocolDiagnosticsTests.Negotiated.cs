using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed partial class ProtocolDiagnosticsTests
    {
        public static IEnumerable<object[]> NegotiationCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var oracle in new[] { false, true })
                    foreach (var kind in new[] { "plain", "fallback", "compressed" })
                        yield return new object[] { asynchronous, oracle, kind };
        }

        [Theory]
        [MemberData(nameof(NegotiationCases))]
        public async Task AuthenticatedFramingReportsActualSelectionOnceAcrossPoolReuse(bool asynchronous, bool oracle, string kind)
        {
            using var capture = new Capture();
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=synthetic;Password=private-secret;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Use Compression={kind == "compressed"};Use OB20={kind == "fallback"};Enable Full Link Trace={kind == "fallback"}");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "select 'private-query'";
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                Assert.Equal(1, server.Authentications); Assert.Equal(2, server.Resets);
                var item = Assert.Single(capture.Events, e => e.Key == "OceanBaseConnector.Protocol.Negotiated");
                var fields = item.Value;
                Assert.Equal(kind == "compressed" ? "compressed" : "mysql", fields["Layer"]);
                Assert.Equal(kind == "compressed" ? "compressed" : kind == "fallback" ? "ob20" : "mysql", fields["RequestedLayer"]);
                Assert.Equal(kind == "fallback", fields["FullLinkTraceRequested"]);
                Assert.Equal(false, fields["FullLinkTraceEnabled"]); Assert.Equal(false, fields["ModernTraceFormat"]);
                Assert.Equal(new[] { "ChannelId", "FullLinkTraceEnabled", "FullLinkTraceRequested", "Layer", "ModernTraceFormat", "RequestedLayer" }, fields.Keys.OrderBy(x => x, StringComparer.Ordinal));
                Assert.IsType<long>(fields["ChannelId"]);
                Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)fields).Add("private", "secret"));
                foreach (var value in fields.Values)
                {
                    Assert.True(value is long or string or bool);
                    Assert.DoesNotContain("private", value?.ToString() ?? ""); Assert.DoesNotContain("secret", value?.ToString() ?? "");
                }
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false, "authentication")]
        [InlineData(true, "authentication")]
        [InlineData(false, "identity")]
        [InlineData(true, "identity")]
        [InlineData(false, "charset")]
        [InlineData(true, "charset")]
        public async Task FailedAuthenticationOrIdentityNeverReportsNegotiated(bool asynchronous, string stage)
        {
            using var capture = new Capture();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = LoopbackServer.Start(async peer =>
            {
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                if (stage == "authentication") await channel.WritePacketAsync(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48 }, budget.Token);
                else
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 }, budget.Token);
                    if (stage == "charset")
                    {
                        await ServerIdentity.ReplyAsync(channel, oracle: false, token: budget.Token);
                        var setup = await channel.ReadPacketAsync(budget.Token);
                        Assert.Equal("SET NAMES gbk", System.Text.Encoding.ASCII.GetString(setup.AsSpan(1)));
                        await channel.WritePacketAsync(new byte[] { 255, 21, 4, 35, 50, 56, 48, 48, 48 }, budget.Token);
                    }
                    else
                    {
                        channel.BeginCommand(); await channel.ReadPacketAsync(budget.Token);
                        await channel.WritePacketAsync(new byte[] { 0 }, budget.Token);
                    }
                }
            });
            try
            {
                using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=synthetic;TLS Mode=Disabled" + (stage == "charset" ? ";Character Set=gbk" : ""));
                await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync(budget.Token) : Task.Run(connection.Open, budget.Token));
                Assert.Equal(ConnectionState.Closed, connection.State);
                await server.Completion.WaitAsync(budget.Token);
                Assert.DoesNotContain(capture.Events, e => e.Key == "OceanBaseConnector.Protocol.Negotiated");
            }
            finally { server.Stop(); }
        }

        [Fact]
        public void DisabledNegotiationEventDoesNotAllocateDictionaries()
        {
            using var capture = new Capture(_ => false);
            using var wire = new System.IO.MemoryStream(); var channel = new PacketChannel(wire);
            for (var i = 0; i < 20; i++) channel.ReportNegotiated(false, true, true);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1024; i++) channel.ReportNegotiated(false, true, true);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
            Assert.Empty(capture.Events);
        }

        [Theory]
        [InlineData(false, "filter")]
        [InlineData(true, "filter")]
        [InlineData(false, "write")]
        [InlineData(true, "write")]
        [InlineData(false, "reenter")]
        [InlineData(true, "reenter")]
        public async Task NegotiationObserverFailureOrReentryPreservesAuthenticationAndActivity(bool asynchronous, string kind)
        {
            using var parent = new Activity("application").Start();
            var visits = 0;
            void Callback()
            {
                visits++; Activity.Current = new Activity("observer");
                if (kind != "reenter") throw new InvalidOperationException("private-observer-error");
                using var nested = new System.IO.MemoryStream(); new PacketChannel(nested).BeginCommand();
            }
            using var capture = new Capture(name =>
            {
                if (name != "OceanBaseConnector.Protocol.Negotiated") return false;
                if (kind == "filter") Callback();
                return true;
            }, kind == "filter" ? null : Callback);
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, false);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=synthetic;TLS Mode=Disabled");
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            Assert.Equal(ConnectionState.Open, connection.State); Assert.Same(parent, Activity.Current);
            Assert.Equal(1, visits);
            using var command = connection.CreateCommand(); command.CommandText = "select 1";
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            connection.Close();
        }
    }
}
