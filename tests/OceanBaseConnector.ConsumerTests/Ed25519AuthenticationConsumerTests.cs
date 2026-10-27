using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
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
    public sealed class Ed25519AuthenticationConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var switchPlugin in new[] { false, true })
                        foreach (var password in new[] { "secretpw", "päss🔑", "" })
                            yield return new object[] { target, asynchronous, switchPlugin, password };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetAuthenticatesEd25519AndExecutes(string target, bool asynchronous, bool switchPlugin, string password)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var challenge = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            if (!switchPlugin)
            {
                var seedStart = Array.IndexOf(greeting, (byte)0, 1) + 5;
                // The fixed native greeting has 8+12 challenge bytes and one terminator.
                var secondSeedStart = greeting.Length - Encoding.ASCII.GetByteCount("mysql_native_password\0") - 13;
                using var packet = new MemoryStream(); packet.Write(greeting, 0, secondSeedStart);
                packet.Write(challenge, 8, 24); packet.WriteByte(0); packet.Write(Encoding.ASCII.GetBytes("client_ed25519\0"));
                greeting = packet.ToArray(); Buffer.BlockCopy(challenge, 0, greeting, seedStart, 8); greeting[seedStart + 16] = 33;
            }
            var expected = password.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(password == "secretpw"
                ? "15181d233cb3f7c3f7457e36e3d7b7412fc5d3a473a3086de5a814a14345aa6e3ab148d593ba6dcfd7f64679039f19fb35a845a7f053ed46c73c01451769bd03"
                : "2ffea4b8101ad8cdeaf2a0e8658104c80aab57b537873965e250b0a4bb5cc3cf1446a6ea103bd462da4d3f2006024f12b0c296c89f613d232a50f2b930d90002");
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(greeting, budget.Token);
                var response = await channel.ReadPacketAsync(budget.Token);
                if (switchPlugin)
                {
                    var packet = new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("client_ed25519\0")).Concat(challenge).ToArray();
                    await channel.WritePacketAsync(packet, budget.Token); response = await channel.ReadPacketAsync(budget.Token);
                }
                else
                {
                    var cursor = new ByteCursor(response); cursor.Skip(32); Assert.Equal("u", cursor.ReadNullTerminatedAscii());
                    response = cursor.ReadLengthEncodedBytes(); Assert.Equal("client_ed25519", cursor.ReadNullTerminatedAscii());
                }
                Assert.Equal(expected, response);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                var command = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
                Assert.Equal(3, command[0]); Assert.Equal("update t set x=1", Encoding.UTF8.GetString(command, 1, command.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token);
                try { Assert.Equal(0, await stream.ReadAsync(new byte[1], budget.Token)); }
                catch (IOException) { /* A peer close may reset the socket. */ }
            });
            var context = new AssemblyLoadContext("ed25519-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                var builder = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1",
                    ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u",
                    ["Password"] = password,
                    ["TLS Mode"] = "Disabled",
                    ["Connection Timeout"] = 10,
                };
                connection.ConnectionString = builder.ConnectionString;
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.False(new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString }.ContainsKey("Password"));
                using var command = connection.CreateCommand(); command.CommandText = "update t set x=1";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
