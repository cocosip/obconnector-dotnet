using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class Sha2AuthenticationConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var path in new[] { "fast", "switch", "full", "sha256" })
                        yield return new object[] { target, asynchronous, path };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetAuthenticatesOrRefusesFullPasswordThroughPublicConnection(string target, bool asynchronous, string path)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var prefixLength = greeting.Length - Encoding.ASCII.GetByteCount("mysql_native_password\0");
            if (path != "switch")
            {
                var plugin = path == "sha256" ? "sha256_password\0" : "caching_sha2_password\0";
                var prefix = greeting.AsSpan(0, prefixLength).ToArray();
                greeting = new byte[prefixLength + Encoding.ASCII.GetByteCount(plugin)];
                prefix.CopyTo(greeting, 0); Encoding.ASCII.GetBytes(plugin).CopyTo(greeting, prefixLength);
            }
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(greeting, budget.Token);
                if (path == "sha256") { await AssertClosed(stream, budget.Token); return; }
                var response = await channel.ReadPacketAsync(budget.Token);
                if (path == "switch")
                {
                    using var packet = new MemoryStream(); packet.WriteByte(254);
                    packet.Write(Encoding.ASCII.GetBytes("caching_sha2_password\0"));
                    packet.Write(Convert.FromHexString("01020304050607081112131415161718191a1b1c")); packet.WriteByte(0);
                    await channel.WritePacketAsync(packet.ToArray(), budget.Token);
                    response = await channel.ReadPacketAsync(budget.Token);
                }
                else
                {
                    var cursor = new ByteCursor(response); cursor.Skip(32);
                    Assert.Equal("u", cursor.ReadNullTerminatedAscii()); response = cursor.ReadLengthEncodedBytes();
                    Assert.Equal("caching_sha2_password", cursor.ReadNullTerminatedAscii());
                }
                Assert.Equal(Convert.FromHexString("40d9e546b2cb35d699e9c5254222d2d9961b99dcc2b43f3656634f559e76779b"), response);
                await channel.WritePacketAsync(new byte[] { 1, path == "full" ? (byte)4 : (byte)3 }, budget.Token);
                if (path != "full")
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                    await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                    var command = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token);
                    Assert.Equal(3, command[0]);
                    Assert.Equal("update t set x=1", Encoding.UTF8.GetString(command, 1, command.Length - 1));
                    await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token);
                }
                await AssertClosed(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("sha2-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=secretpw;TLS Mode=Disabled;Connection Timeout=5";
                if (path == "full" || path == "sha256")
                {
                    var error = asynchronous ? await Assert.ThrowsAnyAsync<DbException>(() => connection.OpenAsync(budget.Token))
                        : Assert.ThrowsAny<DbException>(connection.Open);
                    Assert.Contains("VerifyFull", error.Message); Assert.DoesNotContain("secretpw", error.ToString());
                    Assert.Equal(ConnectionState.Closed, connection.State);
                }
                else
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    Assert.Equal(ConnectionState.Open, connection.State);
                    using var command = connection.CreateCommand(); command.CommandText = "update t set x=1";
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static async Task AssertClosed(Stream stream, CancellationToken token)
        {
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException) { /* Closing a refused connection may reset the socket. */ }
        }
    }
}
