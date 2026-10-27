using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class ClientCertificateConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var pooling in new[] { false, true }) yield return new object[] { target, asynchronous, pooling };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetAuthenticatesWithIsolatedClientCertificates(string target, bool asynchronous, bool pooling)
        {
            await using var server = Server();
            using var first = TestCertificateFactory.CreateSelfSigned("client-a");
            using var second = TestCertificateFactory.CreateSelfSigned("client-b");
            var configuration = Configuration(server.Port, pooling);
            var context = new AssemblyLoadContext("client-certificate-" + target, isCollectible: true);
            DbConnection? a = null, b = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                a = (DbConnection)Activator.CreateInstance(type)!; a.ConnectionString = configuration;
                b = (DbConnection)Activator.CreateInstance(type)!; b.ConnectionString = configuration;
                type.GetProperty("ClientCertificate")!.SetValue(a, first);
                type.GetProperty("ClientCertificate")!.SetValue(b, second);
                RemoteCertificateValidationCallback validate = (_, certificate, _, errors) => certificate != null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0;
                type.GetProperty("RemoteCertificateValidationCallback")!.SetValue(a, validate);
                type.GetProperty("RemoteCertificateValidationCallback")!.SetValue(b, validate);
                await Execute(a, asynchronous); await Execute(b, asynchronous); await Execute(a, asynchronous);
                Assert.Equal(pooling ? 2 : 3, server.AuthenticatedCertificates.Count);
                Assert.Contains(first.Thumbprint, server.AuthenticatedCertificates);
                Assert.Contains(second.Thumbprint, server.AuthenticatedCertificates);
                ClearPool(a, configuration); ClearPool(b, configuration);
            }
            finally
            {
                a?.Close(); b?.Close();
                if (a != null) ClearPool(a, configuration); if (b != null) ClearPool(b, configuration);
                a?.Dispose(); b?.Dispose(); context.Unload();
            }
        }

        [Theory]
        [InlineData("net10.0", false, false)]
        [InlineData("net10.0", false, true)]
        [InlineData("net10.0", true, false)]
        [InlineData("net10.0", true, true)]
        public async Task ModernSourcesOwnTheirClientCertificateSnapshot(string target, bool asynchronous, bool pooling)
        {
            await using var server = Server();
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var hash = certificate.Thumbprint;
            var context = new AssemblyLoadContext("client-certificate-source-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                var builder = Activator.CreateInstance(type, Configuration(server.Port, pooling))!;
                Assert.Same(builder, type.GetMethod("UseClientCertificate")!.Invoke(builder, new object?[] { certificate }));
                RemoteCertificateValidationCallback validate = (_, actual, _, errors) => actual != null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0;
                type.GetMethod("UseRemoteCertificateValidationCallback")!.Invoke(builder, new object?[] { validate });
                using var source = (DbDataSource)type.GetMethod("Build")!.Invoke(builder, null)!;
                certificate.Dispose();
                using var connection = source.CreateConnection();
                await Execute(connection, asynchronous); await Execute(connection, asynchronous);
                Assert.Equal(pooling ? 1 : 2, server.AuthenticatedCertificates.Count);
                Assert.All(server.AuthenticatedCertificates, actual => Assert.Equal(hash, actual));
            }
            finally { context.Unload(); }
        }

        private static async Task Execute(DbConnection connection, bool asynchronous)
        {
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "selected";
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
        }
        private static MutualTlsServer Server()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            return new MutualTlsServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!),
                channel => SessionTimeZoneFixture.ReadCommand(channel, CancellationToken.None, expectProbe: false));
        }
        private static string Configuration(int port, bool pooling) => new DbConnectionStringBuilder
        { ["Server"] = "127.0.0.1", ["Port"] = port, ["User ID"] = "u", ["TLS Server Name"] = "ob-loopback", ["Pooling"] = pooling, ["Max Pool Size"] = 1 }.ConnectionString;
        private static void ClearPool(DbConnection connection, string configuration)
        {
            var pool = connection.GetType().GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection);
            if (pool == null) return;
            ((IDisposable)pool).Dispose();
            connection.GetType().Assembly.GetType("OceanBaseConnector.Internal.Session.PhysicalSessionPoolRegistry")!
                .GetMethod("ReleaseIfEmpty", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { (object)configuration, pool });
        }
    }
}
