using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed class PoolClearingConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var all in new[] { false, true }) yield return new object[] { target, asynchronous, all };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetClearsBorrowedAndIdleOrdinarySessions(string target, bool asynchronous, bool all)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var context = new AssemblyLoadContext("pool-clear-" + target, isCollectible: true);
            DbConnection? connection = null;
            MethodInfo? clear = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                clear = type.GetMethod("ClearPool", BindingFlags.Public | BindingFlags.Static)!; Assert.NotNull(clear);
                var clearAll = type.GetMethod("ClearAllPools", BindingFlags.Public | BindingFlags.Static)!; Assert.NotNull(clearAll);
                connection = Connection(type, Configuration(server.Port), certificate);
                await Open(connection, asynchronous);
                if (all) clearAll.Invoke(null, null); else clear.Invoke(null, new object[] { connection });
                await Query(connection); await Close(connection, asynchronous);
                Assert.Equal(0, server.Resets);
                await Open(connection, asynchronous); await Query(connection); await Close(connection, asynchronous);
                Assert.Equal(2, server.Accepted);
                clear.Invoke(null, new object[] { connection });
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(3, server.Accepted);
            }
            finally
            {
                if (connection != null) { connection.Close(); clear?.Invoke(null, new object[] { connection }); connection.Dispose(); }
                context.Unload();
            }
        }

        [Theory]
        [InlineData("net8.0", false)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", false)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", false)]
        [InlineData("net10.0", true)]
        public async Task ModernSourceClearsWithoutLosingItsCertificateSnapshot(string target, bool asynchronous)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var context = new AssemblyLoadContext("pool-clear-source-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var builderType = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                var builder = Activator.CreateInstance(builderType, Configuration(server.Port))!;
                builderType.GetMethod("UseClientCertificate")!.Invoke(builder, new object[] { certificate });
                RemoteCertificateValidationCallback validation = (_, _, _, _) => true;
                builderType.GetMethod("UseRemoteCertificateValidationCallback")!.Invoke(builder, new object[] { validation });
                using var source = (DbDataSource)builderType.GetMethod("Build")!.Invoke(builder, null)!;
                var sourceClear = source.GetType().GetMethod("Clear")!; Assert.NotNull(sourceClear);
                certificate.Dispose();
                using var connection = source.CreateConnection();
                using var unopened = source.CreateConnection();
                var type = connection.GetType();
                await Open(connection, asynchronous);
                sourceClear.Invoke(source, null);
                await Query(connection); await Close(connection, asynchronous);
                Assert.Equal(0, server.Resets);
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                type.GetMethod("ClearAllPools")!.Invoke(null, null);
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(2, server.Accepted);
                type.GetMethod("ClearPool")!.Invoke(null, new object[] { unopened });
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(3, server.Accepted);
            }
            finally { context.Unload(); }
        }

        private static DbConnection Connection(Type type, string configuration, object certificate)
        {
            var connection = (DbConnection)Activator.CreateInstance(type)!; connection.ConnectionString = configuration;
            type.GetProperty("ClientCertificate")!.SetValue(connection, certificate);
            RemoteCertificateValidationCallback validation = (_, _, _, _) => true;
            type.GetProperty("RemoteCertificateValidationCallback")!.SetValue(connection, validation);
            return connection;
        }
        private static Task Open(DbConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(DbConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
        private static async Task Query(DbConnection connection) { using var command = connection.CreateCommand(); command.CommandText = "selected"; Assert.Equal(1, await command.ExecuteNonQueryAsync()); }
        private static MutualTlsServer Server()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            return new MutualTlsServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!),
                channel => SessionTimeZoneFixture.ReadCommand(channel, CancellationToken.None, expectProbe: false));
        }
        private static string Configuration(int port) => new DbConnectionStringBuilder
        { ["Server"] = "127.0.0.1", ["Port"] = port, ["User ID"] = "u", ["TLS Server Name"] = "ob-loopback", ["Pooling"] = true, ["Max Pool Size"] = 1 }.ConnectionString;
    }
}
