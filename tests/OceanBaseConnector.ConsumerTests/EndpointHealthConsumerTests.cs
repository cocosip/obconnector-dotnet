using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class EndpointHealthConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var path in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { 0, 1 } : new[] { 0, 1, 2 })
                            yield return new object[] { target, oracle, asynchronous, path };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReusesEndpointHealthAcrossMatchingConnections(string target, bool oracle, bool asynchronous, int path)
        {
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new CredentialServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!), oracle: oracle,
                sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            var context = new AssemblyLoadContext("endpoint-health-" + target, isCollectible: true);
            DbConnection? first = null, second = null; DbDataSource? source = null;
            using var capture = new TcpAttemptCapture();
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                var configuration = $"Server=127.0.0.1:{((IPEndPoint)refused.LocalEndPoint!).Port},127.0.0.1:{server.Port};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Host Recheck Seconds=60;Pooling={path != 0};Max Pool Size=2";
                var builderType = assembly.GetType("OceanBaseConnector.OceanBaseConnectionStringBuilder")!;
                var builder = (DbConnectionStringBuilder)Activator.CreateInstance(builderType, configuration)!;
                Assert.Equal(60, builderType.GetProperty("HostRecheckSeconds")!.GetValue(builder));
                Assert.Equal("Host Recheck Seconds", builderType.GetField("HostRecheckSecondsKey")!.GetRawConstantValue());
                if (path == 2)
                {
                    var sourceBuilderType = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                    var sourceBuilder = Activator.CreateInstance(sourceBuilderType, builder.ConnectionString)!;
                    source = (DbDataSource)sourceBuilderType.GetMethod("Build")!.Invoke(sourceBuilder, null)!;
                    first = source.CreateConnection(); second = source.CreateConnection();
                }
                else
                {
                    first = (DbConnection)Activator.CreateInstance(type)!; first.ConnectionString = builder.ConnectionString;
                    second = path == 0 ? first : (DbConnection)Activator.CreateInstance(type)!;
                    if (path != 0) second.ConnectionString = builder.ConnectionString;
                }
                if (asynchronous) await first.OpenAsync(); else first.Open();
                if (path == 0) first.Close();
                if (asynchronous) await second.OpenAsync(); else second.Open();
                Assert.Equal(new[] { "error", "success", "success" }, capture.Outcomes);
                Assert.Equal(ConnectionState.Open, second.State); Assert.Equal(2, server.Accepted);
                using var command = second.CreateCommand(); command.CommandText = "health-selected connection works";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally
            {
                second?.Close(); first?.Close();
                if (source != null) source.Dispose();
                else if (first != null) first.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { first });
                second?.Dispose(); first?.Dispose(); context.Unload();
            }
        }
    }
}
