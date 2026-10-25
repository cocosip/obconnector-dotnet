using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class DiscoveryPolicyConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var path in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { 0, 1 } : new[] { 0, 1, 2, 3 })
                            foreach (var ocp in new[] { false, true })
                                foreach (var weighted in new[] { false, true })
                                    yield return new object[] { target, oracle, asynchronous, path, ocp, weighted };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetUsesGroupPriorityAndWeightsForPhysicalConnections(string target, bool oracle, bool asynchronous, int path, bool ocp, bool weighted)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var bytes = Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!);
            await using var first = new CredentialServer(bytes, oracle: oracle, sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            await using var second = new CredentialServer(bytes, oracle: oracle, sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            using var http = new OcpHttpFixture(DiscoveryPolicyFixture.Json(first.Port, second.Port, weighted));
            using var file = new TnsFileFixture(DiscoveryPolicyFixture.Tns(first.Port, second.Port, weighted));
            var context = new AssemblyLoadContext("discovery-policy-" + target, isCollectible: true);
            DbConnection? connection = null; DbDataSource? source = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var resolverType = assembly.GetType("OceanBaseConnector.OceanBase" + (ocp ? "Ocp" : "Tns") + "AddressResolver")!;
                Assert.True(resolverType.IsPublic && resolverType.IsSealed);
                var resolver = Activator.CreateInstance(resolverType, ocp ? new object?[] { http.Client, new Uri("https://ocp.example/"), "APP", null } : new object?[] { file.Path, "APP", null })!;
                var configuration = $"Server=localhost;Port=1;User ID=u;TLS Mode=Disabled;Pooling={path == 1 || path == 2};Max Pool Size=2;Min Pool Size={(path == 1 || path == 2 ? 2 : 0)};Connection Timeout=5;Load Balance=RoundRobin;Host Recheck Seconds=60";
                if (path >= 2)
                {
                    var builderType = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                    var builder = Activator.CreateInstance(builderType, configuration)!;
                    builderType.GetMethod("UseAddressResolver")!.Invoke(builder, new[] { resolver });
                    source = (DbDataSource)builderType.GetMethod("Build")!.Invoke(builder, null)!;
                    connection = source.CreateConnection();
                }
                else
                {
                    connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                    connection.ConnectionString = configuration;
                    connection.GetType().GetProperty("AddressResolver")!.SetValue(connection, resolver);
                }
                for (var i = 0; i < 3; i++)
                {
                    await Open(connection, asynchronous);
                    using var command = connection.CreateCommand(); command.CommandText = "select discovery policy";
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    connection.Close();
                    if (source != null) source.GetType().GetMethod("Clear")!.Invoke(source, null);
                    else connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                }
                var expected = path == 1 || path == 2 ? 6 : 3;
                Assert.Equal(weighted ? 0 : expected, first.Accepted);
                Assert.Equal(weighted ? expected : 0, second.Accepted);
                Assert.Equal(ocp ? 3 : 0, http.Requests.Count);
            }
            finally
            {
                connection?.Close();
                if (source != null) source.Dispose();
                else connection?.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                connection?.Dispose(); context.Unload();
            }
        }

        private static Task Open(DbConnection connection, bool asynchronous)
        {
            if (asynchronous) return connection.OpenAsync();
            connection.Open(); return Task.CompletedTask;
        }
    }
}
