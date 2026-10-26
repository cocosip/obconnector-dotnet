using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class DiscoveryBlacklistConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var path in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { 0, 1 } : new[] { 0, 1, 2, 3 })
                            foreach (var ocp in new[] { false, true }) yield return new object[] { target, oracle, asynchronous, path, ocp };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public Task EveryAssetAppliesBlacklistAndZeroBudgetAcrossPhysicalAndIdlePaths(string target, bool oracle, bool asynchronous, int path, bool ocp)
            => VerifyBlacklist(target, oracle, asynchronous, path, ocp, group: false);

        [Theory]
        [MemberData(nameof(Assets))]
        public Task EveryAssetAppliesGroupBlacklistAcrossPhysicalAndIdlePaths(string target, bool oracle, bool asynchronous, int path, bool ocp)
            => VerifyBlacklist(target, oracle, asynchronous, path, ocp, group: true);

        [Theory]
        [MemberData(nameof(Assets))]
        public Task EveryAssetAppliesGroupAttemptLimitAcrossPhysicalAndIdlePaths(string target, bool oracle, bool asynchronous, int path, bool ocp)
            => VerifyBlacklist(target, oracle, asynchronous, path, ocp, group: true, limited: true);

        [Theory]
        [MemberData(nameof(Assets))]
        public Task EveryAssetAppliesAddressBlacklistAcrossPhysicalAndIdlePaths(string target, bool oracle, bool asynchronous, int path, bool ocp)
            => VerifyBlacklist(target, oracle, asynchronous, path, ocp, group: true, address: true);

        private static async Task VerifyBlacklist(string target, bool oracle, bool asynchronous, int path, bool ocp, bool group, bool limited = false, bool address = false)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var bytes = Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!);
            await using var server = new CredentialServer(bytes, oracle: oracle, sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)refused.LocalEndPoint!).Port;
            string Tns(int cap)
            {
                var text = group ? GroupBlacklistFixture.Tns(port, server.Port, limited && cap == 0 ? 2 : cap, addressThreshold: address ? 3 : null) : DiscoveryBlacklistFixture.Tns(port, server.Port, cap);
                return limited ? text.Replace("(ADDRESS_LIST=", "(ADDRESS_LIST=(OBLB_RETRY_ALL_DOWNS=" + (cap == 0 ? 0 : 1) + ")") : text;
            }
            string Json(int cap)
            {
                var text = group ? GroupBlacklistFixture.Json(port, server.Port, limited && cap == 0 ? 2 : cap, addressThreshold: address ? 3 : null) : DiscoveryBlacklistFixture.Json(port, server.Port, cap);
                return limited ? text.Replace("\"ADDRESS\":[", "\"OBLB_RETRY_ALL_DOWNS\":" + (cap == 0 ? 0 : 1) + ",\"ADDRESS\":[") : text;
            }
            var repeatedFailures = group && !limited;
            var physicalBudget = address ? 4 : repeatedFailures ? 3 : 2;
            using var http = new OcpHttpFixture(Json(physicalBudget));
            using var file = new TnsFileFixture(Tns(physicalBudget));
            var context = new AssemblyLoadContext("discovery-blacklist-" + target, isCollectible: true);
            var pooled = path == 1 || path == 2;
            DbConnection? connection = null; DbDataSource? source = null;
            using var capture = new TcpAttemptCapture();
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var resolverType = assembly.GetType("OceanBaseConnector.OceanBase" + (ocp ? "Ocp" : "Tns") + "AddressResolver")!;
                var resolver = Activator.CreateInstance(resolverType, ocp ? new object?[] { http.Client, new Uri("https://ocp.example/"), "APP", null } : new object?[] { file.Path, "APP", null })!;
                var config = $"Server=localhost;User ID=u;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=2;Min Pool Size={(pooled ? 2 : 0)};Connection Timeout=15";
                if (path >= 2)
                {
                    var builderType = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                    var builder = Activator.CreateInstance(builderType, config)!;
                    builderType.GetMethod("UseAddressResolver")!.Invoke(builder, new[] { resolver });
                    source = (DbDataSource)builderType.GetMethod("Build")!.Invoke(builder, null)!; connection = source.CreateConnection();
                }
                else
                {
                    connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                    connection.ConnectionString = config; connection.GetType().GetProperty("AddressResolver")!.SetValue(connection, resolver);
                }
                await Open(connection, asynchronous);
                using (var command = connection.CreateCommand())
                {
                    // Discard one prewarmed lease: cap=0 must still allow the remaining valid idle session.
                    command.CommandText = pooled ? "SET @discard = 1" : "select blacklist";
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                connection.Close(); Assert.Equal(pooled ? 2 : 1, server.Accepted);
                file.Write(Tns(0)); http.Body = Json(0);
                if (pooled) { await Open(connection, asynchronous); connection.Close(); }
                else
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => Open(connection, asynchronous));
                    Assert.Equal("OceanBaseException", error.GetType().Name);
                }
                var initial = limited && pooled ? new[] { "error", "success", "error", "success" }
                    : Enumerable.Repeat("error", address ? 3 : repeatedFailures ? 2 : 1).Concat(Enumerable.Repeat("success", pooled ? 2 : 1)).ToArray();
                Assert.Equal(initial, capture.Outcomes);
                if (pooled) Clear(connection, source);
                file.Write(Tns(physicalBudget)); http.Body = Json(physicalBudget);
                await Open(connection, asynchronous);
                using (var command = connection.CreateCommand()) { command.CommandText = "select after policy change"; Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); }
                Assert.Equal(initial.Concat(pooled ? initial : limited ? new[] { "error", "success" } : new[] { "success" }), capture.Outcomes);
                Assert.Equal(pooled ? 4 : 2, server.Accepted); Assert.Equal(ocp ? 3 : 0, http.Requests.Count);
            }
            finally
            {
                connection?.Close(); if (source != null) source.Dispose(); else if (connection != null) Clear(connection, null);
                connection?.Dispose(); context.Unload();
            }
        }
        private static void Clear(DbConnection connection, DbDataSource? source) { if (source != null) source.GetType().GetMethod("Clear")!.Invoke(source, null); else connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection }); }
        private static Task Open(DbConnection connection, bool asynchronous) { if (asynchronous) return connection.OpenAsync(); connection.Open(); return Task.CompletedTask; }
    }
}
