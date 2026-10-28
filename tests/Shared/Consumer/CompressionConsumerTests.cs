using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net.Security;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class CompressionConsumerTests
    {
        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetCompressesOverTcpAndTlsWithPoolReset(string target, bool oracle, bool asynchronous, bool tls)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new CompressionServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!), oracle, tls, authSwitch: true);
            var context = new AssemblyLoadContext("compression-" + target, isCollectible: true);
            DbConnection? connection = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={server.Port};User ID=u;Password=secret;Use Compression=true;Pooling=true;Max Pool Size=1;TLS Mode={(tls ? "VerifyFull" : "Disabled")}";
                if (tls)
                {
                    RemoteCertificateValidationCallback validate = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate!.Thumbprint;
                    type.GetProperty("RemoteCertificateValidationCallback")!.SetValue(connection, validate);
                }
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "select " + new string('z', 100_000);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                Assert.True(server.Compressed); Assert.Equal(1, server.Authentications);
                Assert.Equal(2, server.Queries); Assert.Equal(2, server.Resets); Assert.True(server.Pings >= 1);
            }
            finally
            {
                if (connection != null)
                {
                    connection.Close();
                    connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                    connection.Dispose();
                }
                context.Unload();
            }
        }
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var tls in new[] { OceanBaseConnector.Tests.TransportTestMode.Encrypted }) yield return new object[] { target, oracle, asynchronous, tls };
        }
    }
}
