using System;
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
    public sealed class BatchConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        public void StandardAssetsDoNotExposeModernBatchTypes(string target)
        {
            var context = new AssemblyLoadContext("batch-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                foreach (var name in new[] { "OceanBaseBatch", "OceanBaseBatchCommand", "OceanBaseBatchCommandCollection", "OceanBaseBatchReader" }) Assert.Null(assembly.GetType("OceanBaseConnector." + name));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                Assert.False(connection.CanCreateBatch); Assert.Throws<NotSupportedException>(() => connection.CreateBatch());
            }
            finally { context.Unload(); }
        }

        [Theory]
        [InlineData("net8.0", false, false)]
        [InlineData("net8.0", false, true)]
        [InlineData("net8.0", true, false)]
        [InlineData("net8.0", true, true)]
        [InlineData("net9.0", false, false)]
        [InlineData("net9.0", false, true)]
        [InlineData("net9.0", true, false)]
        [InlineData("net9.0", true, true)]
        [InlineData("net10.0", false, false)]
        [InlineData("net10.0", false, true)]
        [InlineData("net10.0", true, false)]
        [InlineData("net10.0", true, true)]
        public async Task ModernAssetsConsumeConnectionAndDataSourceBatches(string target, bool asynchronous, bool dataSource)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token);
                for (var i = 0; i < 2; i++)
                {
                    channel.BeginCommand(); var request = await channel.ReadPacketAsync(budget.Token); Assert.Equal(3, request[0]);
                    Assert.Equal(i == 0 ? "one" : "two", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    var ok = (byte[])SessionTimeZoneFixture.Report.Clone(); ok[1] = (byte)(i + 2); await channel.WritePacketAsync(ok, budget.Token);
                }
                if (dataSource) Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], budget.Token));
            });
            var context = new AssemblyLoadContext("batch-" + target, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                Assert.True(factory.CanCreateBatch); using var unbound = factory.CreateBatch();
                using var connection = factory.CreateConnection()!; connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=secret;TLS Mode=Disabled";
                await using var source = factory.CreateDataSource(connection.ConnectionString);
                using var batch = dataSource ? source.CreateBatch() : connection.CreateBatch();
                if (!dataSource) await connection.OpenAsync(budget.Token);
                else
                {
                    Assert.DoesNotContain("secret", source.ConnectionString);
                    Assert.Throws<NotSupportedException>(() => batch.Connection); Assert.Throws<NotSupportedException>(() => batch.Transaction); Assert.Throws<NotSupportedException>(() => batch.Prepare());
                }
                var one = factory.CreateBatchCommand()!; one.CommandText = "one"; batch.BatchCommands.Add(one);
                var two = factory.CreateBatchCommand()!; two.CommandText = "two"; batch.BatchCommands.Add(two);
                Assert.True(one.CanCreateParameter); Assert.Equal(assembly, one.CreateParameter().GetType().Assembly);
                Assert.Equal(5, asynchronous ? await batch.ExecuteNonQueryAsync(budget.Token) : batch.ExecuteNonQuery());
                Assert.Equal(2, one.RecordsAffected); Assert.Equal(3, two.RecordsAffected);
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
