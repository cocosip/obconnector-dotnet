using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class RsaAuthenticationConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var plugin in new[] { "sha256_password", "caching_sha2_password" })
                        foreach (var retrieval in new[] { false, true })
                            yield return new object[] { target, asynchronous, plugin, retrieval };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetAuthenticatesRsaAndExecutesThroughPublicApis(string target, bool asynchronous, string plugin, bool retrieval)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var rsa = RSA.Create(2048); var pem = rsa.ExportSubjectPublicKeyInfoPem();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var original = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var prefixLength = original.Length - Encoding.ASCII.GetByteCount("mysql_native_password\0");
            var greeting = new byte[prefixLength + Encoding.ASCII.GetByteCount(plugin) + 1];
            Buffer.BlockCopy(original, 0, greeting, 0, prefixLength); Encoding.ASCII.GetBytes(plugin).CopyTo(greeting, prefixLength);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(greeting, budget.Token);
                var handshake = await channel.ReadPacketAsync(budget.Token);
                var cursor = new ByteCursor(handshake); cursor.Skip(32); Assert.Equal("u", cursor.ReadNullTerminatedAscii());
                var response = cursor.ReadLengthEncodedBytes(); Assert.Equal(plugin, cursor.ReadNullTerminatedAscii());
                if (plugin == "caching_sha2_password")
                {
                    Assert.Equal(32, response.Length); await channel.WritePacketAsync(new byte[] { 1, 4 }, budget.Token);
                    response = await channel.ReadPacketAsync(budget.Token);
                }
                if (retrieval)
                {
                    Assert.Equal(new byte[] { plugin == "sha256_password" ? (byte)1 : (byte)2 }, response);
                    var key = Encoding.ASCII.GetBytes(pem + "\0"); var packet = new byte[key.Length + 1]; packet[0] = 1; key.CopyTo(packet, 1);
                    await channel.WritePacketAsync(packet, budget.Token); response = await channel.ReadPacketAsync(budget.Token);
                }
                var decoded = rsa.Decrypt(response, RSAEncryptionPadding.OaepSHA1);
                var seed = Convert.FromHexString("01020304050607081112131415161718191a1b1c");
                for (var i = 0; i < decoded.Length; i++) decoded[i] ^= seed[i % seed.Length];
                Assert.Equal(Encoding.UTF8.GetBytes("密码🔑\0"), decoded);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                var command = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
                Assert.Equal(3, command[0]); Assert.Equal("update t set x=1", Encoding.UTF8.GetString(command, 1, command.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token);
                try { Assert.Equal(0, await stream.ReadAsync(new byte[1], budget.Token)); }
                catch (IOException) { /* A peer close may reset the socket. */ }
            });
            var context = new AssemblyLoadContext("rsa-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                var builder = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port, ["User ID"] = "u",
                    ["Password"] = "密码🔑", ["TLS Mode"] = "Disabled", ["Allow Public Key Retrieval"] = retrieval,
                };
                if (!retrieval) builder["Server RSA Public Key"] = pem;
                connection.ConnectionString = builder.ConnectionString;
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.DoesNotContain("PUBLIC KEY", connection.ConnectionString); Assert.DoesNotContain("密码", connection.ConnectionString);
                using var command = connection.CreateCommand(); command.CommandText = "update t set x=1";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
