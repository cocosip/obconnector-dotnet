using System;
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
    public sealed class SequentialConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net8.0")]
        [InlineData("net9.0")]
        [InlineData("net10.0")]
        public async Task EachAssetStreamsFieldsAndReleasesConsumedSession(string target)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token); channel.BeginCommand();
                Assert.Equal(3, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 2 }, budget.Token);
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 118, 1, 118, 12, 63, 0, 255, 0, 0, 0, 252, 0, 0, 0, 0, 0 };
                await channel.WritePacketAsync(column, budget.Token); column[12] = 45; column[18] = 253;
                await channel.WritePacketAsync(column, budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                var text = Encoding.UTF8.GetBytes("\uFEFFA中😀Z"); using var row = new MemoryStream();
                row.Write(new byte[] { 5, 10, 11, 12, 13, 14 }); row.WriteByte((byte)text.Length); row.Write(text);
                await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                channel.BeginCommand(); Assert.Equal(3, (await SessionTimeZoneFixture.ReadCommand(channel, budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
            });
            var context = new AssemblyLoadContext("sequential-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token); using var command = connection.CreateCommand(); command.CommandText = "select streamed";
                using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token))
                {
                    Assert.True(reader.HasRows); Assert.Equal(2, reader.GetColumnSchema().Count); Assert.True(await reader.ReadAsync(budget.Token));
                    using var stream = await reader.GetFieldValueAsync<Stream>(0, budget.Token); var bytes = new byte[2];
                    await stream.ReadExactlyAsync(bytes, budget.Token); Assert.Equal(new byte[] { 10, 11 }, bytes);
                    using var chars = await reader.GetFieldValueAsync<TextReader>(1, budget.Token);
                    Assert.Equal("\uFEFFA中😀Z", await chars.ReadToEndAsync(budget.Token));
                    Assert.Throws<InvalidOperationException>(() => stream.ReadByte());
                    Assert.False(await reader.ReadAsync(budget.Token)); Assert.Throws<InvalidOperationException>(() => chars.Read());
                }
                command.CommandText = "after stream"; Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                Assert.Equal(ConnectionState.Open, connection.State); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
