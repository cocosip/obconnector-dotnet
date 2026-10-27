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
    public sealed class TnsAddressResolverConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var path in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { 0, 1 } : new[] { 0, 1, 2, 3 })
                            yield return new object[] { target, oracle, asynchronous, path };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetDiscoversUpdatedFilesBeforeReusingIdleSessions(string target, bool oracle, bool asynchronous, int path)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var bytes = Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!);
            await using var first = new CredentialServer(bytes, oracle: oracle, sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            await using var second = new CredentialServer(bytes, oracle: oracle, sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            using var file = new TnsFileFixture(Entry(first.Port));
            var context = new AssemblyLoadContext("tns-discovery-" + target, isCollectible: true);
            DbConnection? connection = null; DbDataSource? source = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var resolverType = assembly.GetType("OceanBaseConnector.OceanBaseTnsAddressResolver")!;
                Assert.True(resolverType.IsPublic && resolverType.IsSealed);
                var resolver = Activator.CreateInstance(resolverType, new object?[] { file.Path, "app", "tenant" })!;
                var configuration = $"Server=localhost;Port=1;User ID=u;TLS Mode=Disabled;Pooling={path == 1 || path == 2};Max Pool Size=1;Connection Timeout=5";
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
                await Open(connection, asynchronous); connection.Close();
                File.Delete(file.Path);
                await Assert.ThrowsAnyAsync<DbException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Equal(1, first.Accepted);
                file.Write(Entry(second.Port)); await Open(connection, asynchronous);
                Assert.Equal(1, first.Accepted); Assert.Equal(1, second.Accepted);
                using var command = connection.CreateCommand(); command.CommandText = "select file discovered";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally
            {
                connection?.Close();
                if (source != null) source.Dispose();
                else connection?.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                connection?.Dispose(); context.Unload();
            }
        }

        private static string Entry(int port)
        {
            var entry = TnsFileFixture.Entry(port);
            return entry.Insert(entry.Length - 1, "(CONNECT_DATA=(SERVICE_NAME=tenant))");
        }

        private static Task Open(DbConnection connection, bool asynchronous)
        {
            if (asynchronous) return connection.OpenAsync();
            connection.Open(); return Task.CompletedTask;
        }
    }
}
