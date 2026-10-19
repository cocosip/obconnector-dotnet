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
    public sealed class OldPasswordAuthenticationConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var path in new[] { "initial", "named", "legacy" })
                        yield return new object[] { target, asynchronous, path };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetRefusesOldPasswordOnUnverifiedChannels(string target, bool asynchronous, string path)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            if (path == "initial")
            {
                var prefixLength = greeting.Length - Encoding.ASCII.GetByteCount("mysql_native_password\0");
                var replacement = new byte[prefixLength + Encoding.ASCII.GetByteCount("mysql_old_password\0")];
                Buffer.BlockCopy(greeting, 0, replacement, 0, prefixLength);
                Encoding.ASCII.GetBytes("mysql_old_password\0").CopyTo(replacement, prefixLength); greeting = replacement;
            }
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await channel.WritePacketAsync(greeting, budget.Token);
                if (path != "initial")
                {
                    await channel.ReadPacketAsync(budget.Token);
                    var request = path == "legacy" ? new byte[] { 254 } :
                        Encoding.ASCII.GetBytes("\0mysql_old_password\0abcdefgh\0");
                    request[0] = 254;
                    await channel.WritePacketAsync(request, budget.Token);
                }
                try { Assert.Equal(0, await stream.ReadAsync(new byte[1], budget.Token)); }
                catch (IOException) { /* Refusing authentication may reset the peer socket. */ }
            });
            var context = new AssemblyLoadContext("old-password-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=hidden-secret;TLS Mode=Disabled;Connection Timeout=5";
                var error = asynchronous ? await Assert.ThrowsAnyAsync<DbException>(() => connection.OpenAsync(budget.Token))
                    : Assert.ThrowsAny<DbException>(connection.Open);
                Assert.Contains("VerifyFull", error.Message); Assert.DoesNotContain("hidden-secret", error.ToString());
                Assert.Equal(ConnectionState.Closed, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
